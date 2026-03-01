using UnityEngine;
using TMPro;
using System;
using System.Net;
using System.Net.Sockets;
using System.Collections;
using System.Threading;

public class UDPTunnel : MonoBehaviour
{
    public static UDPTunnel Instance { get; private set; }

    [Header("Network Settings")]
    public TMP_InputField ipInput;
    public TMP_InputField sendPortInput, recvPortInput;
    public TextMeshProUGUI buttonLabel;

    private UdpClient udpClient;
    private IPEndPoint remoteEndPoint;
    public bool isConnected = false;

    private const int HEADER_SIZE = 84; // 84 bytes for full telemetry
    private const float MAX_LIDAR_DIST = 20f;
    private byte[] sendPayload;
    private int lidarCount = 0;

    // Thread-safe Reward
    private int _rewardScaled = 0;
    private const float REWARD_SCALE = 1000f;

    private void Awake()
    {
        if (Instance != null && Instance != this) Destroy(this);
        else Instance = this;
    }

    public void AddReward(float amt) => Interlocked.Add(ref _rewardScaled, (int)(amt * REWARD_SCALE));

    // Gets the accumulated reward and resets it to 0 immediately
    private float GetAndResetReward()
    {
        int val = Interlocked.Exchange(ref _rewardScaled, 0);
        return (float)val / REWARD_SCALE;
    }

    public void ToggleConnection()
    {
        // FIX: Previously called Start() which is a Unity magic method.
        if (!isConnected) StartConnection();
        else StopConnection();
    }

    private void StartConnection()
    {
        try
        {
            var lidar = FindFirstObjectByType<LidarSensor2D>();
            lidarCount = lidar != null ? lidar.numberOfPoints : 0;
            sendPayload = new byte[HEADER_SIZE + (lidarCount * 2)];

            // --- Socket Reuse Logic ---
            udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            // Bind to Receive Port
            int rPort = int.Parse(recvPortInput.text);
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, rPort));

            // Setup Remote Target (Python Script)
            string ip = ipInput.text;
            int sPort = int.Parse(sendPortInput.text);
            remoteEndPoint = new IPEndPoint(IPAddress.Parse(ip), sPort);

            udpClient.BeginReceive(OnReceive, null);

            isConnected = true;
            if (buttonLabel != null) buttonLabel.text = "Disconnect";

            Debug.Log($"UDP Tunnel Active. Sending to {ip}:{sPort}, Listening on {rPort}");
            StartCoroutine(EndOfFixedUpdateLoop());
        }
        catch (Exception e)
        {
            Debug.LogError($"Connection Failed: {e.Message}");
            StopConnection();
        }
    }

    private IEnumerator EndOfFixedUpdateLoop()
    {
        while (isConnected)
        {
            // Wait for physics to finish so we send the final state of the frame
            yield return new WaitForFixedUpdate();
            if (DataBridge.Instance != null) SendTelemetry();
        }
    }

    private void SendTelemetry()
    {
        int o = 0;
        var db = DataBridge.Instance;

        // 1. System & Basic Motion
        Pack(db.timeSinceStart, ref o);      // 0
        Pack(Time.fixedDeltaTime, ref o);    // 4
        Pack(db.speedMS, ref o);             // 8
        Pack(db.steering, ref o);            // 12 (Last Command)
        Pack(db.steeringAngleRad, ref o);    // 16 (Actual Physics Angle)
        Pack(db.throttle, ref o);            // 20
        Pack(db.wheelTicks, ref o);          // 24
        Pack(db.currentWheelRPM, ref o);     // 28

        // 2. IMU Conversion (Unity LHS -> ROS RHS)
        // Unity: Z=Fwd, X=Right, Y=Up
        // ROS:   X=Fwd, Y=Left,  Z=Up
        Vector3 rEuler = new Vector3(db.imuAngles.z, -db.imuAngles.x, db.imuAngles.y);
        Vector3 rLin = new Vector3(db.linAccel.z, -db.linAccel.x, db.linAccel.y);
        Vector3 rAng = new Vector3(db.angAccel.z, -db.angAccel.x, db.angAccel.y);

        Pack(rEuler.x, ref o); Pack(rEuler.y, ref o); Pack(rEuler.z, ref o); // 32, 36, 40
        Pack(rLin.x, ref o); Pack(rLin.y, ref o); Pack(rLin.z, ref o);   // 44, 48, 52
        Pack(rAng.x, ref o); Pack(rAng.y, ref o); Pack(rAng.z, ref o);   // 56, 60, 64

        // 3. Scoring & Status
        Pack(GetAndResetReward(), ref o);    // 68
        Pack(db.lapCount, ref o);            // 72

        // Check if 'isCrashed' exists in DataBridge, otherwise send 0
        // (Assuming you added 'public bool isCrashed;' to DataBridge)
        Pack(db.isCrashed ? 1 : 0, ref o);   // 76
        db.isCrashed = false;                // Resetting here ensures Python saw it

        Pack(lidarCount, ref o);             // 80

        // 4. Lidar (Quantized)
        o = HEADER_SIZE;
        // Safety check: ensure we don't read past the array if it changed size
        int pointsToPack = Mathf.Min(lidarCount, db.lidarRanges != null ? db.lidarRanges.Length : 0);

        for (int i = 0; i < pointsToPack; i++)
        {
            float dist = Mathf.Clamp(db.lidarRanges[i], 0f, MAX_LIDAR_DIST);
            ushort q = (ushort)((dist / MAX_LIDAR_DIST) * 65535);
            sendPayload[o + (i * 2)] = (byte)(q & 0xFF);
            sendPayload[o + (i * 2) + 1] = (byte)(q >> 8);
        }

        try { udpClient.Send(sendPayload, sendPayload.Length, remoteEndPoint); }
        catch { /* Transient network error, ignore */ }
    }

    private void Pack(float v, ref int o) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, sendPayload, o, 4); o += 4; }
    private void Pack(int v, ref int o) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, sendPayload, o, 4); o += 4; }

    private void OnReceive(IAsyncResult ar)
    {
        // Safety check to prevent ObjectDisposedException if disconnected during receive
        if (!isConnected || udpClient == null) return;

        try
        {
            IPEndPoint ip = new IPEndPoint(IPAddress.Any, 0);
            byte[] b = udpClient.EndReceive(ar, ref ip);
            if (b.Length >= 8)
            {
                DataBridge.Instance.steering = BitConverter.ToSingle(b, 0);
                DataBridge.Instance.throttle = BitConverter.ToSingle(b, 4);
            }
        }
        catch (ObjectDisposedException) { return; } // Socket closed gracefully
        catch (Exception e) { Debug.LogWarning($"UDP Recv Error: {e.Message}"); }
        finally
        {
            // Only restart listening if we are still theoretically connected
            if (isConnected && udpClient != null)
            {
                try { udpClient.BeginReceive(OnReceive, null); }
                catch { } // Handle race condition where socket closes in this nanosecond
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