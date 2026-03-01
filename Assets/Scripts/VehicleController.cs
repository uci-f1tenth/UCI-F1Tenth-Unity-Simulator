using UnityEngine;
using TMPro;

public class CarControl : MonoBehaviour
{
    [Header("Control Mode")]
    public bool useUdpControl = false;
    public TextMeshProUGUI buttonLabel;

    [Header("Direct Motor Properties")]
    [Tooltip("The absolute maximum torque the motor can produce (Flat Cap).")]
    public float peakTorqueLimit = 300f;
    [Tooltip("The theoretical stall torque if there was no limit (defines the slope).")]
    public float motorTorque = 500f;
    [Tooltip("RPM where torque becomes 0.")]
    public float maxRPM = 1000f;

    [Header("Steering")]
    public float steeringRange = 30f;
    public float steeringSpeedDeg = 300.0f;

    [Header("System References")]
    public CheckpointManager checkpointManager;

    private WheelControl[] wheels;
    private Rigidbody rigidBody;
    private CarInputActions carControls;
    private float currentSteerAngle = 0f;

    void Awake() => carControls = new CarInputActions();
    void OnEnable() => carControls.Enable();
    void OnDisable() => carControls.Disable();

    void Start()
    {
        rigidBody = GetComponent<Rigidbody>();
        wheels = GetComponentsInChildren<WheelControl>();
        UpdateToggleButtonUI();

        if (checkpointManager != null)
            checkpointManager.TeleportToLastCheckpoint(rigidBody);
    }

    public void ToggleControlMode()
    {
        useUdpControl = !useUdpControl;
        UpdateToggleButtonUI();
    }

    private void UpdateToggleButtonUI()
    {
        if (buttonLabel != null)
            buttonLabel.text = useUdpControl ? "Automatic" : "Manual";
    }

    void FixedUpdate()
    {
        if (DataBridge.Instance == null) return;

        float vInput, hInput;

        // 1. INPUT SELECTION
        if (useUdpControl)
        {
            vInput = DataBridge.Instance.throttle;
            hInput = DataBridge.Instance.steering;
        }
        else
        {
            Vector2 inputVector = carControls.Car.Movement.ReadValue<Vector2>();
            hInput = inputVector.x;
            vInput = inputVector.y;

            DataBridge.Instance.throttle = vInput;
            DataBridge.Instance.steering = hInput;
        }

        // 2. RPM TRACKING
        float avgRPM = 0;
        int motorizedCount = 0;
        foreach (var wheel in wheels)
        {
            if (wheel.motorized)
            {
                avgRPM += wheel.WheelCollider.rpm;
                motorizedCount++;
            }
        }
        if (motorizedCount > 0) avgRPM /= motorizedCount;

        // 3. PHASE CURRENT LIMITED TORQUE CALCULATION
        float rpmRatio = Mathf.Clamp01(Mathf.Abs(avgRPM) / maxRPM);
        float linearTorque = motorTorque * (1f - rpmRatio);
        float availableTorque = Mathf.Min(peakTorqueLimit, linearTorque);

        // 4. DIRECT MOTOR OUTPUT
        // motorOut will be negative if vInput is negative, creating braking/reverse force
        float motorOut = vInput * availableTorque;

        // 5. STEERING
        float targetAngle = hInput * steeringRange;
        currentSteerAngle = Mathf.MoveTowards(currentSteerAngle, targetAngle, steeringSpeedDeg * Time.fixedDeltaTime);

        // 6. APPLY TO WHEELS
        foreach (var wheel in wheels)
        {
            // Set Steering
            if (wheel.steerable) wheel.WheelCollider.steerAngle = currentSteerAngle;

            // Set Motor (Direct Command)
            if (wheel.motorized) wheel.WheelCollider.motorTorque = motorOut;

            // Explicitly ensure brakeTorque is always zero
            wheel.WheelCollider.brakeTorque = 0f;
        }

        // SYNC TELEMETRY
        DataBridge.Instance.currentWheelRPM = avgRPM;
        DataBridge.Instance.appliedWheelTorque = motorOut;
        DataBridge.Instance.steeringAngleRad = currentSteerAngle * Mathf.Deg2Rad;
        DataBridge.Instance.speedMS = Vector3.Dot(transform.forward, rigidBody.linearVelocity);

        // Flip Check
        if (checkpointManager != null && Vector3.Dot(transform.up, Vector3.up) < 0.25f)
            checkpointManager.TeleportToLastCheckpoint(rigidBody);
    }
}