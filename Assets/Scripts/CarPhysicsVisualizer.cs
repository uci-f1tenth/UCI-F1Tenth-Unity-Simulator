using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class CarPhysicsVisualizer : MonoBehaviour, ITimeStepper
{
    [Header("Settings")]
    public bool showVisuals = true; // Master toggle
    public float gizmoSize = 0.05f;
    public float steeringLineLength = 2.0f;
    public float velocityScale = 0.5f;
    public float accelScale = 0.2f;

    [Header("References")]
    public Rigidbody rb;
    public WheelCollider wheelL;
    public WheelCollider wheelR;

    private Material lineMaterial;
    private Vector3 lastVelocity;
    private Vector3 currentAcceleration;

    // --- UI TOGGLE FUNCTIONS ---

    public void ToggleAllVisuals(bool value) => showVisuals = value;

    // Optional: Individual toggles if you have multiple UI checkboxes
    public void SetShowVisuals(bool value) => showVisuals = value;

    public void TimeStep()
    {
        if (rb == null) return;

        // Calculate Acceleration: (Current Vel - Last Vel) / Time
        Vector3 currentVelocity = rb.linearVelocity;
        currentAcceleration = (currentVelocity - lastVelocity) / TimeStepManager.fixedDeltaTime;
        lastVelocity = currentVelocity;
    }

    void OnEnable()
    {
        RenderPipelineManager.endContextRendering += OnEndContextRendering;
    }

    void OnDisable()
    {
        RenderPipelineManager.endContextRendering -= OnEndContextRendering;
    }

    private void OnDrawGizmos()
    {
        if (rb == null || !showVisuals) return;

        // 1. Center of Mass
        Gizmos.color = Color.red;
        Vector3 worldCoM = transform.TransformPoint(rb.centerOfMass);
        Gizmos.DrawWireSphere(worldCoM, gizmoSize);
    }

    private void CreateLineMaterial()
    {
        if (!lineMaterial)
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            lineMaterial = new Material(shader);
        }
    }

    void OnEndContextRendering(ScriptableRenderContext context, List<Camera> cameras)
    {
        if (rb == null || !Application.isPlaying || !showVisuals) return;

        CreateLineMaterial();
        lineMaterial.SetPass(0);

        GL.PushMatrix();
        GL.Begin(GL.LINES);

        Vector3 worldCoM = rb.worldCenterOfMass;

        // 1. World Velocity (Blue)
        DrawLine(worldCoM, worldCoM + (rb.linearVelocity * velocityScale), Color.blue);

        // 2. Acceleration (Red)
        // This shows which way the force is pulling the car (Gas, Brake, or Centripetal)
        DrawLine(worldCoM, worldCoM + (currentAcceleration * accelScale), Color.red);

        // 3. Forward Velocity (Yellow)
        float forwardSpeed = Vector3.Dot(rb.linearVelocity, rb.transform.forward);
        Vector3 forwardVelVec = rb.transform.forward * forwardSpeed;
        DrawLine(worldCoM, worldCoM + (forwardVelVec * velocityScale), Color.yellow);

        // 4. Steering Visuals
        if (wheelL) DrawSteering(wheelL);
        if (wheelR) DrawSteering(wheelR);

        GL.End();
        GL.PopMatrix();
    }

    void DrawSteering(WheelCollider wheel)
    {
        Vector3 pos = wheel.transform.position;
        DrawLine(pos, pos + (rb.transform.forward * steeringLineLength), new Color(0, 1, 0, 0.2f));

        Quaternion steerRot = Quaternion.Euler(0, wheel.steerAngle, 0);
        Vector3 steeringDir = rb.transform.rotation * steerRot * Vector3.forward;
        DrawLine(pos, pos + (steeringDir * steeringLineLength), Color.green);
    }

    void DrawLine(Vector3 start, Vector3 end, Color color)
    {
        GL.Color(color);
        GL.Vertex(start);
        GL.Vertex(end);
    }
}