using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

public class MATCPTunnel : MonoBehaviour
{
    public static MATCPTunnel Instance { get; private set; }

    // Static References
    private MADataHub dataHub;
    private MATimeStepManager timeStepManager;
    private MASceneReset sceneResetObject;

    [Header("--- UI References ---")]
    public TMP_InputField ipInput;
    public TMP_InputField portInput;
    public TextMeshProUGUI buttonLabel;

    [Header("--- Sensor Settings ---")]
    [Tooltip("Maximum range for Lidar in meters. Should match your Agent's Lidar script.")]
    public float maxLidarDist = 20f;

    [Header("--- Performance ---")]
    [Tooltip("Render every Nth frame in Build (30 = 1fps visual, 60fps physics)")]
    public int buildRenderInterval = 30;
    [Tooltip("Render every Nth frame in Editor (Keep low, e.g., 1 or 2)")]
    public int editorRenderInterval = 1;

    // --- NETWORKING STATE ---
    private TcpListener server;
    private TcpClient connectedClient;
    private NetworkStream stream;
    private Thread receiveThread;

    public bool isConnected = false;
    private bool isRunning = false;
    private uint sequenceId = 0;

    // --- DATA BUFFERS ---
    // Header: SeqID(4) + Time(4) + DT(4) + AgentCount(4) = 16 bytes
    private const int GLOBAL_HEADER_SIZE = 16;

    private byte[] sendPayload;
    private byte[] receiveBuffer; // Buffer for incoming controls

    // --- THREAD SAFETY ---
    private readonly object _lock = new object();

    // Command Buffers (One slot per agent)
    private float[] _pendingSteer;
    private float[] _pendingThrottle;
    private bool _hasNewCommand = false;

    // Reward Buffers (Accumulated between steps)
    private float[] _accumulatedRewards;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        // Framerate Unlocking
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;

        // Optimize Rendering
        OnDemandRendering.renderFrameInterval = Application.isEditor ? editorRenderInterval : buildRenderInterval;
    }

    private void Start()
    {
        // Ensure we have a hub reference
        if (dataHub == null) dataHub = MADataHub.Instance;
        if (timeStepManager == null) timeStepManager = MATimeStepManager.Instance;
        if (sceneResetObject == null) sceneResetObject = MASceneReset.Instance;
    }

    public void OnEndStringEdit(string str)
    {
        int newRenderInterval = int.Parse(str);
        OnDemandRendering.renderFrameInterval = newRenderInterval;
        Debug.Log($"Updated currentRenderInterval to {newRenderInterval}!");
    }

    // =================================================================================================
    //                                      MAIN THREAD LOOP
    // =================================================================================================
    private void Update()
    {
        // 1. Check for incoming commands from the Network Thread
        bool process = false;

        lock (_lock)
        {
            if (_hasNewCommand)
            {
                // Copy commands to DataHub immediately
                Array.Copy(_pendingSteer, dataHub.steering, dataHub.numberOfAgents);
                Array.Copy(_pendingThrottle, dataHub.throttle, dataHub.numberOfAgents);

                process = true;
                _hasNewCommand = false;
            }
        }

        // 2. Step the Physics (Manual Mode)
        // This triggers the physics engine to advance one fixedDeltaTime
        if (process && timeStepManager != null && timeStepManager.mode == MATimeStepManager.ControlMode.Manual)
        {
            timeStepManager.StepManual(timeStepManager.fixedStepSize);
        }
    }

    // =================================================================================================
    //                                      PUBLIC API
    // =================================================================================================

    /// <summary>
    /// Thread-safe method to add reward to a specific agent.
    /// Call this from collisions or checkpoints.
    /// </summary>
    public void AddReward(int agentIndex, float amount)
    {
        if (_accumulatedRewards == null || agentIndex >= _accumulatedRewards.Length) return;

        lock (_lock)
        {
            _accumulatedRewards[agentIndex] += amount;
        }
    }

    public void ToggleConnection()
    {
        if (!isConnected) StartServer();
        else StopServer();
    }

    // =================================================================================================
    //                                      SERVER LOGIC
    // =================================================================================================

    private void StartServer()
    {
        try
        {
            int numAgents = dataHub.numberOfAgents;

            // Lidar count comes from the first initialized array in the Hub
            int lidarPoints = (dataHub.lidarRanges != null && dataHub.lidarRanges.Length > 0)
                              ? dataHub.lidarRanges[0].Length
                              : 360;

            // 1. Initialize Thread-Safe Buffers
            _pendingSteer = new float[numAgents];
            _pendingThrottle = new float[numAgents];
            _accumulatedRewards = new float[numAgents];
            receiveBuffer = new byte[numAgents * 8]; // 2 floats (8 bytes) per agent

            // 2. Calculate Payload Size
            // Metadata per agent: ~88 bytes. Lidar: points * 2 bytes.
            int agentMetaSize = 72; // Removed lap data, now 72 bytes
            int agentLidarSize = lidarPoints * 2;
            int totalSize = GLOBAL_HEADER_SIZE + (numAgents * (agentMetaSize + agentLidarSize));

            sendPayload = new byte[totalSize];
            Debug.Log($"[TCP] Payload Size: {totalSize / 1024} KB per step. (Max Lidar: {maxLidarDist}m)");

            // 3. Start Listener
            int port = int.Parse(portInput.text);
            server = new TcpListener(IPAddress.Any, port);
            server.Start();

            isRunning = true;
            isConnected = true;
            sequenceId = 0;

            // 4. Start Background Thread
            receiveThread = new Thread(ListenLoop);
            receiveThread.IsBackground = true;
            receiveThread.Start();

            if (buttonLabel) buttonLabel.text = "Stop Server";
            Debug.Log($"[TCP] Multi-Agent Server started on port {port}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[TCP] Failed to start: {e.Message}");
            StopServer();
        }
    }

    private void ListenLoop()
    {
        while (isRunning)
        {
            try
            {
                // Reset connection state
                if (connectedClient != null) connectedClient.Close();

                Debug.Log("[TCP] Waiting for client...");
                connectedClient = server.AcceptTcpClient();
                connectedClient.NoDelay = true;
                stream = connectedClient.GetStream();
                Debug.Log("[TCP] Client Connected!");

                while (isRunning && connectedClient.Connected)
                {
                    // Read Action Packet: [Steer0, Throt0, Steer1, Throt1, ...]
                    int bytesExpected = receiveBuffer.Length;
                    int bytesRead = 0;

                    while (bytesRead < bytesExpected)
                    {
                        int r = stream.Read(receiveBuffer, bytesRead, bytesExpected - bytesRead);
                        if (r == 0) throw new IOException("Disconnected");
                        bytesRead += r;
                    }

                    // Deserialize Controls
                    lock (_lock)
                    {
                        for (int i = 0; i < dataHub.numberOfAgents; i++)
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
                if (isRunning) Debug.Log($"[TCP] Connection lost: {e.Message}");
            }
        }
    }

    // =================================================================================================
    //                                      SEND TELEMETRY
    // =================================================================================================

    // Call this AFTER physics step and Lidar generation are complete
    public void SendTelemetry()
    {
        if (!isConnected || stream == null || !stream.CanWrite) return;

        int o = 0; // Offset

        // --- 1. GLOBAL HEADER (16 Bytes) ---
        Pack(sequenceId, ref o); sequenceId++;
        Pack(dataHub.timeSinceStart, ref o);
        Pack(TimeStepManager.fixedDeltaTime, ref o);
        Pack(dataHub.numberOfAgents, ref o);

        // --- 2. PER AGENT LOOP ---
        for (int i = 0; i < dataHub.numberOfAgents; i++)
        {
            // A. Physics & Inputs [6 * 4]
            Pack(dataHub.speedMS[i], ref o);
            Pack(dataHub.throttle[i], ref o);
            Pack(dataHub.steering[i], ref o);
            Pack(dataHub.steeringAngleDeg[i], ref o);
            Pack(dataHub.currentWheelRPM[i], ref o);
            Pack(dataHub.appliedWheelTorque[i], ref o);

            // B. IMU (Euler, LinAccel, AngAccel) [9 * 4]
            PackVec3(dataHub.imuAnglesDeg[i], ref o);
            PackVec3(dataHub.linAccelMS[i], ref o);
            PackVec3(dataHub.angAccelDeg[i], ref o);

            // C. Racing Logic [4 * 4]
            /*
            Pack(dataHub.lapCount[i], ref o);
            Pack(dataHub.currentLapTime[i], ref o);
            Pack(dataHub.lastLapTime[i], ref o);
            Pack(dataHub.bestLapTime[i], ref o);
            */

            // D. Crash data [1 * 4]
            Pack(dataHub.isCrashed[i] ? 1 : 0, ref o); // Send crash as int (1/0)

            // E. Rewards (Read & Reset Atomically) [1 * 4]
            float rewardToSend = 0f;
            lock (_lock)
            {
                rewardToSend = _accumulatedRewards[i];
                _accumulatedRewards[i] = 0f; // Reset for next step
            }
            Pack(rewardToSend, ref o);

            // F. Lidar [1 * 4 + length * 2]
            float[] scan = dataHub.lidarRanges[i];
            int pointCount = scan.Length;
            Pack(pointCount, ref o); // Header for lidar count

            for (int j = 0; j < pointCount; j++)
            {
                // Use the new configurable maxLidarDist
                float dist = Mathf.Clamp(scan[j], 0f, maxLidarDist);

                // Compress to UShort (0-65535)
                ushort compressed = (ushort)((dist / maxLidarDist) * 65535);

                sendPayload[o] = (byte)(compressed & 0xFF);
                sendPayload[o + 1] = (byte)(compressed >> 8);
                o += 2;
            }
        }

        // --- 3. WRITE TO SOCKET ---
        try
        {
            stream.Write(sendPayload, 0, sendPayload.Length);

            // Reset status flags after sending
            for (int i = 0; i < dataHub.numberOfAgents; i++)
            {
                dataHub.isCrashed[i] = false;
            }
        }
        catch (Exception)
        {
            // Client likely disconnected
        }
    }

    // --- Helpers ---
    private void Pack(float v, ref int o)
    {
        var b = BitConverter.GetBytes(v);
        sendPayload[o] = b[0]; sendPayload[o + 1] = b[1]; sendPayload[o + 2] = b[2]; sendPayload[o + 3] = b[3];
        o += 4;
    }
    private void Pack(int v, ref int o)
    {
        var b = BitConverter.GetBytes(v);
        sendPayload[o] = b[0]; sendPayload[o + 1] = b[1]; sendPayload[o + 2] = b[2]; sendPayload[o + 3] = b[3];
        o += 4;
    }
    private void Pack(uint v, ref int o)
    {
        var b = BitConverter.GetBytes(v);
        sendPayload[o] = b[0]; sendPayload[o + 1] = b[1]; sendPayload[o + 2] = b[2]; sendPayload[o + 3] = b[3];
        o += 4;
    }
    private void PackVec3(Vector3 v, ref int o)
    {
        Pack(v.x, ref o); Pack(v.y, ref o); Pack(v.z, ref o);
    }

    private void StopServer()
    {
        isRunning = false;
        isConnected = false;
        if (server != null) server.Stop();
        if (receiveThread != null && receiveThread.IsAlive) receiveThread.Abort();
        if (buttonLabel) buttonLabel.text = "Start Server";
    }

    private void OnDisable() => StopServer();
    private void OnApplicationQuit() => StopServer();
}