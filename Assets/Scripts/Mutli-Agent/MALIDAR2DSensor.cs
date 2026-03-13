using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

public enum MALidarScanDirection
{
    CounterClockwise, // Standard Math (Right -> Front -> Left)
    Clockwise         // (Left -> Front -> Right)
}

/// <summary>
/// Multi-Agent Lidar Sensor.
/// Performs highly optimized raycasts via Burst and writes raw float distances to MADataHub.
/// Contains ZERO visual or rendering code to maximize training performance.
/// </summary>
public class MALIDAR2DSensor : MonoBehaviour, ITimeStepper
{
    [Header("--- Lidar Configuration is Located in Data Hub ---")]

    [Header("--- Settings ---")]
    public LayerMask detectionMask = ~0;
    public float noiseSigma = 0.01f;
    [Range(0, 1f)] public float dropoutProbability = 0.005f;

    // --- System References ---
    private MAVehicleController _vehicleController;
    private MADataHub _dataHub;
    private MATimeStepManager _timeManager;

    // --- Native Buffers (Physics) ---
    private NativeArray<RaycastCommand> _commands;
    private NativeArray<RaycastHit> _results;
    private NativeArray<float3> _localDirections;
    private NativeArray<float> _rangesNative;
    private float[] _rangesManaged; // Buffer to pass to Hub
    private QueryParameters _qParams;

    // --- State ---
    private float _nextUpdateTime;
    private int _resolution;

    // =================================================================
    //                      INITIALIZATION
    // =================================================================
    void Start()
    {
        _dataHub = MADataHub.Instance;
        _timeManager = MATimeStepManager.Instance;
        _vehicleController = GetComponentInParent<MAVehicleController>();

        // Grab resolution dynamically from the Hub so it always matches
        _resolution = _dataHub.lidarResolution;

        InitializeBuffers();
        PrecomputeDirections();

        _qParams = QueryParameters.Default;
        _qParams.layerMask = detectionMask;
        _qParams.hitTriggers = QueryTriggerInteraction.Ignore;
    }

    private void InitializeBuffers()
    {
        _commands = new NativeArray<RaycastCommand>(_resolution, Allocator.Persistent);
        _results = new NativeArray<RaycastHit>(_resolution, Allocator.Persistent);
        _localDirections = new NativeArray<float3>(_resolution, Allocator.Persistent);
        _rangesNative = new NativeArray<float>(_resolution, Allocator.Persistent);
        _rangesManaged = new float[_resolution];
    }

    private void PrecomputeDirections()
    {
        // PULL DIRECTLY FROM HUB AND CONVERT TO RADIANS
        float startAngle = _dataHub.lidarMinAngleDeg * Mathf.Deg2Rad;
        float endAngle = _dataHub.lidarMaxAngleDeg * Mathf.Deg2Rad;

        if (_dataHub.lidarScanDirection == MALidarScanDirection.Clockwise)
        {
            startAngle = _dataHub.lidarMaxAngleDeg * Mathf.Deg2Rad;
            endAngle = _dataHub.lidarMinAngleDeg * Mathf.Deg2Rad;
        }

        float angleStep = (endAngle - startAngle) / (_resolution - 1);

        for (int i = 0; i < _resolution; i++)
        {
            float currentAngle = startAngle + (i * angleStep);
            _localDirections[i] = new float3(Mathf.Cos(currentAngle), 0, Mathf.Sin(currentAngle));
        }
    }

    private void OnDestroy()
    {
        if (_commands.IsCreated) _commands.Dispose();
        if (_results.IsCreated) _results.Dispose();
        if (_localDirections.IsCreated) _localDirections.Dispose();
        if (_rangesNative.IsCreated) _rangesNative.Dispose();
    }

    // =================================================================
    //                      CORE DATA LOOP
    // =================================================================
    public void TimeStep()
    {
        if (_dataHub == null || _vehicleController == null) return;

        // Apply realistic update rate (Jitter simulated)
        if (_dataHub.lidarUpdateRateHz > 0f && _timeManager.fixedTime < _nextUpdateTime) return;

        Profiler.BeginSample("MALidar.TotalScanTime");

        int agentIdx = _vehicleController.agentIndex;
        Vector3 origin = transform.position;
        Quaternion rotation = transform.rotation;

        // 1. Setup Raycast Commands
        for (int i = 0; i < _resolution; i++)
        {
            Vector3 worldDir = rotation * (Vector3)_localDirections[i];
            _commands[i] = new RaycastCommand(origin, worldDir, _qParams, _dataHub.lidarMaxRange); // Hub Max Range
        }

        // 2. Schedule Physics Raycasts
        JobHandle raycastHandle = RaycastCommand.ScheduleBatch(_commands, _results, 64, default);

        // 3. Schedule Processing (Noise & Dropout)
        ProcessLidarJob processJob = new ProcessLidarJob
        {
            hits = _results,
            ranges = _rangesNative,
            minRange = _dataHub.lidarMinRange, // Hub Min Range
            maxRange = _dataHub.lidarMaxRange, // Hub Max Range
            noiseSigma = noiseSigma,
            dropoutProb = dropoutProbability,
            seed = (uint)(Time.frameCount + agentIdx + 1) // Unique seed per agent
        };

        JobHandle processHandle = processJob.Schedule(_resolution, 64, raycastHandle);
        processHandle.Complete(); // Must complete now so RL model gets data immediately

        // 4. Bridge Data to Hub
        NativeArray<float>.Copy(_rangesNative, _rangesManaged, _resolution);
        _rangesManaged.CopyTo(_dataHub.lidarRanges[agentIdx], 0);

        Profiler.EndSample();

        // Schedule next scan
        if (_dataHub.lidarUpdateRateHz > 0f)
            _nextUpdateTime = _timeManager.fixedTime + (1f / _dataHub.lidarUpdateRateHz);
    }

    // =================================================================
    //                      BURST JOBS
    // =================================================================
    [BurstCompile]
    struct ProcessLidarJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<RaycastHit> hits;
        [WriteOnly] public NativeArray<float> ranges;

        public float minRange;
        public float maxRange;
        public float noiseSigma;
        public float dropoutProb;
        public uint seed;

        public void Execute(int index)
        {
            var hit = hits[index];
            bool hitSomething = hit.colliderEntityId != 0;
            var rng = new Unity.Mathematics.Random(seed + (uint)index);

            if (hitSomething && dropoutProb > 0 && rng.NextFloat() < dropoutProb)
                hitSomething = false;

            if (!hitSomething)
            {
                ranges[index] = maxRange;
            }
            else
            {
                float noise = (rng.NextFloat() * 2f - 1f) * noiseSigma;
                ranges[index] = math.clamp(hit.distance + noise, minRange, maxRange);
            }
        }
    }
}