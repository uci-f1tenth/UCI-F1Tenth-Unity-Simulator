using UnityEngine;
using TMPro;
using System;

public class MAInfoPanelUpdater : MonoBehaviour, ITimeStepper
{
    [Header("--- Settings ---")]
    [Tooltip("Update UI every N physics ticks. Higher = Better Performance.")]
    [Min(1)] public int renderFrameInterval = 5;

    [Tooltip("Which agent's data to visualize.")]
    public int selectedAgentIndex = 0;

    [Header("--- Global Stats ---")]
    public TextMeshProUGUI timeVal;
    public TextMeshProUGUI fpsVal;
    public TextMeshProUGUI upsVal; // Updates (Steps) Per Second
    public TextMeshProUGUI totalAgentsVal;

    [Header("--- Controls & Physics ---")]
    public TextMeshProUGUI speedVal;
    public TextMeshProUGUI throttleVal;
    public TextMeshProUGUI steerVal;
    public TextMeshProUGUI rpmVal;
    public TextMeshProUGUI torqueVal;
    public TextMeshProUGUI yawVal;
    public TextMeshProUGUI encoderVal; // Wheel Ticks

    [Header("--- Sensors (IMU) ---")]
    public TextMeshProUGUI imuVal;       // Rotation (Euler)
    public TextMeshProUGUI angAccelVal;  // Angular Velocity/Accel
    public TextMeshProUGUI linAccelVal;  // Linear Acceleration

    [Header("--- Racing Logic ---")]
    public TextMeshProUGUI lapCountVal;
    public TextMeshProUGUI currentLapVal;
    public TextMeshProUGUI lastLapVal;
    public TextMeshProUGUI bestLapVal;

    // --- State Cache (For "Check Before Write" Optimization) ---
    private float _lastSpeed, _lastThrottle, _lastSteer, _lastRpm, _lastTorque, _lastYaw;
    private float _lastCurLap, _lastPrevLap, _lastBestLap;
    private int _lastWheelTicks, _lastLaps;
    private Vector3 _lastImu, _lastAngAccel, _lastLinAccel;

    // --- Internal Timers ---
    private int _tickCounter = 0;
    private float _fpsTimer = 0f;
    private int _frameCount = 0;
    private float _currentFPS = 0f;
    private const float EPSILON = 0.005f; // Threshold for float comparison

    // --- References ---
    private MADataHub _dataHub;
    private MATimeStepManager _timeManager;

    void Start()
    {
        _dataHub = MADataHub.Instance;
        _timeManager = MATimeStepManager.Instance;
    }

    // Standard Update: Used ONLY for Frame Rate calculation (visuals)
    void Update()
    {
        if (!this.isActiveAndEnabled)
            return;

        _frameCount++;
        _fpsTimer += Time.unscaledDeltaTime;
        if (_fpsTimer > 0.5f)
        {
            _currentFPS = _frameCount / _fpsTimer;
            _frameCount = 0;
            _fpsTimer = 0f;

            // Update FPS text here directly as it doesn't depend on Physics Step
            if (fpsVal != null) fpsVal.text = _currentFPS.ToString("F0");
        }
    }

    // =================================================================
    //                  CORE DATA LOOP (Controlled by Manager)
    // =================================================================
    public void TimeStep()
    {
        if (!this.isActiveAndEnabled)
            return;

        // 1. Efficiency: Skip frames based on interval
        _tickCounter++;
        if (_tickCounter % renderFrameInterval != 0) return;

        if (_dataHub == null || _timeManager == null) return;
        if (_dataHub.numberOfAgents == 0) return;

        // 2. Safety: Clamp Index
        selectedAgentIndex = Mathf.Clamp(selectedAgentIndex, 0, _dataHub.numberOfAgents - 1);
        int idx = selectedAgentIndex;

        // ---------------------------------------------------------
        // A. GLOBAL DATA
        // ---------------------------------------------------------
        UpdateText(timeVal, FormatTime(_timeManager.fixedTime));
        UpdateText(upsVal, $"{_dataHub.ups:F0}");
        UpdateText(totalAgentsVal, _dataHub.numberOfAgents.ToString());

        // ---------------------------------------------------------
        // B. AGENT TELEMETRY (Using Optimized Checks)
        // ---------------------------------------------------------

        // Physics & Controls
        UpdateIfChanged(ref _lastSpeed, _dataHub.speedMS[idx], speedVal, "F1");
        UpdateIfChanged(ref _lastThrottle, _dataHub.throttle[idx], throttleVal, "F2");
        UpdateIfChanged(ref _lastSteer, _dataHub.steering[idx], steerVal, "F2");

        UpdateIfChanged(ref _lastRpm, _dataHub.currentWheelRPM[idx], rpmVal, "F0");
        UpdateIfChanged(ref _lastTorque, _dataHub.appliedWheelTorque[idx], torqueVal, "F1");
        UpdateIfChanged(ref _lastYaw, _dataHub.steeringAngleDeg[idx], yawVal, "F1");

        // Sensors
        // Note: You'll need to ensure DataHub actually has these arrays. 
        // If not, replace these lines with your specific sensor logic.
        UpdateVectorIfChanged(ref _lastImu, _dataHub.imuAnglesDeg[idx], imuVal, showMag: false);
        UpdateVectorIfChanged(ref _lastAngAccel, _dataHub.angAccelDeg[idx], angAccelVal, showMag: true);
        UpdateVectorIfChanged(ref _lastLinAccel, _dataHub.linAccelMS[idx], linAccelVal, showMag: true);

        // Encoders (Integers)
        UpdateIntIfChanged(ref _lastWheelTicks, _dataHub.wheelTicks[idx], encoderVal);

        // ---------------------------------------------------------
        // C. RACING LOGIC
        // ---------------------------------------------------------
        UpdateIntIfChanged(ref _lastLaps, _dataHub.lapCount[idx], lapCountVal);

        UpdateIfTimeChanged(ref _lastCurLap, _dataHub.currentLapTime[idx], currentLapVal);
        UpdateIfTimeChanged(ref _lastPrevLap, _dataHub.lastLapTime[idx], lastLapVal);
        UpdateIfTimeChanged(ref _lastBestLap, _dataHub.bestLapTime[idx], bestLapVal);
    }

    // =================================================================
    //                  OPTIMIZED HELPER METHODS
    // =================================================================

    // 1. Float Check
    private void UpdateIfChanged(ref float lastVal, float newVal, TextMeshProUGUI label, string format)
    {
        if (label == null) return;
        if (Mathf.Abs(lastVal - newVal) > EPSILON)
        {
            lastVal = newVal;
            label.text = newVal.ToString(format);
        }
    }

    // 2. Integer Check
    private void UpdateIntIfChanged(ref int lastVal, int newVal, TextMeshProUGUI label)
    {
        if (label == null) return;
        if (lastVal != newVal)
        {
            lastVal = newVal;
            label.text = newVal.ToString();
        }
    }

    // 3. Vector Check (Expensive, so critical to check distance first)
    private void UpdateVectorIfChanged(ref Vector3 lastVal, Vector3 newVal, TextMeshProUGUI label, bool showMag)
    {
        if (label == null) return;
        // Uses Square Magnitude to avoid expensive Sqrt() call for the check
        if ((lastVal - newVal).sqrMagnitude > (EPSILON * EPSILON))
        {
            lastVal = newVal;
            string txt = $"{newVal.x:F1}, {newVal.y:F1}, {newVal.z:F1}";
            if (showMag) txt += $" ({newVal.magnitude:F1})";
            label.text = txt;
        }
    }

    // 4. Time Formatting Check
    private void UpdateIfTimeChanged(ref float lastVal, float newVal, TextMeshProUGUI label)
    {
        if (label == null) return;
        if (Mathf.Abs(lastVal - newVal) > 0.05f) // Lower precision for time display is fine
        {
            lastVal = newVal;
            label.text = FormatTime(newVal);
        }
    }

    // 5. Direct Set (Use sparingly)
    private void UpdateText(TextMeshProUGUI label, string text)
    {
        if (label != null) label.text = text;
    }

    private string FormatTime(float seconds)
    {
        if (seconds < 0 || !float.IsFinite(seconds) || seconds >= float.MaxValue) return "--:--.---";

        TimeSpan t = TimeSpan.FromSeconds(seconds);
        return string.Format("{0:D2}:{1:D2}.{2:D3}", t.Minutes, t.Seconds, t.Milliseconds);
    }
}