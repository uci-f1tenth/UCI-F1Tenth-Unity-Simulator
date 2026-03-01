using UnityEngine;
using TMPro; // Required for updating the button text

public class CarControl : MonoBehaviour
{
    [Header("Control Mode")]
    public bool useUdpControl = false;
    public TextMeshProUGUI buttonLabel; // Drag your Button's Text (TMP) object here

    [Header("Car Properties")]
    public float motorTorque = 500f;
    public float brakeTorque = 500f;
    public float maxSpeed = 15f;
    public float steeringRange = 30f;
    public float steeringSpeedDeg = 600.0f;

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

        // Set initial button text based on default state
        UpdateToggleButtonUI();

        if (checkpointManager != null) checkpointManager.TeleportToLastCheckpoint(rigidBody);
    }

    // --- NEW: THE TOGGLE LOGIC ---
    public void ToggleControlMode()
    {
        useUdpControl = !useUdpControl; // Flip the state
        UpdateToggleButtonUI();
        Debug.Log(useUdpControl ? "Switched to UDP/AI Control" : "Switched to Manual Control");
    }

    private void UpdateToggleButtonUI()
    {
        if (buttonLabel != null)
        {
            buttonLabel.text = useUdpControl ? "Automatic" : "Manual";
        }
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

            // Always keep the Hub updated with the current state
            DataBridge.Instance.throttle = vInput;
            DataBridge.Instance.steering = hInput;
        }

        // 2. PHYSICS & HUB UPDATE
        float forwardSpeed = Vector3.Dot(transform.forward, rigidBody.linearVelocity);
        DataBridge.Instance.speedMS = forwardSpeed;

        float speedFactor = Mathf.InverseLerp(0, maxSpeed, Mathf.Abs(forwardSpeed));
        float currentMotorTorque = Mathf.Lerp(motorTorque, 0, speedFactor);

        float targetAngle = hInput * steeringRange;
        currentSteerAngle = Mathf.MoveTowards(currentSteerAngle, targetAngle, steeringSpeedDeg * Time.fixedDeltaTime);

        // 3. APPLY TO WHEELS
        foreach (var wheel in wheels)
        {
            if (wheel.steerable) wheel.WheelCollider.steerAngle = currentSteerAngle;
            if (wheel.motorized) wheel.WheelCollider.motorTorque = vInput * currentMotorTorque;
            wheel.WheelCollider.brakeTorque = (Mathf.Abs(vInput) < 0.01f) ? brakeTorque : 0.0f;
        }

        if (checkpointManager != null && Vector3.Dot(transform.up, Vector3.up) < 0.25f) checkpointManager.TeleportToLastCheckpoint(rigidBody);
    }
}