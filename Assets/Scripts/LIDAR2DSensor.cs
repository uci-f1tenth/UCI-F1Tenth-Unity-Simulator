using UnityEngine;
using Unity.Collections;
using Unity.Jobs;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class LidarSensor2D : MonoBehaviour
{
    [Header("Lidar Configuration")]
    public int numberOfPoints = 360;
    public float minAngleRad = -Mathf.PI;
    public float maxAngleRad = Mathf.PI;
    public float minRange = 0.1f;
    public float maxRange = 10f;
    public float updateRateHz = 20f;

    [Header("Settings")]
    public bool showVisualization = true;
    public bool ignoreSelf = true;
    public LayerMask detectionMask = ~0;

    // Internal Physics Data
    private float scanTimer;
    private NativeArray<RaycastCommand> commands;
    private NativeArray<RaycastHit> results;
    private float[] rangesManaged;

    // Internal Mesh Data (Visualization)
    private Mesh lineMesh;
    private Vector3[] meshVertices;
    private int[] meshIndices;

    void Start()
    {
        // 1. Initialize Physics Arrays (Unmanaged memory for Job System)
        commands = new NativeArray<RaycastCommand>(numberOfPoints, Allocator.Persistent);
        results = new NativeArray<RaycastHit>(numberOfPoints, Allocator.Persistent);
        rangesManaged = new float[numberOfPoints];

        // 2. Initialize Mesh for Visualization
        lineMesh = new Mesh { name = "LidarLineMesh" };
        GetComponent<MeshFilter>().mesh = lineMesh;

        // 3. Setup DataBridge Reference
        if (DataBridge.Instance != null)
            DataBridge.Instance.lidarRanges = rangesManaged;

        PrepareMeshBuffers();
    }

    private void PrepareMeshBuffers()
    {
        int vertexCount = numberOfPoints * 2;
        meshVertices = new Vector3[vertexCount];
        meshIndices = new int[vertexCount];
        for (int i = 0; i < vertexCount; i++) meshIndices[i] = i;
    }

    void OnDestroy()
    {
        if (commands.IsCreated) commands.Dispose();
        if (results.IsCreated) results.Dispose();
    }

    void FixedUpdate()
    {
        float interval = 1f / updateRateHz;
        scanTimer += Time.fixedDeltaTime;

        if (scanTimer >= interval)
        {
            ExecuteBatchLidarScan();
            scanTimer -= interval;
        }
    }

    private void ExecuteBatchLidarScan()
    {
        Vector3 origin = transform.position;
        Quaternion rotation = transform.rotation;
        float angleStep = (maxAngleRad - minAngleRad) / (numberOfPoints - 1);

        // --- Unity 6 Query Parameters ---
        QueryParameters qParams = QueryParameters.Default;
        qParams.layerMask = detectionMask;
        qParams.hitTriggers = QueryTriggerInteraction.Ignore;

        // 1. Prepare Batch Commands
        for (int i = 0; i < numberOfPoints; i++)
        {
            float currentAngle = minAngleRad + (i * angleStep);
            Vector3 worldDir = rotation * new Vector3(Mathf.Cos(currentAngle), 0, Mathf.Sin(currentAngle));
            commands[i] = new RaycastCommand(origin, worldDir, qParams, maxRange);
        }

        // 2. Schedule and Run Jobs (Multithreaded Raycasting)
        JobHandle handle = RaycastCommand.ScheduleBatch(commands, results, 1, default);
        handle.Complete();

        // 3. Process Results & Update Visualization Buffers
        for (int i = 0; i < numberOfPoints; i++)
        {
            RaycastHit hit = results[i];
            bool noHit = hit.collider == null || (ignoreSelf && hit.collider.transform.root == transform.root);

            rangesManaged[i] = noHit ? float.PositiveInfinity : Mathf.Max(hit.distance, minRange);

            if (showVisualization)
            {
                float drawDist = noHit ? maxRange : rangesManaged[i];
                float currentAngle = minAngleRad + (i * angleStep);

                // Line Start (Origin)
                meshVertices[i * 2] = Vector3.zero;
                // Line End (Hit Point in local space)
                meshVertices[i * 2 + 1] = new Vector3(Mathf.Cos(currentAngle) * drawDist, 0, Mathf.Sin(currentAngle) * drawDist);
            }
        }

        // 4. Update Visuals (Single Draw Call)
        if (showVisualization)
        {
            lineMesh.vertices = meshVertices;
            lineMesh.SetIndices(meshIndices, MeshTopology.Lines, 0);
            lineMesh.RecalculateBounds();
        }
        else if (lineMesh.vertexCount > 0)
        {
            lineMesh.Clear();
        }

        // 5. Update DataBridge
        if (DataBridge.Instance != null)
            DataBridge.Instance.lidarRanges = rangesManaged;
    }

    public void ToggleVisualization(bool state)
    {
        showVisualization = state;
        if (!state) lineMesh.Clear();
    }
}