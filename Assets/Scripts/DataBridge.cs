using UnityEngine;

public class DataBridge : MonoBehaviour
{
    public static DataBridge Instance { get; private set; }

    [Header("Performance & Time")]
    public float fps;
    public float ups;
    public float timeSinceStart;

    [Header("Telemetry & Sensors")]
    public float speedMS;
    public float throttle;
    public float steering; // Input [-1, 1]
    public int encoderA, encoderB;
    public Vector3 imuAngles, angAccel, linAccel;

    [Header("Wheel Telemetry")]
    public float currentWheelRPM;    // Average RPM of drive wheels
    public float appliedWheelTorque; // Current torque output (N-m)
    public float steeringAngleRad;   // Current steering angle in Radians

    [Header("Racing")]
    public int lapCount;
    public float currentLapTime, lastLapTime, bestLapTime;

    [Header("LIDAR Data")]
    public float[] lidarRanges; // Array of distances in meters

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); }
        else { Instance = this; }
    }

    void Update()
    {
        timeSinceStart = Time.time;
        fps = 1.0f / Time.unscaledDeltaTime;
    }

    void FixedUpdate()
    {
        ups = 1.0f / Time.fixedDeltaTime;
    }
}