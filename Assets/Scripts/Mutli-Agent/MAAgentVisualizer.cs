using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using System.Collections.Generic;
using TMPro;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Burst;

public class MAAgentVisualizer : MonoBehaviour
{
    public static MAAgentVisualizer Instance { get; private set; }

    // =================================================================
    //                          STRUCTS & ENUMS
    // =================================================================

    [System.Serializable]
    public class CameraPreset
    {
        public string name = "New View";
        public CameraMode mode = CameraMode.Chase;
        public Vector3 localPosition = new Vector3(0, 2, -5);
        public Vector3 lookAtOffset = Vector3.zero;
        public Transform staticAnchor;
    }

    public enum CameraMode { Chase, Static }

    // =================================================================
    //                        INSPECTOR VARIABLES
    // =================================================================

    [Header("--- Camera Control ---")]
    public Camera mainCamera;
    public Button cameraModeNextButton;
    public Button cameraModePrevButton;
    public TextMeshProUGUI cameraModeText;
    [Range(0f, 100f)] public float smoothSpeed = 10f;
    public List<CameraPreset> cameraViews = new List<CameraPreset>();

    [Header("--- Visuals UI ---")]
    public Toggle masterToggle;
    public Toggle lidarToggle;

    [Header("--- Line Visualization ---")]
    public float velocityScale = 0.5f;
    public float accelScale = 0.2f;
    public float steeringLineLength = 2.0f;
    public Color colorVelocity = Color.blue;
    public Color colorAccel = Color.red;
    public Color colorForward = Color.yellow;
    public Color colorSteer = Color.green;

    [Header("--- Lidar Visualization ---")]
    public bool showLidar = true;
    public Color nearColor = Color.red;
    public Color farColor = Color.green;

    // =================================================================
    //                      INTERNAL STATE VARIABLES
    // =================================================================

    // Camera State
    private int _currentCamIndex = 0;
    private CameraPreset _activeView;

    // Render & Agent State
    private Material _lineMaterial;
    private MAVehicleController _agentToDraw;

    // Cached for performance to avoid GetComponentsInChildren every frame
    private WheelCollider[] _cachedWheels;
    private Transform _cachedLidarTransform; // CACHED: Lidar Sensor Transform
    private int _frameDrawRequest;

    // Lidar Mesh State
    private Mesh _lidarMesh;
    private GameObject _lidarRenderObj;
    private NativeArray<float> _lidarRangesNative;
    private NativeArray<Vector3> _meshVertsNative;
    private NativeArray<Color> _meshColorsNative;
    private int[] _meshIndices;
    private int _lidarResolution;

    // =================================================================
    //                 UNITY LIFECYCLE (INIT & TEARDOWN)
    // =================================================================

    private void Awake()
    {
        // Enforce Singleton pattern
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        if (mainCamera == null) mainCamera = Camera.main;

        // Ensure there is at least one camera view available
        if (cameraViews.Count == 0) cameraViews.Add(new CameraPreset { name = "Default Chase" });
        if (cameraModeNextButton != null) cameraModeNextButton.onClick.AddListener(NextCamera);
        if (cameraModePrevButton != null) cameraModePrevButton.onClick.AddListener(PrevCamera);

        // This ensures the mesh clears immediately when toggled off
        if (lidarToggle != null)
        {
            lidarToggle.isOn = showLidar; // Sync UI state with initial variable
            lidarToggle.onValueChanged.AddListener(val => {
                showLidar = val;
                if (!val) _lidarMesh.Clear();
            });
        }

        UpdateActiveView();
        InitializeLidarMesh();
        CreateLineMaterial();
    }

    private void InitializeLidarMesh()
    {
        // Create a dedicated object to hold the Lidar lines
        _lidarRenderObj = new GameObject("Visualizer_LidarMesh");
        _lidarRenderObj.transform.SetParent(this.transform);

        var mf = _lidarRenderObj.AddComponent<MeshFilter>();
        var mr = _lidarRenderObj.AddComponent<MeshRenderer>();

        Shader shader = Shader.Find("Sprites/Default");
        if (shader != null) mr.sharedMaterial = new Material(shader);

        // Optimize renderer since this is just for debug lines
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;

        _lidarMesh = new Mesh { name = "LidarLineMesh" };
        _lidarMesh.MarkDynamic();
        mf.mesh = _lidarMesh;
    }

    private void OnEnable() { RenderPipelineManager.endContextRendering += OnEndContextRendering; }
    private void OnDisable() { RenderPipelineManager.endContextRendering -= OnEndContextRendering; }

    private void OnDestroy()
    {
        DisposeBuffers();
    }

    // Safely cleanup native arrays to prevent memory leaks
    private void DisposeBuffers()
    {
        if (_lidarRangesNative.IsCreated) _lidarRangesNative.Dispose();
        if (_meshVertsNative.IsCreated) _meshVertsNative.Dispose();
        if (_meshColorsNative.IsCreated) _meshColorsNative.Dispose();
    }

    private void LateUpdate()
    {
        if (mainCamera == null || _agentToDraw == null || _activeView == null) return;

        _frameDrawRequest = Time.frameCount;

        // Ensure the visual representation of wheels matches physics state
        _agentToDraw.SyncWheels();

        // Handle camera tracking based on selected mode
        if (_activeView.mode == CameraMode.Chase) HandleChaseCamera();
        else if (_activeView.mode == CameraMode.Static) HandleStaticCamera();

        UpdateLidarMesh();
    }

    // =================================================================
    //                     PUBLIC API & CAMERA LOGIC
    // =================================================================

    // Call this from your UI or Manager to switch which agent is being observed
    public void DrawVisuals(MAVehicleController agent)
    {
        if (agent == null) return;
        _agentToDraw = agent;

        // CACHE THE WHEELS HERE
        // Prevents massive framerate drop in rendering loop by avoiding Find/GetComponents
        _cachedWheels = _agentToDraw.GetComponentsInChildren<WheelCollider>();

        // CACHE THE LIDAR TRANSFORM
        // Look for the MALIDAR2DSensor component to get the exact origin and rotation
        var lidarSensor = _agentToDraw.GetComponentInChildren<MALIDAR2DSensor>();
        if (lidarSensor != null)
        {
            _cachedLidarTransform = lidarSensor.transform;
        }
        else
        {
            // Fallback just in case the sensor is missing
            _cachedLidarTransform = _agentToDraw.transform;
            Debug.LogWarning($"Visualizer: No MALIDAR2DSensor found on agent {_agentToDraw.name}. Lidar mesh will render at agent root.");
        }
    }

    private void NextCamera() { _currentCamIndex = (_currentCamIndex + 1) % cameraViews.Count; UpdateActiveView(); }
    private void PrevCamera() { _currentCamIndex--; if (_currentCamIndex < 0) _currentCamIndex = cameraViews.Count - 1; UpdateActiveView(); }

    private void UpdateActiveView()
    {
        _activeView = cameraViews[_currentCamIndex];
        if (cameraModeText != null) cameraModeText.text = _activeView.name;
    }

    private void HandleChaseCamera()
    {
        if (_agentToDraw == null) return;

        Transform target = _agentToDraw.transform;
        Transform camTransform = mainCamera.transform;

        // 1. Calculate Target Position
        // If staticAnchor exists, we use world space offset; otherwise, local relative offset
        Vector3 targetPos = (_activeView.staticAnchor != null)
            ? target.position + _activeView.localPosition
            : target.TransformPoint(_activeView.localPosition);

        // 2. Calculate Target Rotation
        Quaternion targetRot;
        if (_activeView.staticAnchor != null)
        {
            targetRot = _activeView.staticAnchor.rotation;
        }
        else
        {
            Vector3 lookAtTarget = target.TransformPoint(_activeView.lookAtOffset);
            Vector3 lookDir = lookAtTarget - camTransform.position;
            targetRot = (lookDir.sqrMagnitude > 0.0001f)
                ? Quaternion.LookRotation(lookDir)
                : camTransform.rotation;
        }

        // 3. Apply Smooth or Instant Update
        if (smoothSpeed > 0f)
        {
            float t = Time.deltaTime * smoothSpeed;
            camTransform.position = Vector3.Lerp(camTransform.position, targetPos, t);
            camTransform.rotation = Quaternion.Slerp(camTransform.rotation, targetRot, t);
        }
        else
        {
            camTransform.position = targetPos;
            camTransform.rotation = targetRot;
        }
    }

    private void HandleStaticCamera()
    {
        if (_activeView.staticAnchor != null)
        {
            mainCamera.transform.position = _activeView.staticAnchor.position;
            mainCamera.transform.rotation = _activeView.staticAnchor.rotation;
        }
    }

    // =================================================================
    //                 LIDAR MESH GENERATION (BURST)
    // =================================================================

    private void UpdateLidarMesh()
    {
        // Check both the master global toggle and the specific Lidar toggle
        bool masterOn = (masterToggle == null || masterToggle.isOn);
        bool lidarOn = (lidarToggle != null) ? lidarToggle.isOn : showLidar;

        bool isVisualizing = masterOn && lidarOn;

        // Clear the mesh if visualization is off or missing references
        if (!isVisualizing || _agentToDraw == null || MADataHub.Instance == null)
        {
            // Only clear if the mesh actually has data to avoid redundant calls
            if (_lidarMesh.vertexCount > 0) _lidarMesh.Clear();
            return;
        }

        var db = MADataHub.Instance;
        int idx = _agentToDraw.agentIndex;

        // Reallocate native arrays if the resolution changes at runtime
        if (!_lidarRangesNative.IsCreated || _lidarResolution != db.lidarCount)
        {
            DisposeBuffers();

            _lidarResolution = db.lidarCount;
            _lidarRangesNative = new NativeArray<float>(_lidarResolution, Allocator.Persistent);
            _meshVertsNative = new NativeArray<Vector3>(_lidarResolution * 2, Allocator.Persistent);
            _meshColorsNative = new NativeArray<Color>(_lidarResolution * 2, Allocator.Persistent);

            // Setup indices for Line topology (0,1, 2,3, 4,5...)
            _meshIndices = new int[_lidarResolution * 2];
            for (int i = 0; i < _meshIndices.Length; i++) _meshIndices[i] = i;
        }

        // Align the mesh root with the actual Lidar Sensor, not the Agent root
        if (_cachedLidarTransform != null)
        {
            _lidarRenderObj.transform.position = _cachedLidarTransform.position;
            _lidarRenderObj.transform.rotation = _cachedLidarTransform.rotation;
        }

        // Copy managed array data into the native array for Burst Job processing
        _lidarRangesNative.CopyFrom(db.lidarRanges[idx]);

        float sAngle = db.lidarMinAngleDeg * Mathf.Deg2Rad;
        float eAngle = db.lidarMaxAngleDeg * Mathf.Deg2Rad;

        // Adjust angles depending on sweep direction
        if (db.lidarScanDirection == MALidarScanDirection.Clockwise)
        {
            sAngle = db.lidarMaxAngleDeg * Mathf.Deg2Rad;
            eAngle = db.lidarMinAngleDeg * Mathf.Deg2Rad;
        }

        GenerateLidarMeshJob meshJob = new GenerateLidarMeshJob
        {
            ranges = _lidarRangesNative,
            verts = _meshVertsNative,
            colors = _meshColorsNative,
            res = _lidarResolution,
            maxRange = db.lidarMaxRange,
            startAngle = sAngle,
            endAngle = eAngle,
            colorNear = new float4(nearColor.r, nearColor.g, nearColor.b, nearColor.a),
            colorFar = new float4(farColor.r, farColor.g, farColor.b, farColor.a)
        };

        // Execute the job on the main thread (fast enough due to Burst)
        meshJob.Run(_lidarResolution);

        // Apply updated data to the Mesh
        _lidarMesh.SetVertices(_meshVertsNative);
        _lidarMesh.SetColors(_meshColorsNative);
        _lidarMesh.SetIndices(_meshIndices, MeshTopology.Lines, 0);
        _lidarMesh.RecalculateBounds();
    }

    [BurstCompile]
    struct GenerateLidarMeshJob : IJobFor
    {
        [ReadOnly] public NativeArray<float> ranges;

        // Attributes required because we are writing to indices beyond 'i' (i*2 and i*2+1)
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<Vector3> verts;
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<Color> colors;

        public int res;
        public float maxRange;
        public float startAngle;
        public float endAngle;
        public float4 colorNear;
        public float4 colorFar;

        public void Execute(int i)
        {
            int vIndex = i * 2;
            float dist = ranges[i];

            // Protect against divide-by-zero if resolution is ever set to 1
            float angleStep = res > 1 ? (endAngle - startAngle) / (res - 1) : 0f;
            float currentAngle = startAngle + (i * angleStep);

            // Sine handles X, Cosine handles Z so that 0 degrees points Forward (+Z)
            float3 dir = new float3(math.cos(currentAngle), 0, math.sin(currentAngle));

            float t = dist / maxRange;
            float4 cVec = math.lerp(colorNear, colorFar, t);
            Color finalColor = new Color(cVec.x, cVec.y, cVec.z, cVec.w);

            // Origin vertex of the line
            verts[vIndex] = Vector3.zero;
            colors[vIndex] = finalColor;

            // Hit vertex of the line
            verts[vIndex + 1] = new Vector3(dir.x * dist, dir.y * dist, dir.z * dist);
            colors[vIndex + 1] = finalColor;
        }
    }

    // =================================================================
    //                    GL DEBUG LINE RENDERING
    // =================================================================

    private void OnEndContextRendering(ScriptableRenderContext context, List<Camera> cameras)
    {
        // Prevent drawing stale frames or drawing without an agent
        if (Time.frameCount > _frameDrawRequest + 1 || _agentToDraw == null) return;
        if (masterToggle != null && !masterToggle.isOn) return;

        // Ensure we only draw during the main camera's render pass
        if (!cameras.Contains(mainCamera)) return;

        DrawLineVisuals();
    }

    private void DrawLineVisuals()
    {
        if (_lineMaterial == null) CreateLineMaterial();
        _lineMaterial.SetPass(0);

        GL.PushMatrix();
        GL.Begin(GL.LINES);

        Vector3 centerPos = _agentToDraw.transform.position;
        if (_agentToDraw.rb != null)
            centerPos = _agentToDraw.transform.TransformPoint(_agentToDraw.rb.centerOfMass);

        var db = MADataHub.Instance;
        int idx = _agentToDraw.agentIndex;

        // 1. Velocity (Blue)
        Vector3 velocity = (_agentToDraw.rb != null) ? _agentToDraw.rb.linearVelocity : Vector3.zero;
        DrawLine(centerPos, centerPos + (velocity * velocityScale), colorVelocity);

        // 2. Acceleration (Red)
        // Convert ROS axes back to Unity axes, then to World Space
        Vector3 rawAccel = db.linAccelMS[idx];
        Vector3 unityLocalAccel = new Vector3(-rawAccel.y, rawAccel.z, rawAccel.x);
        Vector3 worldAccel = _agentToDraw.transform.TransformDirection(unityLocalAccel);

        Vector3 accelStart = centerPos + Vector3.up * 0.05f; // Slight offset to prevent Z-fighting
        DrawLine(accelStart, accelStart + (worldAccel * accelScale), colorAccel);

        // 3. Forward Vector (Yellow)
        // Projects current velocity onto the forward axis to visualize longitudinal speed
        Vector3 forward = _agentToDraw.transform.forward;
        float forwardSpeed = Vector3.Dot(velocity, forward);
        DrawLine(centerPos, centerPos + (forward * forwardSpeed * velocityScale), colorForward);

        // 4. Steering Angles (Green) - USING CACHED WHEELS
        float steerAngle = db.steeringAngleDeg[idx];
        if (_cachedWheels != null)
        {
            foreach (var wheel in _cachedWheels)
            {
                if (wheel.transform.localPosition.z > 0) // Filter for Front wheels only
                {
                    Vector3 pos = wheel.transform.position;

                    // Draw Ghost Line (Neutral/Straight ahead)
                    GL.Color(new Color(0, 1, 0, 0.2f));
                    GL.Vertex(pos);
                    GL.Vertex(pos + (_agentToDraw.transform.forward * steeringLineLength));

                    // Draw Active Steer Line
                    Quaternion rot = Quaternion.Euler(0, steerAngle, 0);
                    Vector3 steerDir = _agentToDraw.transform.rotation * rot * Vector3.forward;

                    GL.Color(colorSteer);
                    GL.Vertex(pos);
                    GL.Vertex(pos + (steerDir * steeringLineLength));
                }
            }
        }

        GL.End();
        GL.PopMatrix();
    }

    private void DrawLine(Vector3 start, Vector3 end, Color col)
    {
        GL.Color(col);
        GL.Vertex(start);
        GL.Vertex(end);
    }

    private void CreateLineMaterial()
    {
        if (_lineMaterial == null)
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader != null)
            {
                _lineMaterial = new Material(shader);
                _lineMaterial.hideFlags = HideFlags.HideAndDontSave;
                _lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _lineMaterial.SetInt("_ZWrite", 0);
            }
            else
            {
                Debug.LogWarning("Could not find Hidden/Internal-Colored shader for debug lines.");
            }
        }
    }
}