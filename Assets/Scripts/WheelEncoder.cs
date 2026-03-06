using UnityEngine;

public class WheelTicksSim : MonoBehaviour, ITimeStepper
{
    [Header("Hardware Configuration")]
    [Tooltip("How many pulses the physical sensor sends per 1 full wheel turn.")]
    public int ticksPerRevolution = 360;

    [Tooltip("How often the data is updated (Hz). Real-world VESC data usually runs at 10Hz to 100Hz.")]
    public float updateRateHz = 50f;

    private double _internalAccumulatedTicks = 0;
    private float _updateTimer = 0f;

    public void TimeStep()
    {
        if (DataBridge.Instance == null) return;

        // 1. Calculate the movement for this specific physics step (to ensure no lost motion)
        float rpm = DataBridge.Instance.currentWheelRPM;
        float ticksPerSecond = (rpm / 60.0f) * ticksPerRevolution;

        // We always accumulate the ticks internally to keep the math perfect
        _internalAccumulatedTicks += ticksPerSecond * TimeStepManager.fixedDeltaTime;
        if (_internalAccumulatedTicks > 1000000000) // Stop overflow from long training runs
        {
            _internalAccumulatedTicks = 0;
        }

        // 2. Control the output rate to the DataBridge
        float interval = 1f / updateRateHz;
        _updateTimer += TimeStepManager.fixedDeltaTime;

        if (_updateTimer >= interval)
        {
            // Update the 'Hardware' signal only at the specified rate
            DataBridge.Instance.wheelTicks = (int)_internalAccumulatedTicks;

            _updateTimer -= interval;
        }
    }
}