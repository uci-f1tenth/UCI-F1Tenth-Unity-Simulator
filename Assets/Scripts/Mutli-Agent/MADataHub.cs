using UnityEngine;

public class MADataHub : MonoBehaviour, ITimeStepper
{
    public static MADataHub Instance { get; private set; }

    // Static References
    private MATimeStepManager timeStepManager;

    [Header("Simulation Configuration")]
    [Tooltip("Number of agents to allocate buffers for")]
    public int numberOfAgents = 2;

    [Header("Network Configuration")]
    public int lidarResolution = 270;

    [Header("Global Performance")]
    public float fps;
    public float ups;
    public float timeSinceStart;

    // --- TELEMETRY ---
    [Header("--- Agent Telemetry ---")]
    public float[] speedMS;
    public float[] throttle;
    public float[] steering;
    public int[] wheelTicks;

    [Header("--- IMU & Physics ---")]
    public Vector3[] imuAnglesDeg;
    public Vector3[] angAccelDeg;
    public Vector3[] linAccelMS;

    [Header("--- Mechanical State ---")]
    public float[] currentWheelRPM;
    public float[] appliedWheelTorque;
    public float[] steeringAngleDeg;

    // --- RACING DATA ---
    [Header("--- Racing Progress ---")]
    public int[] lapCount;
    public float[] currentLapTime;
    public float[] lastLapTime;
    public float[] bestLapTime;

    // --- SENSORS & STATUS ---
    [Header("--- Lidar & Health ---")]
    public float[][] lidarRanges;
    public bool[] isCrashed;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;

        timeStepManager = MATimeStepManager.Instance;

        InitializeBuffers();
    }

    /// <summary>
    /// Allocates memory for all agent arrays based on numberOfAgents.
    /// </summary>
    private void InitializeBuffers()
    {
        // Telemetry
        speedMS = new float[numberOfAgents];
        throttle = new float[numberOfAgents];
        steering = new float[numberOfAgents];
        wheelTicks = new int[numberOfAgents];

        // Physics
        imuAnglesDeg = new Vector3[numberOfAgents];
        angAccelDeg = new Vector3[numberOfAgents];
        linAccelMS = new Vector3[numberOfAgents];

        // Mechanical
        currentWheelRPM = new float[numberOfAgents];
        appliedWheelTorque = new float[numberOfAgents];
        steeringAngleDeg = new float[numberOfAgents];

        // Racing
        lapCount = new int[numberOfAgents];
        currentLapTime = new float[numberOfAgents];
        lastLapTime = new float[numberOfAgents];
        bestLapTime = new float[numberOfAgents];

        // Sensors & Status
        isCrashed = new bool[numberOfAgents];

        lidarRanges = new float[numberOfAgents][];
        for (int i = 0; i < numberOfAgents; i++)
        {
            lidarRanges[i] = new float[lidarResolution];
        }

        Debug.Log($"<color=cyan>MADataHub: Initialized buffers for {numberOfAgents} agents.</color>");
    }

    void Update()
    {
        timeSinceStart = Time.time;
        fps = 1.0f / Time.unscaledDeltaTime;
    }

    public void TimeStep()
    {
        ups = 1.0f / timeStepManager.fixedDeltaTime;
    }
}