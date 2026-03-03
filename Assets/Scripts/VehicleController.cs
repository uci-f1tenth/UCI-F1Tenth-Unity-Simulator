using UnityEngine;
using TMPro;

public class VehicleController : MonoBehaviour, ITimeStepper
{
    [Header("Control Mode")]
    public bool useUdpControl = false;
    public TextMeshProUGUI buttonLabel;

    [Header("Direct Motor Properties")]
    public float peakTorqueLimit = 300f;
    public float motorTorque = 500f;
    public float maxRPM = 1000f;

    [Header("Steering")]
    public float steeringRange = 30f;
    public float steeringSpeedDeg = 300.0f;

    [Header("System References")]
    public CheckpointManager checkpointManager;

    [Header("Collision & RL Settings")]
    public string wallTag = "Wall";
    public float resetCooldown = 0.5f;
    public float crashPenalty = -50.0f; // Increased penalty for RL training
    private float lastResetTime;

    [Tooltip("Idle penalty.")]
    public float idlePenalty = -0.1f;
    [Tooltip("Move forward motivation.")]
    public float forwardMotivation = 0.1f;

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

    private void OnCollisionEnter(Collision collision)
    {
        if (collision.gameObject.CompareTag(wallTag))
        {
            HandleReset();
        }
    }

    public void HandleReset()
    {
        // Only trigger if cooldown has passed to avoid multiple penalties in one hit
        if (TimeStepManager.fixedTime > lastResetTime + resetCooldown)
        {
            lastResetTime = TimeStepManager.fixedTime;

            // 1. Update DataBridge for Python Telemetry
            if (DataBridge.Instance != null)
            {
                DataBridge.Instance.isCrashed = true; // Flag sent to Python
            }

            // 2. Add Thread-Safe Penalty
            if (TCPTunnel.Instance != null)
            {
                TCPTunnel.Instance.AddReward(crashPenalty);
            }

            // 3. Physical Teleport
            if (checkpointManager != null)
            {
                checkpointManager.TeleportToLastCheckpoint(rigidBody);

                // Zero out velocities to prevent the car from sliding after teleport
                rigidBody.linearVelocity = Vector3.zero;
                rigidBody.angularVelocity = Vector3.zero;
            }

            Debug.Log($"<color=red>Vehicle Crash!</color> Penalty: {crashPenalty}");
        }
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

    public void TimeStep()
    {
        if (DataBridge.Instance == null) return;

        TCPTunnel.Instance.AddReward(idlePenalty);

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

        // 3. TORQUE CALCULATION
        float rpmRatio = Mathf.Clamp01(Mathf.Abs(avgRPM) / maxRPM);
        float linearTorque = motorTorque * (1f - rpmRatio);
        float availableTorque = Mathf.Min(peakTorqueLimit, linearTorque);
        float motorOut = vInput * availableTorque;

        // 4. STEERING
        float targetAngle = hInput * steeringRange;
        currentSteerAngle = Mathf.MoveTowards(currentSteerAngle, targetAngle, steeringSpeedDeg * TimeStepManager.fixedDeltaTime);

        // 5. APPLY TO WHEELS
        foreach (var wheel in wheels)
        {
            if (wheel.steerable) wheel.WheelCollider.steerAngle = currentSteerAngle;
            if (wheel.motorized) wheel.WheelCollider.motorTorque = motorOut;
        }

        // 6. SYNC TELEMETRY (SIM-TO-REAL)
        DataBridge.Instance.currentWheelRPM = avgRPM;
        DataBridge.Instance.appliedWheelTorque = motorOut;
        DataBridge.Instance.steeringAngleRad = currentSteerAngle * Mathf.Deg2Rad;
        DataBridge.Instance.speedMS = Vector3.Dot(transform.forward, rigidBody.linearVelocity);

        if (DataBridge.Instance.throttle > 0.01)
        {
            TCPTunnel.Instance.AddReward(forwardMotivation);
        }

        // 8. FLIP CHECK
        if (Vector3.Dot(transform.up, Vector3.up) < 0.25f)
        {
            HandleReset();
        }
    }
}