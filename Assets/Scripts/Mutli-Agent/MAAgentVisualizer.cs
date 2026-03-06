using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using System.Collections.Generic;
using TMPro;

public class MAAgentVisualizer : MonoBehaviour
{
    public static MAAgentVisualizer Instance { get; private set; }

    // =================================================================
    //                          CAMERA SYSTEM
    // =================================================================
    [System.Serializable]
    public class CameraPreset
    {
        public string name = "New View";
        public CameraMode mode = CameraMode.Chase;

        [Header("Chase Settings (Relative)")]
        [Tooltip("Position relative to the car (e.g., 0, 2, -5 for standard chase)")]
        public Vector3 localPosition = new Vector3(0, 2, -5);
        [Tooltip("Point relative to car to look at (e.g., 0, 0, 2)")]
        public Vector3 lookAtOffset = Vector3.zero;

        [Header("Static Settings (World)")]
        [Tooltip("Drag a scene object here to use its position/rotation")]
        public Transform staticAnchor;
    }

    public enum CameraMode { Chase, Static }

    [Header("--- Camera Control ---")]
    public Camera mainCamera;
    public TextMeshProUGUI cameraModeText;
    [Range(0f, 100f)] public float smoothSpeed = 10f;
    public List<CameraPreset> cameraViews = new List<CameraPreset>();

    // Internal Camera State
    private int _currentCamIndex = 0;
    private CameraPreset _activeView;

    // =================================================================
    //                          DEBUG VISUALS
    // =================================================================
    [Header("--- Visuals UI ---")]
    [Tooltip("Connect this to your UI Toggle. If off, lines are hidden, but camera still works.")]
    public Toggle masterToggle;

    [Header("--- Visualization Settings ---")]
    public float velocityScale = 0.5f;
    public float accelScale = 0.2f;
    public float steeringLineLength = 2.0f;

    [Header("--- Colors ---")]
    public Color colorVelocity = Color.blue;
    public Color colorAccel = Color.red;
    public Color colorForward = Color.yellow;
    public Color colorSteer = Color.green;

    // Internal Render State
    private Material _lineMaterial;
    private MAVehicleController _agentToDraw;
    private int _frameDrawRequest;

    // =================================================================
    //                          INIT
    // =================================================================

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        // Default to main camera if not assigned
        if (mainCamera == null) mainCamera = Camera.main;

        // Validate camera list
        if (cameraViews.Count == 0)
        {
            cameraViews.Add(new CameraPreset { name = "Default Chase" });
        }
        UpdateActiveView();
    }

    private void OnEnable()
    {
        RenderPipelineManager.endContextRendering += OnEndContextRendering;
    }

    private void OnDisable()
    {
        RenderPipelineManager.endContextRendering -= OnEndContextRendering;
    }

    // =================================================================
    //                        PUBLIC API (UI)
    // =================================================================

    /// <summary>
    /// Hook this to your "Next Camera" Button
    /// </summary>
    public void NextCamera()
    {
        _currentCamIndex = (_currentCamIndex + 1) % cameraViews.Count;
        UpdateActiveView();
    }

    /// <summary>
    /// Hook this to your "Prev Camera" Button
    /// </summary>
    public void PrevCamera()
    {
        _currentCamIndex--;
        if (_currentCamIndex < 0) _currentCamIndex = cameraViews.Count - 1;
        UpdateActiveView();
    }

    private void UpdateActiveView()
    {
        _activeView = cameraViews[_currentCamIndex];
        // Update a UI Text label here to show the camera name
        if (cameraModeText != null) cameraModeText.text = _activeView.name;
    }

    /// <summary>
    /// Called by MAInfoPanelUpdater every frame.
    /// Updates camera position and registers GL drawing.
    /// </summary>
    public void DrawVisuals(MAVehicleController agent)
    {
        if (agent == null) return;

        // 1. Always update the target (so camera works even if lines are off)
        _agentToDraw = agent;
        _frameDrawRequest = Time.frameCount;

        // 2. Sync Wheels (Visual Mesh Update)
        // Only needed if we are actually looking at this agent
        agent.SyncWheels();
    }

    // =================================================================
    //                       CAMERA LOGIC
    // =================================================================

    private void LateUpdate()
    {
        if (mainCamera == null || _agentToDraw == null || _activeView == null) return;

        // Only move camera if we received a request this frame 
        // (This prevents camera ghosting if the simulation is paused or UI deselected)
        if (Time.frameCount > _frameDrawRequest + 1) return;

        if (_activeView.mode == CameraMode.Chase)
        {
            HandleChaseCamera();
        }
        else if (_activeView.mode == CameraMode.Static)
        {
            HandleStaticCamera();
        }
    }

    private void HandleChaseCamera()
    {
        if (_agentToDraw == null) return;
        Transform target = _agentToDraw.transform;

        // 1. Calculate Target Position (Where the camera wants to be)
        Vector3 desiredPos = target.TransformPoint(_activeView.localPosition);

        // 2. Calculate Look Target (What the camera wants to look at)
        Vector3 lookAtTarget = target.TransformPoint(_activeView.lookAtOffset);

        // 3. Move Position (Smoothly)
        // We move the camera *before* calculating rotation to ensure the vector is accurate
        if (smoothSpeed > 0f)
        {
            mainCamera.transform.position = Vector3.Lerp(mainCamera.transform.position, desiredPos, Time.deltaTime * smoothSpeed);
        }
        else
        {
            mainCamera.transform.position = desiredPos;
        }

        // 4. Calculate Direction Vector
        Vector3 lookDir = lookAtTarget - mainCamera.transform.position;

        // --- SAFETY CHECK ---
        // If the camera overlaps the target (vector length ~0), skip rotation to avoid the error.
        if (lookDir.sqrMagnitude > 0.001f)
        {
            Quaternion targetRot = Quaternion.LookRotation(lookDir);
            if (smoothSpeed > 0f)
            {
                mainCamera.transform.rotation = Quaternion.Slerp(mainCamera.transform.rotation, targetRot, Time.deltaTime * smoothSpeed);
            }
            else
            {
                mainCamera.transform.rotation = targetRot;
            }
        }
    }

    private void HandleStaticCamera()
    {
        if (_activeView.staticAnchor != null)
        {
            // Just snap to the static transform
            // We use Lerp just in case you switch cameras rapidly, so it doesn't "cut" instantly
            mainCamera.transform.position = Vector3.Lerp(mainCamera.transform.position, _activeView.staticAnchor.position, Time.deltaTime * 5f);
            mainCamera.transform.rotation = Quaternion.Slerp(mainCamera.transform.rotation, _activeView.staticAnchor.rotation, Time.deltaTime * 5f);
        }
        else
        {
            // If user forgot to assign a transform, look at the car from a generic high point
            // This is a fallback
            mainCamera.transform.LookAt(_agentToDraw.transform);
        }
    }

    // =================================================================
    //                      DEBUG LINE RENDERING
    // =================================================================

    private void OnEndContextRendering(ScriptableRenderContext context, System.Collections.Generic.List<Camera> cameras)
    {
        // 1. Check if we should draw lines
        if (_agentToDraw == null || !Application.isPlaying) return;
        if (Time.frameCount > _frameDrawRequest + 1) return;

        // UI Toggle Check: If the toggle is OFF, we exit here. 
        // (Note: We did this AFTER the camera logic so camera still works)
        if (masterToggle != null && !masterToggle.isOn) return;

        CreateLineMaterial();
        _lineMaterial.SetPass(0);

        var db = MADataHub.Instance;
        if (db == null) return;
        int idx = _agentToDraw.agentIndex;

        GL.PushMatrix();
        GL.Begin(GL.LINES);

        Vector3 centerPos = _agentToDraw.transform.position;
        if (_agentToDraw.rb != null)
            centerPos = _agentToDraw.transform.TransformPoint(_agentToDraw.rb.centerOfMass);

        // --- DRAWING LOGIC ---

        // 1. Velocity (Blue)
        Vector3 velocity = (_agentToDraw.rb != null) ? _agentToDraw.rb.linearVelocity : Vector3.zero;
        DrawLine(centerPos, centerPos + (velocity * velocityScale), colorVelocity);

        // 2. Acceleration (Red)
        Vector3 accel = db.linAccelMS[idx];
        Vector3 accelStart = centerPos + Vector3.up * 0.05f;
        DrawLine(accelStart, accelStart + (accel * accelScale), colorAccel);

        // 3. Forward (Yellow)
        Vector3 forward = _agentToDraw.transform.forward;
        float forwardSpeed = Vector3.Dot(velocity, forward);
        DrawLine(centerPos, centerPos + (forward * forwardSpeed * velocityScale), colorForward);

        // 4. Steering (Green)
        float steerAngle = db.steeringAngleDeg[idx];
        DrawSteeringLogic(_agentToDraw, steerAngle);

        GL.End();
        GL.PopMatrix();
    }

    private void DrawSteeringLogic(MAVehicleController agent, float steerAngle)
    {
        foreach (var wheel in agent.GetComponentsInChildren<WheelCollider>())
        {
            if (wheel.transform.localPosition.z > 0) // Front wheels only
            {
                Vector3 pos = wheel.transform.position;

                // Ghost (Neutral)
                GL.Color(new Color(0, 1, 0, 0.2f));
                GL.Vertex(pos);
                GL.Vertex(pos + (agent.transform.forward * steeringLineLength));

                // Active (Steered)
                Quaternion rot = Quaternion.Euler(0, steerAngle, 0);
                Vector3 steerDir = agent.transform.rotation * rot * Vector3.forward;

                GL.Color(colorSteer);
                GL.Vertex(pos);
                GL.Vertex(pos + (steerDir * steeringLineLength));
            }
        }
    }

    private void DrawLine(Vector3 start, Vector3 end, Color col)
    {
        GL.Color(col);
        GL.Vertex(start);
        GL.Vertex(end);
    }

    private void CreateLineMaterial()
    {
        if (!_lineMaterial)
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            _lineMaterial = new Material(shader);
        }
    }
}