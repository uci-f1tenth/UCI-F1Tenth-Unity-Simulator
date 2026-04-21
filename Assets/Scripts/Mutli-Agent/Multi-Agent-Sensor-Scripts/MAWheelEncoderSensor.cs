using UnityEngine;

/// <summary>
/// Multi-Agent Wheel Encoder (Odometry).
/// Simulates wheel tick generation based on RPM from the vehicle controller.
/// Automatically links to the parent MAVehicleController.
/// </summary>
public class MAWheelEncoderSensor : MonoBehaviour, ITimeStepper
{
    [Header("--- Hardware Configuration ---")]
    [Tooltip("How many pulses the physical sensor sends per 1 full wheel turn.")]
    public int ticksPerRevolution = 360;

    [Tooltip("How often the data is updated (Hz). Real-world VESC data usually runs at 10Hz to 100Hz.")]
    public float updateRateHz = 50f;

    // --- Internal State Cache ---
    private double _internalAccumulatedTicks = 0;
    private float _updateTimer = 0f;

    // --- System References ---
    private MADataHub _dataHub;
    private MATimeStepManager _timeManager;
    private MAVehicleController _vehicleController;

    // =================================================================
    //                      INITIALIZATION
    // =================================================================
    void Start()
    {
        _dataHub = MADataHub.Instance;
        _timeManager = MATimeStepManager.Instance;

        // Auto-Link to the Parent Vehicle Controller
        _vehicleController = GetComponentInParent<MAVehicleController>();

        if (_vehicleController == null)
        {
            Debug.LogError($"[MAWheelEncoder] No MAVehicleController found in parents of {gameObject.name}!");
        }
    }

    // =================================================================
    //                      CORE DATA LOOP
    // =================================================================
    public void TimeStep()
    {
        // Ensure all references are valid before executing
        if (_dataHub == null || _timeManager == null || _vehicleController == null) return;

        // Dynamically grab the index to ensure it's always accurate
        int idx = _vehicleController.agentIndex;

        // 1. READ RPM & CALCULATE MOVEMENT
        // Read the current RPM for this specific agent from the DataHub
        float rpm = _dataHub.currentWheelRPM[idx];

        // Convert RPM to ticks per second
        float ticksPerSecond = (rpm / 60.0f) * ticksPerRevolution;

        // 2. ACCUMULATE TICKS
        // We always accumulate the ticks internally every physics step to ensure no lost motion
        _internalAccumulatedTicks += ticksPerSecond * _timeManager.fixedDeltaTime;

        // Safety: Stop overflow from extremely long training runs
        if (_internalAccumulatedTicks > 1000000000)
        {
            _internalAccumulatedTicks = 0;
        }

        // 3. HARDWARE OUTPUT RATE CONTROL
        // Real sensors don't update every single physics frame; they fire at a specific Hz
        float interval = 1f / updateRateHz;
        _updateTimer += _timeManager.fixedDeltaTime;

        if (_updateTimer >= interval)
        {
            // Write the 'Hardware' signal to the DataHub only at the specified rate
            _dataHub.wheelTicks[idx] = (int)_internalAccumulatedTicks;

            // Reduce the timer by the interval to maintain accurate timing over time
            _updateTimer -= interval;
        }
    }
}