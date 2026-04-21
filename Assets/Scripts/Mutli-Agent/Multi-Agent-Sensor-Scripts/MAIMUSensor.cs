using UnityEngine;

/// <summary>
/// Multi-Agent IMU Sensor. 
/// Simulates an Inertial Measurement Unit (IMU) by calculating acceleration, angular velocity, and Euler angles.
/// Automatically links to the parent MAVehicleController.
/// </summary>
public class MAIMUSensor : MonoBehaviour, ITimeStepper
{
    [Header("--- Sensor Settings ---")]
    public bool useGravity = false;
    [Tooltip("Target frequency in Hz (e.g., 40 for 40Hz)")]
    public float updateRate = 40f;

    [Tooltip("Convert from Unity (Y-Up, Z-Forward) to Real World/ROS (Z-Up, X-Forward)")]
    public bool convertToRealWorldAxes = true;

    [Header("--- Noise Settings (Normalized 0-1) ---")]
    [Range(0f, 1f)] public float noiseIntensity = 0.05f;
    [Range(0f, 1f)] public float driftIntensity = 0.01f;

    // --- Internal State Cache ---
    private float _nextUpdateTime;
    private float _lastSampleTime;
    private Vector3 _currentDrift;

    // --- Physics Cache ---
    private Vector3 _lastLocalVelocity;
    private Vector3 _lastLocalAngularVelocity;

    // --- System References ---
    private MADataHub _dataHub;
    private MATimeStepManager _timeManager;
    private MAVehicleController _vehicleController;
    private Rigidbody _targetRigidbody;

    // =================================================================
    //                      INITIALIZATION
    // =================================================================
    void Start()
    {
        _dataHub = MADataHub.Instance;
        _timeManager = MATimeStepManager.Instance;

        // Auto-Link to the Parent Vehicle Controller
        _vehicleController = GetComponentInParent<MAVehicleController>();

        if (_vehicleController != null)
        {
            // Prefer the Rigidbody already cached by the vehicle controller
            _targetRigidbody = _vehicleController.rb;
        }
        else
        {
            Debug.LogError($"[MAIMUSensor] No MAVehicleController found in parents of {gameObject.name}!");
        }

        // Fallback just in case
        if (_targetRigidbody == null)
            _targetRigidbody = GetComponentInParent<Rigidbody>();

        if (_timeManager != null)
            _lastSampleTime = _timeManager.fixedTime;
    }

    // =================================================================
    //                      CORE DATA LOOP
    // =================================================================
    public void TimeStep()
    {
        if (_dataHub == null || _timeManager == null || _targetRigidbody == null || _vehicleController == null) return;

        // Only run logic if enough time has passed to match the target Hz
        if (_timeManager.fixedTime >= _nextUpdateTime)
        {
            float deltaTime = _timeManager.fixedTime - _lastSampleTime;

            // Prevent division by zero on first frame. 
            if (deltaTime <= 0f) deltaTime = _timeManager.fixedDeltaTime;

            UpdateSensorData(deltaTime);

            // Schedule next update based on frequency (1 / Hz)
            if (updateRate > 0f)
            {
                _nextUpdateTime = _timeManager.fixedTime + (1f / updateRate);
            }
            _lastSampleTime = _timeManager.fixedTime;
        }
    }

    private void UpdateSensorData(float dt)
    {
        // 1. LOCAL ANGLES 
        Vector3 localAnglesDeg = _targetRigidbody.transform.localEulerAngles;

        // 2. LOCAL LINEAR ACCELERATION
        // InverseTransformDirection calculates how the velocity looks from the perspective of the IMU chip
        Vector3 localVelocity = transform.InverseTransformDirection(_targetRigidbody.linearVelocity);
        Vector3 localLinearAccel = (localVelocity - _lastLocalVelocity) / dt;

        if (useGravity)
        {
            Vector3 localGravity = transform.InverseTransformDirection(Physics.gravity);
            localLinearAccel -= localGravity;
        }

        // 3. LOCAL ANGULAR ACCELERATION
        Vector3 localAngularVel = transform.InverseTransformDirection(_targetRigidbody.angularVelocity);
        Vector3 localAngularAccel = (localAngularVel - _lastLocalAngularVelocity) / dt;

        // 4. APPLY HARDWARE CORRUPTION (Noise & Drift)
        Vector3 finalAngles = ApplySensorEffects(localAnglesDeg, 0.02f, dt);
        Vector3 finalLinAccel = ApplySensorEffects(localLinearAccel, 1.5f, dt);
        Vector3 finalAngAccel = ApplySensorEffects(localAngularAccel, 0.5f, dt);

        // 5. AXIS CONVERSION (Sim-to-Real Helper)
        if (convertToRealWorldAxes)
        {
            finalAngles = UnityToRealWorldAxes(finalAngles);
            finalLinAccel = UnityToRealWorldAxes(finalLinAccel);
            finalAngAccel = UnityToRealWorldAxes(finalAngAccel);
        }

        // 6. UPDATE DATAHUB (Array-based for Multi-Agent)
        // We dynamically read _vehicleController.agentIndex to ensure we always have the correct ID
        int idx = _vehicleController.agentIndex;

        _dataHub.imuAnglesDeg[idx] = finalAngles;
        _dataHub.linAccelMS[idx] = finalLinAccel;
        _dataHub.angAccelDeg[idx] = finalAngAccel;

        // Cache for next derivative calculation
        _lastLocalVelocity = localVelocity;
        _lastLocalAngularVelocity = localAngularVel;
    }

    // =================================================================
    //                      HELPER FUNCTIONS
    // =================================================================

    /// <summary>
    /// Converts a Vector3 from Unity's Left-Handed (Y-Up) coordinate system 
    /// to the standard Real-World / ROS (REP-103) Right-Handed (Z-Up) system.
    /// </summary>
    private Vector3 UnityToRealWorldAxes(Vector3 unityVector)
    {
        // Unity: X=Right, Y=Up, Z=Forward
        // ROS:   X=Forward, Y=Left, Z=Up
        return new Vector3(
            unityVector.z,   // Real Forward = Unity Forward
            -unityVector.x,  // Real Left = Negative Unity Right
            unityVector.y    // Real Up = Unity Up
        );
    }

    private Vector3 ApplySensorEffects(Vector3 cleanData, float scale, float dt)
    {
        // Calculate Drift (Accumulates over time)
        if (driftIntensity > 0f)
        {
            _currentDrift += new Vector3(
                Random.Range(-1f, 1f),
                Random.Range(-1f, 1f),
                Random.Range(-1f, 1f)
            ) * (driftIntensity * dt);
        }
        else
        {
            _currentDrift = Vector3.zero;
        }

        // Calculate White Noise (Random every frame)
        Vector3 whiteNoise = Vector3.zero;
        if (noiseIntensity > 0f)
        {
            whiteNoise = new Vector3(
                Random.Range(-1f, 1f),
                Random.Range(-1f, 1f),
                Random.Range(-1f, 1f)
            ) * (noiseIntensity * scale);
        }

        return cleanData + whiteNoise + _currentDrift;
    }
}