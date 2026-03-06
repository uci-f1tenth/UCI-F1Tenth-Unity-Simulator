using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

public class MAAgentVisualizer : MonoBehaviour
{
    public static MAAgentVisualizer Instance { get; private set; }

    [Header("--- UI Control ---")]
    [Tooltip("Connect this to your UI Toggle. If off, nothing draws.")]
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

    // Internal State
    private Material _lineMaterial;
    private MAVehicleController _agentToDraw;
    private int _frameDrawRequest;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
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
    //                      PUBLIC API
    // =================================================================

    /// <summary>
    /// Called by MAInfoPanelUpdater every frame.
    /// Registers the agent to be drawn during the next Render pass.
    /// </summary>
    public void DrawVisuals(MAVehicleController agent)
    {
        // 1. Check Toggle UI
        if (masterToggle != null && !masterToggle.isOn)
        {
            _agentToDraw = null;
            return;
        }

        agent.SyncWheels(); // Also update wheels

        // 2. Register for this frame
        _agentToDraw = agent;
        _frameDrawRequest = Time.frameCount;
    }

    // =================================================================
    //                      RENDERING LOGIC
    // =================================================================

    private void OnEndContextRendering(ScriptableRenderContext context, System.Collections.Generic.List<Camera> cameras)
    {
        // Safety: Only draw if requested THIS frame (prevents stuck visuals)
        if (_agentToDraw == null || !Application.isPlaying) return;
        if (Time.frameCount > _frameDrawRequest + 1) return;

        CreateLineMaterial();
        _lineMaterial.SetPass(0);

        // Access DataHub efficiently
        var db = MADataHub.Instance;
        if (db == null) return;
        int idx = _agentToDraw.agentIndex;

        GL.PushMatrix();
        GL.Begin(GL.LINES);

        Vector3 centerPos = _agentToDraw.transform.position;
        if (_agentToDraw.rb != null)
            centerPos = _agentToDraw.transform.TransformPoint(_agentToDraw.rb.centerOfMass);

        // 1. VELOCITY (Blue)
        Vector3 velocity = Vector3.zero;
        if (_agentToDraw.rb != null) velocity = _agentToDraw.rb.linearVelocity;
        DrawLine(centerPos, centerPos + (velocity * velocityScale), colorVelocity);

        // 2. ACCELERATION (Red) - From DataHub
        Vector3 accel = db.linAccelMS[idx];
        Vector3 accelStart = centerPos + Vector3.up * 0.05f; // Slight offset to avoid Z-fighting
        DrawLine(accelStart, accelStart + (accel * accelScale), colorAccel);

        // 3. FORWARD DIRECTION (Yellow)
        Vector3 forward = _agentToDraw.transform.forward;
        float forwardSpeed = Vector3.Dot(velocity, forward);
        DrawLine(centerPos, centerPos + (forward * forwardSpeed * velocityScale), colorForward);

        // 4. STEERING (Green)
        float steerAngle = db.steeringAngleDeg[idx];
        DrawSteeringLogic(_agentToDraw, steerAngle);

        GL.End();
        GL.PopMatrix();
    }

    private void DrawSteeringLogic(MAVehicleController agent, float steerAngle)
    {
        // Iterate children to find wheels. 
        // Optimization: You could cache these in the Agent script if this is slow.
        foreach (var wheel in agent.GetComponentsInChildren<WheelCollider>())
        {
            // Simple check: Only draw for front wheels (local Z > 0)
            if (wheel.transform.localPosition.z > 0)
            {
                Vector3 pos = wheel.transform.position;

                // Neutral Forward (Ghost)
                Vector3 neutralEnd = pos + (agent.transform.forward * steeringLineLength);
                GL.Color(new Color(0, 1, 0, 0.2f));
                GL.Vertex(pos);
                GL.Vertex(neutralEnd);

                // Steered Direction (Solid)
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