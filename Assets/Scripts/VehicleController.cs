using UnityEngine;

public class CarControl : MonoBehaviour
{
    [Header("Car Properties")]
    public float motorTorque = 500f;
    public float brakeTorque = 500f;
    public float maxSpeed = 15f;
    public float steeringRange = 30f;
    public float steeringSpeedDeg = 600.0f;

    private WheelControl[] wheels;
    private Rigidbody rigidBody;

    private CarInputActions carControls; // Reference to the new input system

    void Awake()
    {
        carControls = new CarInputActions(); // Initialize Input Actions
    }
    void OnEnable()
    {
        carControls.Enable();
    }

    void OnDisable()
    {
        carControls.Disable();
    }

    // Start is called before the first frame update
    void Start()
    {
        rigidBody = GetComponent<Rigidbody>();

        // Get all wheel components attached to the car
        wheels = GetComponentsInChildren<WheelControl>();
    }

    private float currentSteerAngle = 0f;
    // FixedUpdate is called at a fixed time interval
    void FixedUpdate()
    {
        // Read the Vector2 input from the new Input System
        Vector2 inputVector = carControls.Car.Movement.ReadValue<Vector2>();

        // Get player input for acceleration and steering
        float vInput = inputVector.y; // Forward/backward input
        float hInput = inputVector.x; // Steering input

        // Calculate current speed along the car's forward axis
        float forwardSpeed = Vector3.Dot(transform.forward, rigidBody.linearVelocity);
        float speedFactor = Mathf.InverseLerp(0, maxSpeed, Mathf.Abs(forwardSpeed)); // Normalized speed factor

        // Reduce motor torque and steering at high speeds for better handling
        float currentMotorTorque = Mathf.Lerp(motorTorque, 0, speedFactor);

        float targetAngle = hInput * steeringRange;
        currentSteerAngle = Mathf.MoveTowards(
            currentSteerAngle,
            targetAngle,
            steeringSpeedDeg * Time.fixedDeltaTime
        );

        // Determine if the player is accelerating or trying to reverse
        bool isAccelerating = Mathf.Sign(vInput) == Mathf.Sign(forwardSpeed);

        foreach (var wheel in wheels)
        {
            // Apply steering to wheels that support steering
            if (wheel.steerable)
            {
                wheel.WheelCollider.steerAngle = currentSteerAngle;
            }

            if (wheel.motorized)
            {
                wheel.WheelCollider.motorTorque = vInput * currentMotorTorque;
            }

            if (vInput == 0.0f)
            {
                wheel.WheelCollider.brakeTorque = brakeTorque;
            }
            else
            {
                wheel.WheelCollider.brakeTorque = 0.0f;
            }
        }

        // Anti-flip: Vector3.up.dot(transform.up) returns 1 if upright, -1 if upside down
        if (Vector3.Dot(transform.up, Vector3.up) < 0.5f)
        {
            ResetCar();
        }
    }

    public void ResetCar()
    {
        // 1. Kill the momentum so it doesn't fly away
        rigidBody.linearVelocity = Vector3.zero;
        rigidBody.angularVelocity = Vector3.zero;

        // 2. Reset Rotation (Keep the Y heading, but flatten X and Z)
        Vector3 currentEuler = transform.eulerAngles;
        transform.rotation = Quaternion.Euler(0, currentEuler.y, 0);

        // 3. Lift slightly to prevent clipping through the ground
        transform.position += Vector3.up * 0.1f;
    }
}
