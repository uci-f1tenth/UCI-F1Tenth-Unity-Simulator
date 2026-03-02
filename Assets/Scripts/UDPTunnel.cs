using UnityEngine;
using TMPro;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

public class UDPTunnel : MonoBehaviour
{
    public static UDPTunnel Instance { get; private set; }

    public SceneReset sceneResetObject;
    public TimeStepManager timeStepManager;

    [Header("Network Settings")]
    public TMP_InputField ipInput;
    public TMP_InputField sendPortInput, recvPortInput;
    public TextMeshProUGUI buttonLabel;

    private UdpClient udpClient;
    private IPEndPoint remoteEndPoint;
    public bool isConnected = false;

    // INCREASED HEADER SIZE: 84 (Old) + 4 (SeqID) = 88 bytes
    private const int HEADER_SIZE = 88;
    private const float MAX_LIDAR_DIST = 20f;
    private byte[] sendPayload;
    private int lidarCount = 0;

    // --- Sequence Logic ---
    private uint sequenceId = 0; // Simple counter for ordering

    // Thread-safe Reward
    private int _rewardScaled = 0;
    private const float REWARD_SCALE = 1000f;

    private void Awake()
    {
        if (Instance != null && Instance != this) Destroy(this);
        else Instance = this;
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

            // Re-allocate payload buffer to fit new Header size
            sendPayload = new byte[HEADER_SIZE + (lidarCount * 2)];

            // Reset Sequence ID on new connection so Python starts fresh
            sequenceId = 0;

            // --- Socket Reuse Logic ---
            udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            int rPort = int.Parse(recvPortInput.text);
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, rPort));

            string ip = ipInput.text;
            int sPort = int.Parse(sendPortInput.text);
            remoteEndPoint = new IPEndPoint(IPAddress.Parse(ip), sPort);

            udpClient.BeginReceive(OnReceive, null);

            isConnected = true;
            if (buttonLabel != null) buttonLabel.text = "Disconnect";

            Debug.Log($"[UDP] Connected. SeqID reset. Target: {ip}:{sPort}");
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

        // --- 0. Sequence ID (The Reliability Layer) ---
        // Increments every single frame. Python checks this to detect packet loss/ordering.
        Pack(sequenceId, ref o);
        sequenceId++;

        // --- 1. System & Basic Motion ---
        Pack(db.timeSinceStart, ref o);
        Pack(TimeStepManager.fixedDeltaTime, ref o);
        Pack(db.speedMS, ref o);
        Pack(db.steering, ref o);
        Pack(db.steeringAngleRad, ref o);
        Pack(db.throttle, ref o);
        Pack(db.wheelTicks, ref o);
        Pack(db.currentWheelRPM, ref o);

        // --- 2. IMU Conversion ---
        Vector3 rEuler = new Vector3(db.imuAngles.z, -db.imuAngles.x, db.imuAngles.y);
        Vector3 rLin = new Vector3(db.linAccel.z, -db.linAccel.x, db.linAccel.y);
        Vector3 rAng = new Vector3(db.angAccel.z, -db.angAccel.x, db.angAccel.y);

        Pack(rEuler.x, ref o); Pack(rEuler.y, ref o); Pack(rEuler.z, ref o);
        Pack(rLin.x, ref o); Pack(rLin.y, ref o); Pack(rLin.z, ref o);
        Pack(rAng.x, ref o); Pack(rAng.y, ref o); Pack(rAng.z, ref o);

        // --- 3. Scoring & Status ---
        Pack(GetAndResetReward(), ref o);
        Pack(db.lapCount, ref o);
        Pack(db.isCrashed ? 1 : 0, ref o);
        Pack(lidarCount, ref o);

        // --- 4. Lidar (Quantized) ---
        // Ensure offset matches the new HEADER_SIZE (88)
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
            udpClient.Send(sendPayload, sendPayload.Length, remoteEndPoint);
            db.isCrashed = false;
        }
        catch { /* Transient network error */ }
    }

    // --- Helpers ---
    // Overload for uint (Sequence ID)
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

            // Basic packet validation
            if (b.Length >= 8)
            {
                float steer = BitConverter.ToSingle(b, 0);
                float throttle = BitConverter.ToSingle(b, 4);

                // --- Input Handling ---
                // We pass this to the bridge, which the TimeStepManager will eventually read
                DataBridge.Instance.steering = steer;
                DataBridge.Instance.throttle = throttle;

                // --- Trigger Manual Step (If in Manual Mode) ---
                // This is the CRITICAL link for your TimeStepManager
                if (timeStepManager != null && timeStepManager.mode == TimeStepManager.ControlMode.Manual)
                {
                    // Trigger the physics step immediately upon receiving the command
                    timeStepManager.StepManual(timeStepManager.fixedStepSize);
                }

                // Reset Check
                if (sceneResetObject != null && steer >= 100.0f && throttle >= 100.0f)
                {
                    sceneResetObject.ResetCurrentScene();
                }
            }
        }
        catch (ObjectDisposedException) { return; }
        catch (Exception e) { Debug.LogWarning($"UDP Recv Error: {e.Message}"); }
        finally
        {
            if (isConnected && udpClient != null)
            {
                try { udpClient.BeginReceive(OnReceive, null); }
                catch { }
            }
        }
    }

    private void StopConnection()
    {
        isConnected = false;
        StopAllCoroutines();

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