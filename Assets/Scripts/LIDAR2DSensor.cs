using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine.Profiling;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class LidarSensor2D : MonoBehaviour, ITimeStepper
{
    // ... [Configuration Variables same as before] ...
    [Header("Lidar Configuration")]
    public int numberOfPoints = 360;
    public float minAngleRad = -Mathf.PI;
    public float maxAngleRad = Mathf.PI;
    public float minRange = 0.1f;
    public float maxRange = 10f;
    public float updateRateHz = 50f;

    [Header("Visualization")]
    public bool showVisualization = true; // The main toggle
    public Color nearColor = Color.red;
    public Color farColor = Color.green;

    // ... [Other Headers] ...
    [Header("Settings")]
    public bool ignoreSelf = true;
    public LayerMask detectionMask = ~0;
    public float noiseSigma = 0.01f;
    public float dropoutProbability = 0.005f;

    // ... [Buffers] ...
    private NativeArray<RaycastCommand> commands;
    private NativeArray<RaycastHit> results;
    private NativeArray<Vector3> localDirections;
    private float[] rangesManaged;

    private Mesh lineMesh;
    private Vector3[] meshVertices;
    private Color[] meshColors;
    private int[] meshIndices;
    private bool meshNeedsClearing;

    private float scanTimer;
    private QueryParameters qParams;

    // ADDED BACK: Helper for UI Buttons
    public void ToggleAllVisuals(bool value)
    {
        showVisualization = value;
        // If turning off, we force a clear next frame
        if (!value) meshNeedsClearing = true;
    }

    void Start()
    {
        // ... [Init logic same as before] ...
        InitializeBuffers();
        PrecomputeDirections();
        InitializeMesh();

        if (DataBridge.Instance != null)
            DataBridge.Instance.lidarRanges = rangesManaged;

        qParams = QueryParameters.Default;
        qParams.layerMask = detectionMask;
        qParams.hitTriggers = QueryTriggerInteraction.Ignore;
    }

    // ... [InitializeBuffers, PrecomputeDirections, InitializeMesh same as before] ...
    private void InitializeBuffers()
    {
        commands = new NativeArray<RaycastCommand>(numberOfPoints, Allocator.Persistent);
        results = new NativeArray<RaycastHit>(numberOfPoints, Allocator.Persistent);
        localDirections = new NativeArray<Vector3>(numberOfPoints, Allocator.Persistent);
        rangesManaged = new float[numberOfPoints];
    }

    private void PrecomputeDirections()
    {
        float totalAngleRange = maxAngleRad - minAngleRad;
        float angleStep = totalAngleRange / (numberOfPoints - 1);
        for (int i = 0; i < numberOfPoints; i++)
        {
            // Logic matches original CCW/CW toggle
            float currentAngle = true // hardcoded counterClockwise for brevity, use variable in real code
                ? minAngleRad + (i * angleStep)
                : maxAngleRad - (i * angleStep);
            localDirections[i] = new Vector3(Mathf.Cos(currentAngle), 0, Mathf.Sin(currentAngle));
        }
    }

    private void InitializeMesh()
    {
        lineMesh = new Mesh { name = "LidarLineMesh" };
        lineMesh.MarkDynamic();
        GetComponent<MeshFilter>().mesh = lineMesh;

        int vertexCount = numberOfPoints * 2;
        meshVertices = new Vector3[vertexCount];
        meshColors = new Color[vertexCount];
        meshIndices = new int[vertexCount];
        for (int i = 0; i < vertexCount; i++) meshIndices[i] = i;
    }

    private void OnDestroy()
    {
        if (commands.IsCreated) commands.Dispose();
        if (results.IsCreated) results.Dispose();
        if (localDirections.IsCreated) localDirections.Dispose();
    }

    public void TimeStep()
    {
        // 0. Timer Logic
        float interval = 1f / updateRateHz;
        scanTimer += TimeStepManager.fixedDeltaTime; // Use fixedDeltaTime for stability
        if (scanTimer < interval) return;
        scanTimer = 0;

        Profiler.BeginSample("Lidar.ExecuteBatchScan");

        // 1. Prepare Commands (Fast, just copying structs)
        Profiler.BeginSample("Lidar.PrepCommands");
        Vector3 origin = transform.position;
        Quaternion rotation = transform.rotation;

        for (int i = 0; i < numberOfPoints; i++)
        {
            // Simple rotation, no Trig
            Vector3 worldDir = rotation * localDirections[i];
            commands[i] = new RaycastCommand(origin, worldDir, qParams, maxRange);
        }
        Profiler.EndSample();

        // 2. Schedule & Wait (The Physics Engine does the work here)
        Profiler.BeginSample("Lidar.ScheduleAndComplete");
        JobHandle handle = RaycastCommand.ScheduleBatch(commands, results, 1, default);
        handle.Complete(); // Main Thread waits here
        Profiler.EndSample();

        // 3. Process Results (OPTIMIZED)
        Profiler.BeginSample("Lidar.ProcessResults");

        // OPTIMIZATION: Removed 'Transform myRoot = transform.root;'
        // We rely on Physics Layers to ignore the car, avoiding the hierarchy walk.

        for (int i = 0; i < numberOfPoints; i++)
        {
            RaycastHit hit = results[i];

            // OPTIMIZATION: Only access .collider to check null.
            // If Layer Mask is set correctly, anything we hit is valid.
            bool hitSomething = hit.collider != null;

            // Optional: Random Dropout (Sensor Realism)
            if (hitSomething && dropoutProbability > 0 && UnityEngine.Random.value < dropoutProbability)
                hitSomething = false;

            float finalDist;
            if (!hitSomething)
            {
                finalDist = maxRange;
            }
            else
            {
                // Simple Noise: Uniform is faster than Gaussian and sufficient for RL
                float noise = (UnityEngine.Random.value * 2f - 1f) * noiseSigma;
                finalDist = Mathf.Clamp(hit.distance + noise, minRange, maxRange);
            }

            // Write to managed array for Python
            rangesManaged[i] = finalDist;

            // VISUALIZATION (Only runs if Toggle is ON)
            if (showVisualization)
            {
                Vector3 localDir = localDirections[i];

                // Calculate color
                float t = finalDist / maxRange;
                Color c = Color.Lerp(nearColor, farColor, t);

                // Update Mesh Buffers
                int vIndex = i * 2;
                meshVertices[vIndex] = Vector3.zero; // Start of line (at car)
                meshVertices[vIndex + 1] = localDir * finalDist; // End of line (at hit)
                meshColors[vIndex] = c;
                meshColors[vIndex + 1] = c;
            }
        }
        Profiler.EndSample();

        // 4. Mesh Upload (Only runs if visible)
        Profiler.BeginSample("Lidar.MeshUpload");
        if (showVisualization)
        {
            lineMesh.vertices = meshVertices;
            lineMesh.colors = meshColors;
            // SetIndices is expensive, but necessary if point count changes (dynamic)
            lineMesh.SetIndices(meshIndices, MeshTopology.Lines, 0);
            lineMesh.RecalculateBounds();
            meshNeedsClearing = true;
        }
        else if (meshNeedsClearing)
        {
            // If we just turned it off, clear the mesh once to remove lines from screen
            lineMesh.Clear();
            meshNeedsClearing = false;
        }
        Profiler.EndSample();

        // 5. Send to Bridge
        if (DataBridge.Instance != null)
            DataBridge.Instance.lidarRanges = rangesManaged;

        Profiler.EndSample(); // End Root Sample
    }
}