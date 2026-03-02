using UnityEngine;
using TMPro;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

public class UDPTunnel : MonoBehaviour
{
    public static UDPTunnel Instance { get; private set; }

    [Header("References")]
    public SceneReset sceneResetObject;
    public TimeStepManager timeStepManager;

    [Header("Network Settings")]
    public TMP_InputField ipInput;
    public TMP_InputField sendPortInput, recvPortInput;
    public TextMeshProUGUI buttonLabel;

    // Networking
    private UdpClient udpClient;
    private IPEndPoint remoteEndPoint;
    public bool isConnected = false;

    // Data Protocol
    // 22 items * 4 bytes = 88 bytes
    private const int HEADER_SIZE = 88;
    private const float MAX_LIDAR_DIST = 20f;
    private byte[] sendPayload;
    private int lidarCount = 0;
    private uint sequenceId = 0;

    // Thread-safe Reward
    private int _rewardScaled = 0;
    private const float REWARD_SCALE = 1000f;

    // --- THREAD SAFETY QUEUE ---
    private readonly object _lock = new object();
    private float _pendingSteer = 0f;
    private float _pendingThrottle = 0f;
    private bool _hasNewCommand = false;

    private void Awake()
    {
        if (Instance != null && Instance != this) Destroy(this);
        else Instance = this;
    }

    private void Update()
    {
        // 1. Check for commands from the background thread
        float steer = 0, throttle = 0;
        bool processCommand = false;

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
        // RESET SIGNAL: If inputs are effectively "Infinity" (>= 100), reset.
        /*
        if (sceneResetObject != null && steer >= 100f && throttle >= 100f)
        {
            sceneResetObject.ResetCurrentScene();
            // Optional: Send a 'fresh' telemetry packet after reset so Python knows we are back at start
            sequenceId = 0; // Reset sequence too?
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
        if (timeStepManager != null && timeStepManager.mode == TimeStepManager.ControlMode.Manual)
        {
            // This calculates physics for exactly one dt
            timeStepManager.StepManual(timeStepManager.fixedStepSize);
        }
    }

    public void AddReward(float amt) => Interlocked.Add(ref _rewardScaled, (int)(amt * REWARD_SCALE));

    private float GetAndResetReward()
    {
        int val = Interlocked.Exchange(ref _rewardScaled, 0);
        return (float)val / REWARD_SCALE;
    }

    public void ToggleConnection()
    {
        if (!isConnected) StartConnection();
        else StopConnection();
    }

    private void StartConnection()
    {
        try
        {
            var lidar = FindFirstObjectByType<LidarSensor2D>();
            lidarCount = lidar != null ? lidar.numberOfPoints : 0;

            // Allocation
            sendPayload = new byte[HEADER_SIZE + (lidarCount * 2)];
            sequenceId = 0;

            udpClient = new UdpClient();

            // --- FIX FOR "EXISTING CONNECTION CLOSED" ERROR ---
            // SIO_UDP_CONNRESET = -1744830452
            // This prevents the socket from closing if Python stops listening briefly.
            try
            {
                udpClient.Client.IOControl((IOControlCode)(-1744830452), new byte[] { 0 }, null);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UDP] Could not set SIO_UDP_CONNRESET: {e.Message}");
            }

            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            int rPort = int.Parse(recvPortInput.text);
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, rPort));

            string ip = ipInput.text;
            int sPort = int.Parse(sendPortInput.text);
            remoteEndPoint = new IPEndPoint(IPAddress.Parse(ip), sPort);

            udpClient.BeginReceive(OnReceive, null);

            isConnected = true;
            if (buttonLabel != null) buttonLabel.text = "Disconnect";
            Debug.Log($"[UDP] Listening on {rPort}, Target: {ip}:{sPort}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[UDP] Connection Failed: {e.Message}");
            StopConnection();
        }
    }

    public void SendTelemetry()
    {
        if (!isConnected || udpClient == null) return;

        int o = 0;
        var db = DataBridge.Instance;

        // --- PACKING (88 Bytes Header) ---
        Pack(sequenceId, ref o);
        sequenceId++;

        // Motion
        Pack(db.timeSinceStart, ref o);
        Pack(TimeStepManager.fixedDeltaTime, ref o);
        Pack(db.speedMS, ref o);
        Pack(db.steering, ref o);
        Pack(db.steeringAngleRad, ref o);
        Pack(db.throttle, ref o);
        Pack(db.wheelTicks, ref o);
        Pack(db.currentWheelRPM, ref o);

        // IMU
        Vector3 rEuler = new Vector3(db.imuAngles.z, -db.imuAngles.x, db.imuAngles.y);
        Vector3 rLin = new Vector3(db.linAccel.z, -db.linAccel.x, db.linAccel.y);
        Vector3 rAng = new Vector3(db.angAccel.z, -db.angAccel.x, db.angAccel.y);

        Pack(rEuler.x, ref o); Pack(rEuler.y, ref o); Pack(rEuler.z, ref o);
        Pack(rLin.x, ref o); Pack(rLin.y, ref o); Pack(rLin.z, ref o);
        Pack(rAng.x, ref o); Pack(rAng.y, ref o); Pack(rAng.z, ref o);

        // Status
        Pack(GetAndResetReward(), ref o);
        Pack(db.lapCount, ref o);
        Pack(db.isCrashed ? 1 : 0, ref o);
        Pack(lidarCount, ref o);

        // Lidar
        o = HEADER_SIZE; // Safety align
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
            udpClient.Send(sendPayload, sendPayload.Length, remoteEndPoint);
            // Clear crash flag after sending so we don't spam it
            if (db.isCrashed) db.isCrashed = false;
        }
        catch { }
    }

    private void Pack(uint v, ref int o) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, sendPayload, o, 4); o += 4; }
    private void Pack(float v, ref int o) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, sendPayload, o, 4); o += 4; }
    private void Pack(int v, ref int o) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, sendPayload, o, 4); o += 4; }

    private void OnReceive(IAsyncResult ar)
    {
        if (!isConnected || udpClient == null) return;

        try
        {
            IPEndPoint ip = new IPEndPoint(IPAddress.Any, 0);
            byte[] b = udpClient.EndReceive(ar, ref ip);

            if (b.Length >= 8)
            {
                float steer = BitConverter.ToSingle(b, 0);
                float throttle = BitConverter.ToSingle(b, 4);

                // --- THREAD SAFE HANDOFF ---
                // Do NOT call Unity API here. Just store the data.
                lock (_lock)
                {
                    _pendingSteer = steer;
                    _pendingThrottle = throttle;
                    _hasNewCommand = true;
                }
            }
        }
        catch (ObjectDisposedException) { return; }
        catch (Exception e) { Debug.LogWarning($"UDP Recv Error: {e.Message}"); }
        finally
        {
            if (isConnected && udpClient != null)
            {
                try { udpClient.BeginReceive(OnReceive, null); } catch { }
            }
        }
    }

    private void StopConnection()
    {
        isConnected = false;
        if (udpClient != null)
        {
            udpClient.Close();
            udpClient = null;
        }
        if (buttonLabel != null) buttonLabel.text = "Connect";
        Debug.Log("UDP Tunnel Disconnected.");
    }

    private void OnDisable() => StopConnection();
    private void OnApplicationQuit() => StopConnection();
}