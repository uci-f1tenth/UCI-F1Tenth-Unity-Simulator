using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SocialPlatforms;

public class TCPTunnel : MonoBehaviour
{
    public static TCPTunnel Instance { get; private set; }

    [Header("References")]
    public SceneReset sceneResetObject;
    public TimeStepManager timeStepManager;

    [Header("Network Settings")]
    // TCP only needs one port (Server Port). 
    // We ignore SendPort/IP because we reply to whoever connects to us.
    public TMP_InputField portInput;
    public TextMeshProUGUI buttonLabel;

    [Header("Performance Settings")]
    [Tooltip("How many frames to skip rendering in BUILD mode (30 = 1 render per 30 logic updates)")]
    public int buildRenderInterval = 30;

    [Tooltip("How many frames to skip in EDITOR mode (Keep low to prevent UI lag)")]
    public int editorRenderInterval = 1;

    public int currentRenderInterval = 1;

    // Networking
    private TcpListener server;
    private TcpClient connectedClient;
    private NetworkStream stream;
    private Thread receiveThread;

    public bool isConnected = false;
    private bool isRunning = false;

    // Data Protocol
    // 22 items * 4 bytes = 88 bytes
    private const int HEADER_SIZE = 88;
    private const float MAX_LIDAR_DIST = 20f;
    private byte[] sendPayload;
    // Buffer for reading incoming controls (2 floats = 8 bytes)
    private byte[] receiveBuffer = new byte[8];

    private int lidarCount = 0;
    private uint sequenceId = 0;

    // Thread-safe Reward
    private int _rewardScaled = 0;
    private const float REWARD_SCALE = 1_000_000f;

    // --- THREAD SAFETY QUEUE ---
    private readonly object _lock = new object();
    private float _pendingSteer = 0f;
    private float _pendingThrottle = 0f;
    private bool _hasNewCommand = false;

    public void OnEndStringEdit(string str)
    {
        currentRenderInterval = int.Parse(str);
        Debug.Log($"Updated currentRenderInterval to {currentRenderInterval}!");
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) Destroy(this);
        else Instance = this;

        // Disable VSync and allow for "unlimited fps"
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;

        // Check for editor so that the editor window doesn't slow down horribly
        if (Application.isEditor)
        {
            currentRenderInterval = editorRenderInterval;
        }
        else
        {
            currentRenderInterval = buildRenderInterval;
        }
    }

    private void Update()
    {
        // 1. Check for commands from the background thread
        float steer = 0, throttle = 0;
        bool processCommand = false;

        // 2. Tell the Rendering Engine to sleep
        // The Update() loop (and your network check) still runs every single time!
        if (timeStepManager != null && timeStepManager.mode == TimeStepManager.ControlMode.Manual)
        {
            OnDemandRendering.renderFrameInterval = currentRenderInterval;
        }
        else if (OnDemandRendering.renderFrameInterval > 1)
        {
            OnDemandRendering.renderFrameInterval = 1;
            Debug.Log("OnDemandRendering.renderFrameInterval = 1");
        }

        lock (_lock)
        {
            if (_hasNewCommand)
            {
                steer = _pendingSteer;
                throttle = _pendingThrottle;
                processCommand = true;
                _hasNewCommand = false;
            }
        }

        // 2. Execute on Main Thread
        if (processCommand)
        {
            ProcessControlCommand(steer, throttle);
        }
    }

    private void ProcessControlCommand(float steer, float throttle)
    {
        // RESET SIGNAL: Logic preserved from your snippet
        /*
        if (sceneResetObject != null && steer >= 100f && throttle >= 100f)
        {
            sceneResetObject.ResetCurrentScene();
            sequenceId = 0; 
            return;
        }
        */

        // STANDARD CONTROL
        if (DataBridge.Instance != null)
        {
            DataBridge.Instance.steering = steer;
            DataBridge.Instance.throttle = throttle;
        }

        // PHYSICS STEP (Manual Mode)
        // This calculates physics for exactly one dt and typically triggers SendTelemetry()
        if (timeStepManager != null && timeStepManager.mode == TimeStepManager.ControlMode.Manual)
        {
            timeStepManager.StepManual(timeStepManager.fixedStepSize);
        }
    }

    public void AddReward(float amt) => Interlocked.Add(ref _rewardScaled, (int)(amt * REWARD_SCALE));

    private float GetAndResetReward()
    {
        int val = Interlocked.Exchange(ref _rewardScaled, 0);
        float scaledReward = (float)val / REWARD_SCALE;
        //Debug.Log($"Send reward: {scaledReward}");
        return scaledReward;
    }

    public void ToggleConnection()
    {
        if (!isConnected) StartServer();
        else StopServer();
    }

    private void StartServer()
    {
        try
        {
            // Prepare Lidar & Payload
            var lidar = FindFirstObjectByType<LidarSensor2D>();
            lidarCount = lidar != null ? lidar.numberOfPoints : 0;
            sendPayload = new byte[HEADER_SIZE + (lidarCount * 2)];
            sequenceId = 0;

            // Start TCP Listener
            int port = int.Parse(portInput.text);
            server = new TcpListener(IPAddress.Any, port);
            server.Start();

            isConnected = true;
            isRunning = true;

            // Run the listener/reader in a background thread so Unity doesn't freeze
            receiveThread = new Thread(ListenLoop);
            receiveThread.IsBackground = true;
            receiveThread.Start();

            if (buttonLabel != null) buttonLabel.text = "Stop Server";
            Debug.Log($"[TCP] Server started on port {port}. Waiting for Python...");
        }
        catch (Exception e)
        {
            Debug.LogError($"[TCP] Start Failed: {e.Message}");
            StopServer();
        }
    }

    // Background Thread: Waits for client, then reads data loop
    private void ListenLoop()
    {
        while (isRunning)
        {
            try
            {
                Debug.Log("[TCP] Waiting for a client to connect...");
                connectedClient = server.AcceptTcpClient();
                connectedClient.NoDelay = true;
                stream = connectedClient.GetStream();
                Debug.Log("[TCP] Python Client Connected!");

                // Nested loop to handle the active connection
                while (isRunning && connectedClient.Connected)
                {
                    int bytesRead = 0;
                    // Read 8 bytes (2 floats)
                    while (bytesRead < 8)
                    {
                        int r = stream.Read(receiveBuffer, bytesRead, 8 - bytesRead);
                        if (r == 0) throw new IOException("Client disconnected");
                        bytesRead += r;
                    }

                    float steer = BitConverter.ToSingle(receiveBuffer, 0);
                    float throttle = BitConverter.ToSingle(receiveBuffer, 4);

                    lock (_lock)
                    {
                        _pendingSteer = steer;
                        _pendingThrottle = throttle;
                        _hasNewCommand = true;
                    }
                }
            }
            catch (Exception e)
            {
                if (isRunning)
                    Debug.Log($"[TCP] Client lost ({e.Message}). Waiting for reconnect...");
            }
            finally
            {
                // Clean up the specific client, but keep the outer 'isRunning' loop going
                DisconnectClient();
            }
        }
    }

    public void SendTelemetry()
    {
        // Only write if we have a valid, open connection
        if (!isConnected || stream == null || !stream.CanWrite) return;

        int o = 0; // Packet offset
        var db = DataBridge.Instance;

        // --- PACKING (Same Logic) ---
        Pack(sequenceId, ref o);
        sequenceId++;

        // Motion
        Pack(db.timeSinceStart, ref o);
        Pack(TimeStepManager.fixedDeltaTime, ref o);

        // Agent data
        Pack(db.speedMS, ref o);
        Pack(db.steering, ref o);
        Pack(db.steeringAngleDeg, ref o);
        Pack(db.throttle, ref o);
        Pack(db.wheelTicks, ref o);
        Pack(db.currentWheelRPM, ref o);

        // IMU
        /* // Convert to more common IMU axes
        Vector3 rEuler = new Vector3(db.imuAnglesDeg.z, -db.imuAnglesDeg.x, db.imuAnglesDeg.y);
        Vector3 rLin = new Vector3(db.linAccel.z, -db.linAccel.x, db.linAccel.y);
        Vector3 rAng = new Vector3(db.angAccelDeg.z, -db.angAccelDeg.x, db.angAccelDeg.y);
        */

        // Use Unity's Left Handed Coordinates
        Vector3 rEuler = db.imuAnglesDeg;
        Vector3 rLin = db.linAccel;
        Vector3 rAng = db.angAccelDeg;

        Pack(rEuler.x, ref o); Pack(rEuler.y, ref o); Pack(rEuler.z, ref o);
        Pack(rLin.x, ref o); Pack(rLin.y, ref o); Pack(rLin.z, ref o);
        Pack(rAng.x, ref o); Pack(rAng.y, ref o); Pack(rAng.z, ref o);

        // Status
        Pack(GetAndResetReward(), ref o);
        Pack(db.lapCount, ref o);
        Pack(db.isCrashed ? 1 : 0, ref o);
        Pack(lidarCount, ref o);

        // Lidar
        o = HEADER_SIZE;
        int pointsToPack = Mathf.Min(lidarCount, db.lidarRanges != null ? db.lidarRanges.Length : 0);

        for (int i = 0; i < pointsToPack; i++)
        {
            float dist = Mathf.Clamp(db.lidarRanges[i], 0f, MAX_LIDAR_DIST);
            ushort q = (ushort)((dist / MAX_LIDAR_DIST) * 65535);
            sendPayload[o + (i * 2)] = (byte)(q & 0xFF);
            sendPayload[o + (i * 2) + 1] = (byte)(q >> 8);
        }

        try
        {
            // TCP WRITE
            stream.Write(sendPayload, 0, sendPayload.Length);

            // Clear crash flag
            if (db.isCrashed) db.isCrashed = false;
        }
        catch (Exception)
        {
            // If write fails, the client probably disconnected. 
            // The Read thread will catch the exception and clean up.
        }
    }

    private void Pack(uint v, ref int o) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, sendPayload, o, 4); o += 4; }
    private void Pack(float v, ref int o) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, sendPayload, o, 4); o += 4; }
    private void Pack(int v, ref int o) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, sendPayload, o, 4); o += 4; }

    private void DisconnectClient()
    {
        if (stream != null) { stream.Close(); stream = null; }
        if (connectedClient != null) { connectedClient.Close(); connectedClient = null; }
        Debug.Log("[TCP] Client Disconnected.");
    }

    private void StopServer()
    {
        isRunning = false;
        isConnected = false;

        DisconnectClient();

        if (server != null)
        {
            server.Stop();
            server = null;
        }

        // Abort the thread if it's still stuck waiting for a client
        if (receiveThread != null && receiveThread.IsAlive)
        {
            receiveThread.Abort();
        }

        if (buttonLabel != null) buttonLabel.text = "Start Server";
        Debug.Log("TCP Server Stopped.");
    }

    private void OnDisable() => StopServer();
    private void OnApplicationQuit() => StopServer();
}