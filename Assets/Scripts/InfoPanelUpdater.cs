using UnityEngine;
using TMPro;
using System;

public class InfoPanelUpdater : MonoBehaviour, ITimeStepper
{
    [Header("UI Value Slots")]
    public TextMeshProUGUI timeVal;
    public TextMeshProUGUI fpsVal, upsVal, speedVal, throttleVal, steerVal;
    public TextMeshProUGUI encoderVal, imuVal, angAccelVal, linAccelVal;
    public TextMeshProUGUI lapCountVal, currentLapVal, lastLapVal, bestLapVal;

    [Header("New Wheel & Yaw Slots")]
    public TextMeshProUGUI rpmVal;      // New: Wheel RPM
    public TextMeshProUGUI torqueVal;   // New: Applied Torque (Nm)
    public TextMeshProUGUI yawVal;      // New: Steering Yaw (Radians or Degrees)

    private float lastSpeed, lastThrottle, lastSteer, lastCurLap, lastPrevLap, lastBestLap;
    private float lastRpm, lastTorque, lastYaw; // Cache for new values
    private int lastWheelTicks, lastLaps;
    private Vector3 lastImu, lastAngAccel, lastLinAccel;

    private float uiRefreshTimer = 0.2f;
    private const float EPSILON = 0.005f;

    void Update()
    {
        if (DataBridge.Instance == null) return;

        uiRefreshTimer -= Time.unscaledDeltaTime;
        if (uiRefreshTimer <= 0)
        {
            UpdateText(fpsVal, DataBridge.Instance.fps.ToString("F0"));
            UpdateText(timeVal, FormatTime(DataBridge.Instance.timeSinceStart));
            uiRefreshTimer = 0.1f;
        }
    }

    public void TimeStep()
    {
        if (DataBridge.Instance == null) return;
        var db = DataBridge.Instance;

        // 1. Performance
        UpdateText(upsVal, db.ups.ToString("F0"));

        // 2. Telemetry & Sensors
        UpdateIfChanged(ref lastSpeed, db.speedMS, speedVal, "F1");
        UpdateIfChanged(ref lastThrottle, db.throttle, throttleVal, "F2");
        UpdateIfChanged(ref lastSteer, db.steering, steerVal, "F2");

        // --- NEW: Wheel & Yaw Updates ---
        // Displaying RPM as whole number, Torque with 1 decimal, Yaw with 3 decimals
        UpdateIfChanged(ref lastRpm, db.currentWheelRPM, rpmVal, "F0");
        UpdateIfChanged(ref lastTorque, db.appliedWheelTorque, torqueVal, "F3");
        UpdateIfChanged(ref lastYaw, db.steeringAngleRad, yawVal, "F3");

        if (db.wheelTicks != lastWheelTicks)
        {
            lastWheelTicks = db.wheelTicks;
            UpdateText(encoderVal, $"{lastWheelTicks}");
        }

        UpdateVectorIfChanged(ref lastImu, db.imuAngles, imuVal, false);
        UpdateVectorIfChanged(ref lastAngAccel, db.angAccel, angAccelVal, true);
        UpdateVectorIfChanged(ref lastLinAccel, db.linAccel, linAccelVal, true);

        // 3. Racing
        if (db.lapCount != lastLaps)
        {
            lastLaps = db.lapCount;
            UpdateText(lapCountVal, lastLaps.ToString());
        }
        UpdateIfTimeChanged(ref lastCurLap, db.currentLapTime, currentLapVal);
        UpdateIfTimeChanged(ref lastPrevLap, db.lastLapTime, lastLapVal);
        UpdateIfTimeChanged(ref lastBestLap, db.bestLapTime, bestLapVal);
    }

    // --- Helper Methods ---

    private string FormatTime(float seconds)
    {
        if (seconds >= 3600000f || float.IsInfinity(seconds) || float.IsNaN(seconds) || seconds < 0)
        {
            return "--:--:--:---";
        }

        try
        {
            TimeSpan t = TimeSpan.FromSeconds(seconds);
            return string.Format("{0:D2}:{1:D2}:{2:D2}:{3:D3}", t.Hours, t.Minutes, t.Seconds, t.Milliseconds);
        }
        catch (OverflowException)
        {
            return "ERR: OVR";
        }
    }

    private void UpdateVectorIfChanged(ref Vector3 lastVal, Vector3 newVal, TextMeshProUGUI label, bool showMag)
    {
        if (label == null) return;
        if (Vector3.Distance(lastVal, newVal) > EPSILON)
        {
            lastVal = newVal;
            string txt = $"{newVal.x:F2}, {newVal.y:F2}, {newVal.z:F2}";
            if (showMag) txt += $" ({newVal.magnitude:F2})";
            label.text = txt;
        }
    }

    private void UpdateIfChanged(ref float lastVal, float newVal, TextMeshProUGUI label, string format)
    {
        if (label != null && Mathf.Abs(lastVal - newVal) > EPSILON)
        {
            lastVal = newVal;
            label.text = newVal.ToString(format);
        }
    }

    private void UpdateIfTimeChanged(ref float lastVal, float newVal, TextMeshProUGUI label)
    {
        if (label != null && Mathf.Abs(lastVal - newVal) > 0.001f)
        {
            lastVal = newVal;
            label.text = FormatTime(newVal);
        }
    }

    private void UpdateText(TextMeshProUGUI label, string text)
    {
        if (label != null) label.text = text;
    }
}