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

    [Header("Sensor Realism")]
    public float noiseSigma = 0.01f;
    [Range(0, 0.1f)]
    public float dropoutProbability = 0.005f;

    [Header("Visualization Colors")]
    public Color nearColor = Color.red;
    public Color farColor = Color.green;

    [Header("Settings")]
    public bool showVisualization = true;
    public bool ignoreSelf = true;
    public LayerMask detectionMask = ~0;

    private float scanTimer;
    private NativeArray<RaycastCommand> commands;
    private NativeArray<RaycastHit> results;
    private float[] rangesManaged;

    private Mesh lineMesh;
    private Vector3[] meshVertices;
    private Color[] meshColors; // Added for vertex coloring
    private int[] meshIndices;
    private bool meshNeedsClearing;

    void Start()
    {
        commands = new NativeArray<RaycastCommand>(numberOfPoints, Allocator.Persistent);
        results = new NativeArray<RaycastHit>(numberOfPoints, Allocator.Persistent);
        rangesManaged = new float[numberOfPoints];

        lineMesh = new Mesh { name = "LidarLineMesh" };
        GetComponent<MeshFilter>().mesh = lineMesh;

        if (DataBridge.Instance != null)
            DataBridge.Instance.lidarRanges = rangesManaged;

        PrepareMeshBuffers();
    }

    private void PrepareMeshBuffers()
    {
        int vertexCount = numberOfPoints * 2;
        meshVertices = new Vector3[vertexCount];
        meshColors = new Color[vertexCount];
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

        QueryParameters qParams = QueryParameters.Default;
        qParams.layerMask = detectionMask;
        qParams.hitTriggers = QueryTriggerInteraction.Ignore;

        for (int i = 0; i < numberOfPoints; i++)
        {
            float currentAngle = minAngleRad + (i * angleStep);
            Vector3 worldDir = rotation * new Vector3(Mathf.Cos(currentAngle), 0, Mathf.Sin(currentAngle));
            commands[i] = new RaycastCommand(origin, worldDir, qParams, maxRange);
        }

        JobHandle handle = RaycastCommand.ScheduleBatch(commands, results, 1, default);
        handle.Complete();

        for (int i = 0; i < numberOfPoints; i++)
        {
            RaycastHit hit = results[i];
            bool noHit = hit.collider == null ||
                         (ignoreSelf && hit.collider.transform.root == transform.root) ||
                         (Random.value < dropoutProbability);

            if (noHit)
            {
                rangesManaged[i] = float.PositiveInfinity;
            }
            else
            {
                float rawDist = Mathf.Max(hit.distance, minRange);
                float noise = ApplyGaussianNoise(0, noiseSigma);
                rangesManaged[i] = Mathf.Clamp(rawDist + noise, minRange, maxRange);
            }

            if (showVisualization)
            {
                float drawDist = float.IsInfinity(rangesManaged[i]) ? maxRange : rangesManaged[i];
                float currentAngle = minAngleRad + (i * angleStep);

                // Color mapping (0 = nearColor, 1 = farColor)
                float t = Mathf.InverseLerp(minRange, maxRange, drawDist);
                Color rayColor = Color.Lerp(nearColor, farColor, t);

                // Origin point
                meshVertices[i * 2] = Vector3.zero;
                meshColors[i * 2] = rayColor;

                // Hit point
                meshVertices[i * 2 + 1] = new Vector3(Mathf.Cos(currentAngle) * drawDist, 0, Mathf.Sin(currentAngle) * drawDist);
                meshColors[i * 2 + 1] = rayColor;
            }
        }

        if (showVisualization)
        {
            lineMesh.vertices = meshVertices;
            lineMesh.colors = meshColors; // Send colors to GPU
            lineMesh.SetIndices(meshIndices, MeshTopology.Lines, 0);
            lineMesh.RecalculateBounds();
            meshNeedsClearing = true;
        }
        else if (meshNeedsClearing)
        {
            ClearVisuals();
        }

        if (DataBridge.Instance != null)
            DataBridge.Instance.lidarRanges = rangesManaged;
    }

    private float ApplyGaussianNoise(float mean, float stdDev)
    {
        float u1 = 1.0f - Random.value;
        float u2 = 1.0f - Random.value;
        return mean + stdDev * Mathf.Sqrt(-2.0f * Mathf.Log(u1)) * Mathf.Sin(2.0f * Mathf.PI * u2);
    }

    private void ClearVisuals()
    {
        if (lineMesh != null)
        {
            lineMesh.Clear();
            meshNeedsClearing = false;
        }
    }

    public void ToggleVisualization(bool state)
    {
        showVisualization = state;
        if (!state) ClearVisuals();
    }
}