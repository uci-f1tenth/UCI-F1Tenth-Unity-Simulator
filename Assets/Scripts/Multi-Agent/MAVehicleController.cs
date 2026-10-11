using UnityEngine;

public class MAVehicleController : MonoBehaviour, ITimeStepper
{
    [Header("--- Identity ---")]
    [Tooltip("Assigned by the Agent Manager. Determines which data slot this car reads/writes to.")]
    public int agentIndex = -1;

    [Header("--- Motor Properties ---")]
    public float peakTorqueLimit = 3f;
    public float motorTorque = 15f;
    public float maxRPM = 3500f;

    [Header("--- Steering Properties ---")]
    public float steeringRange = 30f;
    public float steeringSpeedDeg = 300.0f;

    // NOTE: CheckpointManager is now a Singleton, so we don't need a public reference here.

    [Header("--- Rewards & Penalties ---")]
    public float velocityReward = 0.1f;
    public float survivalReward = 0.05f;
    public float idlePenalty = -0.1f;
    public float erraticSteeringPenalty = -0.005f;
    public float crashPenalty = -100.0f;

    [Header("--- Reset Settings ---")]
    public float resetCooldown = 0.1f; // Slightly increased to prevent physics jitter
    private float lastResetTime;

    // Internal Physics
    public Rigidbody rb;
    private MAWheelControl[] wheels;
    private float currentSteerAngle = 0f;

    // Cache References for Performance
    private MADataHub dataHub;
    private MATCPTunnel tcpTunnel;
    private MATimeStepManager timeManager;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        wheels = GetComponentsInChildren<MAWheelControl>();

        // Cache singletons to avoid repeated "Instance" lookups in the hot loop
        dataHub = MADataHub.Instance;
        tcpTunnel = MATCPTunnel.Instance;
        timeManager = MATimeStepManager.Instance;

        if (agentIndex == -1)
            Debug.LogWarning($"[MAVehicleController] Warning: Agent on {gameObject.name} has no Index assigned!");

        // Find and Init the new optimized wheel scripts
        wheels = GetComponentsInChildren<MAWheelControl>();
        foreach (var w in wheels) w.Initialize();

        // Teleport to start position on load
        if (MACheckpointManager.Instance != null && agentIndex != -1)
        {
            MACheckpointManager.Instance.TeleportAgent(agentIndex, rb);
        }
    }

    // =================================================================================================
    //                                      CORE SIMULATION LOOP
    // =================================================================================================

    public void TimeStep()
    {
        // Safety Checks
        if (dataHub == null || agentIndex == -1 || agentIndex >= dataHub.numberOfAgents) return;

        // 1. READ INPUTS (From DataHub)
        /*
        { // Testing
            dataHub.throttle[agentIndex] = Random.Range(-1f, 1f);
            dataHub.steering[agentIndex] = Random.Range(-1f, 1f);
        }
        */

        float vInput = dataHub.throttle[agentIndex];
        float hInput = dataHub.steering[agentIndex];

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

        // 3. TORQUE CALCULATION (BLDC Motor Simulation)
        float rpmRatio = Mathf.Clamp01(Mathf.Abs(avgRPM) / maxRPM);
        float linearTorque = motorTorque * (1f - rpmRatio);
        float availableTorque = Mathf.Min(peakTorqueLimit, linearTorque);

        // Apply throttle input to torque
        float motorOut = vInput * availableTorque;

        // 4. STEERING LOGIC
        float targetAngle = hInput * steeringRange;

        // Lerp towards target for smooth steering visual/physics
        float dt = timeManager != null ? timeManager.fixedDeltaTime : Time.fixedDeltaTime;
        currentSteerAngle = Mathf.MoveTowards(currentSteerAngle, targetAngle, steeringSpeedDeg * dt);

        // 5. APPLY PHYSICS TO WHEELS
        foreach (var wheel in wheels)
        {
            if (wheel.steerable) wheel.WheelCollider.steerAngle = currentSteerAngle;
            if (wheel.motorized) wheel.WheelCollider.motorTorque = motorOut;
        }

        // 6. WRITE TELEMETRY (To DataHub)
        dataHub.currentWheelRPM[agentIndex] = avgRPM;
        dataHub.appliedWheelTorque[agentIndex] = motorOut;
        dataHub.steeringAngleDeg[agentIndex] = currentSteerAngle;

        // Calculate Forward Speed
        float speed = Vector3.Dot(transform.forward, rb.linearVelocity);
        dataHub.speedMS[agentIndex] = speed;

        // 7. CALCULATE REWARDS
        if (tcpTunnel != null && tcpTunnel.isConnected)
        {
            float stepReward = 0f;

            // Speed Reward
            stepReward += speed * velocityReward;

            // Survival Reward (Small bonus for just existing without crashing)
            stepReward += survivalReward;

            // Stability Penalty (Penalize zig-zagging)
            stepReward += Mathf.Abs(hInput) * erraticSteeringPenalty;

            // Idle Penalty (Discourage sitting still)
            stepReward += idlePenalty;

            // Send to TCP Tunnel (Scaled by time to ensure consistency)
            tcpTunnel.AddReward(agentIndex, stepReward * dt * 100f);
        }

        // 8. FLIP CHECK
        if (Vector3.Dot(transform.up, Vector3.up) < 0.25f)
        {
            HandleReset();
        }
    }

    // =================================================================
    //                             VISUALS
    // =================================================================
    public void SyncWheels()
    {
        // Batch update all 4 wheels in this single function call
        for (int i = 0; i < wheels.Length; i++)
        {
            wheels[i].SyncVisuals();
        }
    }

    // =================================================================================================
    //                                      RESET LOGIC
    // =================================================================================================

    // Call this from your Wall/Barrier scripts!
    public void HandleReset()
    {
        float currentTime = timeManager != null ? timeManager.fixedTime : Time.time;

        // Cooldown check
        if (currentTime > lastResetTime + resetCooldown)
        {
            lastResetTime = currentTime;

            // 1. Flag Crash for Python
            if (dataHub != null && agentIndex != -1)
            {
                dataHub.isCrashed[agentIndex] = true;
            }

            // 2. Apply Crash Penalty
            if (tcpTunnel != null)
            {
                tcpTunnel.AddReward(agentIndex, crashPenalty);
            }

            // 3. Physical Teleport via Singleton Manager
            if (MACheckpointManager.Instance != null)
            {
                // The Manager handles position, rotation, and zeroing velocity
                MACheckpointManager.Instance.TeleportAgent(agentIndex, rb);
            }
            else
            {
                Debug.LogError("No MACheckpointManager found! Cannot reset agent.");
            }

#if UNITY_EDITOR
            Debug.Log($"<color=red>[Agent {agentIndex}] Crash!</color> Penalty applied.");
#endif
        }
    }
}