using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

public enum LidarScanDirection
{
    CounterClockwise, // Standard Math (Right -> Front -> Left)
    Clockwise         // (Left -> Front -> Right)
}

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class LidarSensor2D : MonoBehaviour, ITimeStepper
{
    // --- Configuration ---
    [Header("Lidar Configuration")]
    public int numberOfPoints = 1080;
    public float minAngleRad = -Mathf.PI;
    public float maxAngleRad = Mathf.PI;
    public float minRange = 0.1f;
    public float maxRange = 20f;
    public LidarScanDirection scanDirection = LidarScanDirection.CounterClockwise;

    [Header("Visualization")]
    public bool showVisualization = true;
    public Color nearColor = Color.red;
    public Color farColor = Color.green;

    [Header("Settings")]
    public LayerMask detectionMask = ~0;
    public float noiseSigma = 0.01f;
    [Range(0, 1f)]
    public float dropoutProbability = 0.005f;

    // --- Buffers ---
    private NativeArray<RaycastCommand> commands;
    private NativeArray<RaycastHit> results;
    private NativeArray<float3> localDirections;
    private NativeArray<float> rangesNative;

    private NativeArray<Vector3> meshVerticesNative;
    private NativeArray<Color> meshColorsNative;

    private float[] rangesManaged;
    private int[] meshIndices;

    private Mesh lineMesh;
    private QueryParameters qParams;
    private bool meshNeedsClearing;

    public void ToggleAllVisuals(bool value)
    {
        showVisualization = value;
        if (!value) meshNeedsClearing = true;
    }

    void Start()
    {
        InitializeBuffers();
        PrecomputeDirections();
        InitializeMesh();
        EnsureMaterial();

        qParams = QueryParameters.Default;
        qParams.layerMask = detectionMask;
        qParams.hitTriggers = QueryTriggerInteraction.Ignore;
    }

    private void EnsureMaterial()
    {
        var mr = GetComponent<MeshRenderer>();
        if (mr.sharedMaterial == null)
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader != null)
                mr.sharedMaterial = new Material(shader);
        }
    }

    private void InitializeBuffers()
    {
        commands = new NativeArray<RaycastCommand>(numberOfPoints, Allocator.Persistent);
        results = new NativeArray<RaycastHit>(numberOfPoints, Allocator.Persistent);
        localDirections = new NativeArray<float3>(numberOfPoints, Allocator.Persistent);
        rangesNative = new NativeArray<float>(numberOfPoints, Allocator.Persistent);

        meshVerticesNative = new NativeArray<Vector3>(numberOfPoints * 2, Allocator.Persistent);
        meshColorsNative = new NativeArray<Color>(numberOfPoints * 2, Allocator.Persistent);

        rangesManaged = new float[numberOfPoints];
    }

    // --- UPDATED: Handles Scan Direction ---
    private void PrecomputeDirections()
    {
        float startAngle = minAngleRad;
        float endAngle = maxAngleRad;

        // If Clockwise, we simply swap the start and end logic for the loop
        if (scanDirection == LidarScanDirection.CounterClockwise)
        {
            startAngle = maxAngleRad;
            endAngle = minAngleRad;
        }

        float totalAngleRange = endAngle - startAngle;
        float angleStep = totalAngleRange / (numberOfPoints - 1);

        for (int i = 0; i < numberOfPoints; i++)
        {
            float currentAngle = startAngle + (i * angleStep);

            // Standard Math: 0 is East (Right), PI/2 is North (Forward)
            // Assuming the car Forward is +Z, Right is +X in Unity world space.
            // But usually for 2D Lidars in Unity:
            // Sin/Cos on (X,Z) plane:
            // Cos(0) = 1 (X=1), Sin(0) = 0 (Z=0) -> World Right
            // Cos(90) = 0 (X=0), Sin(90) = 1 (Z=1) -> World Forward
            localDirections[i] = new float3(Mathf.Cos(currentAngle), 0, Mathf.Sin(currentAngle));
        }
    }

    private void InitializeMesh()
    {
        lineMesh = new Mesh { name = "LidarLineMesh" };
        lineMesh.MarkDynamic();
        GetComponent<MeshFilter>().mesh = lineMesh;

        meshIndices = new int[numberOfPoints * 2];
        for (int i = 0; i < meshIndices.Length; i++) meshIndices[i] = i;

        var mr = GetComponent<MeshRenderer>();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
    }

    private void OnDestroy()
    {
        if (commands.IsCreated) commands.Dispose();
        if (results.IsCreated) results.Dispose();
        if (localDirections.IsCreated) localDirections.Dispose();
        if (rangesNative.IsCreated) rangesNative.Dispose();
        if (meshVerticesNative.IsCreated) meshVerticesNative.Dispose();
        if (meshColorsNative.IsCreated) meshColorsNative.Dispose();
    }

    // --- BURST JOB ---
    [BurstCompile]
    struct ProcessLidarJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<RaycastHit> hits;
        [ReadOnly] public NativeArray<float3> directions;
        [WriteOnly] public NativeArray<float> ranges;

        [NativeDisableParallelForRestriction][WriteOnly] public NativeArray<Vector3> verts;
        [NativeDisableParallelForRestriction][WriteOnly] public NativeArray<Color> colors;

        public bool generateMesh;
        public float minRange;
        public float maxRange;
        public float noiseSigma;
        public float dropoutProb;
        public uint seed;
        public float4 colorNear;
        public float4 colorFar;

        public void Execute(int index)
        {
            var hit = hits[index];
            bool hitSomething = hit.colliderEntityId != 0;
            var rng = new Unity.Mathematics.Random(seed + (uint)index + 1);

            if (hitSomething && dropoutProb > 0 && rng.NextFloat() < dropoutProb)
                hitSomething = false;

            float dist;
            if (!hitSomething)
            {
                dist = maxRange;
            }
            else
            {
                float noise = (rng.NextFloat() * 2f - 1f) * noiseSigma;
                dist = math.clamp(hit.distance + noise, minRange, maxRange);
            }

            ranges[index] = dist;

            if (generateMesh)
            {
                int vIndex = index * 2;
                float t = dist / maxRange;
                float4 cVec = math.lerp(colorNear, colorFar, t);
                Color finalColor = new Color(cVec.x, cVec.y, cVec.z, cVec.w);
                float3 dir = directions[index];

                verts[vIndex] = Vector3.zero;
                verts[vIndex + 1] = new Vector3(dir.x * dist, dir.y * dist, dir.z * dist);
                colors[vIndex] = finalColor;
                colors[vIndex + 1] = finalColor;
            }
        }
    }

    public void TimeStep()
    {
        Profiler.BeginSample("Lidar.TotalScanTime");

        // 1. Setup Commands
        Vector3 origin = transform.position;
        Quaternion rotation = transform.rotation;

        for (int i = 0; i < numberOfPoints; i++)
        {
            // Apply rotation to the precomputed local direction
            Vector3 worldDir = rotation * (Vector3)localDirections[i];
            commands[i] = new RaycastCommand(origin, worldDir, qParams, maxRange);
        }

        // 2. Run Physics
        JobHandle raycastHandle = RaycastCommand.ScheduleBatch(commands, results, 1, default);

        // 3. Run Processing
        ProcessLidarJob processJob = new ProcessLidarJob
        {
            hits = results,
            directions = localDirections,
            ranges = rangesNative,
            verts = meshVerticesNative,
            colors = meshColorsNative,
            generateMesh = showVisualization,
            minRange = minRange,
            maxRange = maxRange,
            noiseSigma = noiseSigma,
            dropoutProb = dropoutProbability,
            seed = (uint)(Time.frameCount),
            colorNear = new float4(nearColor.r, nearColor.g, nearColor.b, nearColor.a),
            colorFar = new float4(farColor.r, farColor.g, farColor.b, farColor.a)
        };

        JobHandle processHandle = processJob.Schedule(numberOfPoints, 64, raycastHandle);
        processHandle.Complete();

        // 4. Data Bridge (FIXED)
        NativeArray<float>.Copy(rangesNative, rangesManaged, numberOfPoints);

        if (DataBridge.Instance != null)
        {
            DataBridge.Instance.lidarRanges = rangesManaged;
        }

        // 5. Visualization logic
        if (showVisualization)
        {
            lineMesh.SetVertices(meshVerticesNative);
            lineMesh.SetColors(meshColorsNative);
            lineMesh.SetIndices(meshIndices, MeshTopology.Lines, 0);
            lineMesh.RecalculateBounds();
            meshNeedsClearing = true;
        }
        else if (meshNeedsClearing)
        {
            lineMesh.Clear();
            meshNeedsClearing = false;
        }

        Profiler.EndSample();
    }

    // Allow validation in editor to recompute if settings change
    private void OnValidate()
    {
        if (Application.isPlaying && localDirections.IsCreated)
        {
            PrecomputeDirections();
        }
    }
}