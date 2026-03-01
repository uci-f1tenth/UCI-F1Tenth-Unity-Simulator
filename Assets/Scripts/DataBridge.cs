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
    public float steering;
    public int encoderA, encoderB;
    public Vector3 imuAngles, angAccel, linAccel;

    [Header("Racing")]
    public int lapCount;
    public float currentLapTime, lastLapTime, bestLapTime;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); }
        else { Instance = this; }
    }

    void Update()
    {
        // Global Time and FPS tracking (always runs)
        timeSinceStart = Time.time;
        fps = 1.0f / Time.unscaledDeltaTime;
    }

    void FixedUpdate()
    {
        // Global UPS tracking (FixedUpdate rate)
        ups = 1.0f / Time.fixedDeltaTime;
    }
}