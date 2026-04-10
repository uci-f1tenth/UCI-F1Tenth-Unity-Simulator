using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI; // Required for the Button component

public class MATCPTunnel : MonoBehaviour
{
    public static MATCPTunnel Instance { get; private set; }

    // =================================================================
    //                        STATIC REFERENCES
    // =================================================================
    private MADataHub dataHub;
    private MATimeStepManager timeStepManager;
    private MASceneReset sceneResetObject;

    // =================================================================
    //                        INSPECTOR VARIABLES
    // =================================================================
    [Header("--- UI References ---")]
    [Tooltip("Input field for IP Address (currently defaults to Any internally)")]
    public TMP_InputField ipInput;
    [Tooltip("Input field for the port number")]
    public TMP_InputField portInput;
    [Tooltip("Input field for the render frame interval")]
    public TMP_InputField renderFrameIntervalInput;
    [Tooltip("The main button used to toggle the server on and off")]
    public Button connectButton;
    [Tooltip("The text component inside the connect button")]
    public TextMeshProUGUI buttonLabel;

    [Header("--- Sensor Settings ---")]
    [Tooltip("Maximum range for Lidar in meters. Should match your Agent's Lidar script.")]
    public float maxLidarDist = 20f;

    // =================================================================
    //                        NETWORKING STATE
    // =================================================================
    private TcpListener server;
    private TcpClient connectedClient;
    private NetworkStream stream;
    private Thread receiveThread;

    public bool isConnected { get; private set; } = false;
    private bool isRunning = false;
    private uint sequenceId = 0;

    // =================================================================
    //                          DATA BUFFERS
    // =================================================================
    private byte[] sendPayload;
    private byte[] receiveBuffer;

    // =================================================================
    //                          THREAD SAFETY
    // =================================================================
    private readonly object _lock = new object();

    // Command Buffers (One slot per agent)
    private float[] _pendingSteer;
    private float[] _pendingThrottle;
    private bool _hasNewCommand = false;

    // Reward Buffers (Accumulated between steps)
    private float[] _accumulatedRewards;

    // =================================================================
    //                      UNITY LIFECYCLE (INIT)
    // =================================================================
    private void Awake()
    {
        // Enforce Singleton
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        // Framerate Unlocking for headless/fast training
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
    }

    private void Start()
    {
        // 1. Resolve Dependencies
        dataHub = MADataHub.Instance;
        timeStepManager = MATimeStepManager.Instance;
        sceneResetObject = MASceneReset.Instance;

        if (dataHub == null) Debug.LogWarning("[TCP] MADataHub is missing!");
        if (timeStepManager == null) Debug.LogWarning("[TCP] MATimeStepManager is missing!");

        // 2. Setup UI Listeners
        SetupUI();
    }

    private void SetupUI()
    {
        // Render Interval Listener
        if (renderFrameIntervalInput != null)
        {
            renderFrameIntervalInput.text = OnDemandRendering.renderFrameInterval.ToString();
            renderFrameIntervalInput.onEndEdit.AddListener(OnEndStringEdit);
        }

        // Connection Toggle Listener
        if (connectButton != null)
        {
            // Remove existing listeners just in case, then add the toggle method
            connectButton.onClick.RemoveAllListeners();
            connectButton.onClick.AddListener(ToggleConnection);
        }
        else
        {
            Debug.LogWarning("[TCP] Connect button reference is missing in the inspector.");
        }

        UpdateButtonUI();
    }

    // =================================================================
    //                      MAIN THREAD LOOP (UPDATE)
    // =================================================================
    private void Update()
    {
        // 1. Check for incoming commands from the Network Thread
        bool process = false;

        lock (_lock)
        {
            if (_hasNewCommand && dataHub != null)
            {
                // Ensure array sizes match before copying to prevent out-of-bounds exceptions
                if (_pendingSteer.Length == dataHub.numberOfAgents)
                {
                    Array.Copy(_pendingSteer, dataHub.steering, dataHub.numberOfAgents);
                    Array.Copy(_pendingThrottle, dataHub.throttle, dataHub.numberOfAgents);
                    process = true;
                }
                _hasNewCommand = false;
            }
        }

        // 2. Step the Physics (Manual Mode)
        if (process && timeStepManager != null && timeStepManager.mode == MATimeStepManager.ControlMode.Manual)
        {
            timeStepManager.StepManual(timeStepManager.fixedStepSize);
        }
    }

    // =================================================================
    //                            PUBLIC API
    // =================================================================

    /// <summary>
    /// Thread-safe method to add reward to a specific agent.
    /// Call this from collisions or checkpoints.
    /// </summary>
    public void AddReward(int agentIndex, float amount)
    {
        if (_accumulatedRewards == null || agentIndex < 0 || agentIndex >= _accumulatedRewards.Length) return;

        lock (_lock)
        {
            _accumulatedRewards[agentIndex] += amount;
        }
    }

    /// <summary>
    /// Toggles the TCP Server state. Bound to the Connect Button.
    /// </summary>
    public void ToggleConnection()
    {
        if (!isConnected) StartServer();
        else StopServer();
    }

    /// <summary>
    /// Updates render intervals dynamically from UI input.
    /// </summary>
    public void OnEndStringEdit(string str)
    {
        if (int.TryParse(str, out int parsedVal))
        {
            int newRenderInterval = Math.Max(parsedVal, 1);
            OnDemandRendering.renderFrameInterval = newRenderInterval;
            renderFrameIntervalInput.text = OnDemandRendering.renderFrameInterval.ToString();
            Debug.Log($"[TCP] Updated renderFrameInterval to {newRenderInterval}");
        }
        else
        {
            Debug.LogWarning("[TCP] Invalid input for render interval.");
            renderFrameIntervalInput.text = OnDemandRendering.renderFrameInterval.ToString();
        }
    }

    // =================================================================
    //                            SERVER LOGIC
    // =================================================================

    private void StartServer()
    {
        if (dataHub == null)
        {
            Debug.LogError("[TCP] Cannot start server: MADataHub is missing.");
            return;
        }

        try
        {
            int numAgents = dataHub.numberOfAgents;
            if (numAgents <= 0)
            {
                Debug.LogWarning("[TCP] No agents found in DataHub. Cannot start server.");
                return;
            }

            // Determine Lidar points safely
            int lidarPoints = 360; // Default fallback
            if (dataHub.lidarRanges != null && dataHub.lidarRanges.Length > 0 && dataHub.lidarRanges[0] != null)
            {
                lidarPoints = dataHub.lidarRanges[0].Length;
            }

            // 1. Initialize Thread-Safe Buffers
            _pendingSteer = new float[numAgents];
            _pendingThrottle = new float[numAgents];
            _accumulatedRewards = new float[numAgents];
            receiveBuffer = new byte[numAgents * 8]; // 2 floats (8 bytes) per agent

            // 2. Calculate Payload Size
            int GLOBAL_HEADER_SIZE = 36; // Sequence, Time, DT, AgentCount + 5 Lidar variables
            int agentMetaSize = 76;      // Physics, IMU, Crash, Reward, LidarCount + 1 WheelTicks
            int agentLidarSize = lidarPoints * 2; // 2 bytes per point (UShort)

            int totalSize = GLOBAL_HEADER_SIZE + (numAgents * (agentMetaSize + agentLidarSize));

            sendPayload = new byte[totalSize];
            Debug.Log($"[TCP] Payload Size: {totalSize / 1024f:F2} KB per step. (Max Lidar: {maxLidarDist}m)");

            // 3. Parse Port and Start Listener
            if (!int.TryParse(portInput.text, out int port)) port = 5005; // Fallback port

            server = new TcpListener(IPAddress.Any, port);
            server.Start();

            isRunning = true;
            isConnected = true;
            sequenceId = 0;
            UpdateButtonUI();

            // 4. Start Background Thread
            receiveThread = new Thread(ListenLoop);
            receiveThread.IsBackground = true;
            receiveThread.Start();

            Debug.Log($"[TCP] Multi-Agent Server started on port {port}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[TCP] Failed to start server: {e.Message}");
            StopServer();
        }
    }

    private void ListenLoop()
    {
        while (isRunning)
        {
            try
            {
                if (connectedClient != null) connectedClient.Close();

                Debug.Log("[TCP] Waiting for client connection...");
                connectedClient = server.AcceptTcpClient();
                connectedClient.NoDelay = true; // Crucial for low-latency RL
                stream = connectedClient.GetStream();
                Debug.Log("[TCP] Client Connected successfully!");

                while (isRunning && connectedClient.Connected)
                {
                    int bytesExpected = receiveBuffer.Length;
                    int bytesRead = 0;

                    // Read Action Packet: [Steer0, Throt0, Steer1, Throt1, ...]
                    while (bytesRead < bytesExpected)
                    {
                        int r = stream.Read(receiveBuffer, bytesRead, bytesExpected - bytesRead);
                        if (r == 0) throw new IOException("Client disconnected cleanly.");
                        bytesRead += r;
                    }

                    // Deserialize Controls
                    lock (_lock)
                    {
                        for (int i = 0; i < _pendingSteer.Length; i++)
                        {
                            int offset = i * 8;
                            _pendingSteer[i] = BitConverter.ToSingle(receiveBuffer, offset);
                            _pendingThrottle[i] = BitConverter.ToSingle(receiveBuffer, offset + 4);
                        }
                        _hasNewCommand = true;
                    }
                }
            }
            catch (Exception e)
            {
                if (isRunning) Debug.LogWarning($"[TCP] Connection lost or reset: {e.Message}");
            }
        }

        Debug.LogError($"[TCP] Listen Loop Died!");
    }

    private void StopServer()
    {
        isRunning = false;
        isConnected = false;

        if (stream != null) stream.Close();
        if (connectedClient != null) connectedClient.Close();
        if (server != null) server.Stop();

        // Abort thread gracefully if needed
        if (receiveThread != null && receiveThread.IsAlive)
        {
            receiveThread.Abort();
        }

        UpdateButtonUI();
        Debug.Log("[TCP] Server stopped.");
    }

    private void UpdateButtonUI()
    {
        if (buttonLabel != null)
        {
            buttonLabel.text = isConnected ? "Stop Server" : "Start Server";
            buttonLabel.color = isConnected ? Color.red : Color.black;
        }
    }

    // =================================================================
    //                          SEND TELEMETRY
    // =================================================================

    /// <summary>
    /// Packages all environment state and writes it to the socket.
    /// Call this AFTER the physics step and Lidar generation are complete.
    /// </summary>
    public void SendTelemetry()
    {
        if (!isConnected || stream == null || !stream.CanWrite || dataHub == null) return;

        int o = 0; // Payload Offset

        try
        {
            // --- 1. GLOBAL HEADER (36 Bytes) ---
            Pack(sequenceId, ref o); sequenceId++;
            Pack(dataHub.timeSinceStart, ref o);
            Pack(timeStepManager.fixedDeltaTime, ref o);
            Pack(dataHub.numberOfAgents, ref o);

            Pack(dataHub.lidarMinAngleDeg, ref o);
            Pack(dataHub.lidarMaxAngleDeg, ref o);
            Pack(dataHub.lidarMinRange, ref o);
            Pack(dataHub.lidarMaxRange, ref o);
            Pack((int)dataHub.lidarScanDirection, ref o); // Cast enum/bool to int

            // --- 2. PER AGENT LOOP (76 Bytes Metadata + Lidar Array) ---
            for (int i = 0; i < dataHub.numberOfAgents; i++)
            {
                // A. Physics & Inputs [6 floats = 24 bytes]
                Pack(dataHub.speedMS[i], ref o);
                Pack(dataHub.throttle[i], ref o);
                Pack(dataHub.steering[i], ref o);
                Pack(dataHub.steeringAngleDeg[i], ref o);
                Pack(dataHub.currentWheelRPM[i], ref o);
                Pack(dataHub.appliedWheelTorque[i], ref o);

                // B. Wheels [1 int = 4 bytes]
                Pack(dataHub.wheelTicks[i], ref o);

                // C. IMU Data [9 floats = 36 bytes]
                PackVec3(dataHub.imuAnglesDeg[i], ref o);
                PackVec3(dataHub.linAccelMS[i], ref o);
                PackVec3(dataHub.angAccelDeg[i], ref o);

                // D. Status & Reward [1 int, 1 float = 8 bytes]
                Pack(dataHub.isCrashed[i] ? 1 : 0, ref o);

                float rewardToSend = 0f;
                lock (_lock)
                {
                    rewardToSend = _accumulatedRewards[i];
                    _accumulatedRewards[i] = 0f;
                }
                Pack(rewardToSend, ref o);

                // E. Lidar Header [1 int = 4 bytes]
                float[] scan = dataHub.lidarRanges[i];
                int pointCount = scan != null ? scan.Length : 0;
                Pack(pointCount, ref o);

                // Dynamic Compression using GLOBAL max range
                float maxRange = Mathf.Max(dataHub.lidarMaxRange, 0.001f);
                for (int j = 0; j < pointCount; j++)
                {
                    float dist = Mathf.Clamp(scan[j], 0f, maxRange);
                    ushort compressed = (ushort)((dist / maxRange) * 65535);

                    sendPayload[o] = (byte)(compressed & 0xFF);
                    sendPayload[o + 1] = (byte)(compressed >> 8);
                    o += 2;
                }
            }

            // --- 3. WRITE TO SOCKET ---
            stream.Write(sendPayload, 0, sendPayload.Length);

            // Reset status flags after sending
            for (int i = 0; i < dataHub.numberOfAgents; i++)
            {
                dataHub.isCrashed[i] = false;
            }
        }
        catch (Exception)
        {
            // Client likely disconnected mid-write; ListenLoop will catch this
        }
    }

    // =================================================================
    //                      PACKING HELPERS
    // =================================================================

    // Efficiency Note: BitConverter.GetBytes allocates a new array every call.
    // By using TryWriteBytes or manual bit shifting, we prevent garbage collection spikes.

    private unsafe void Pack(float v, ref int o)
    {
        // High performance float to byte packing
        uint intBits = *(uint*)&v;
        sendPayload[o] = (byte)intBits;
        sendPayload[o + 1] = (byte)(intBits >> 8);
        sendPayload[o + 2] = (byte)(intBits >> 16);
        sendPayload[o + 3] = (byte)(intBits >> 24);
        o += 4;
    }

    private void Pack(int v, ref int o)
    {
        sendPayload[o] = (byte)v;
        sendPayload[o + 1] = (byte)(v >> 8);
        sendPayload[o + 2] = (byte)(v >> 16);
        sendPayload[o + 3] = (byte)(v >> 24);
        o += 4;
    }

    private void Pack(uint v, ref int o)
    {
        sendPayload[o] = (byte)v;
        sendPayload[o + 1] = (byte)(v >> 8);
        sendPayload[o + 2] = (byte)(v >> 16);
        sendPayload[o + 3] = (byte)(v >> 24);
        o += 4;
    }

    private void PackVec3(Vector3 v, ref int o)
    {
        Pack(v.x, ref o); Pack(v.y, ref o); Pack(v.z, ref o);
    }

    // =================================================================
    //                       CLEANUP ON EXIT
    // =================================================================
    private void OnDisable() => StopServer();
    private void OnApplicationQuit() => StopServer();
}