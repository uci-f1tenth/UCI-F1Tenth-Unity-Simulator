using UnityEngine;
using TMPro;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

public class UDPTunnel : MonoBehaviour
{
    [Header("UI References")]
    public TMP_InputField ipInput;
    public TMP_InputField sendPortInput;
    public TMP_InputField recvPortInput;
    public TextMeshProUGUI buttonLabel;

    [Header("Status")]
    public bool isConnected = false;

    private UdpClient sendClient;
    private UdpClient receiveClient;
    private Thread receiveThread;
    private byte[] sendBuffer = new byte[1448];

    // --- Connection Logic ---

    public void ToggleConnection()
    {
        if (!isConnected)
            StartConnection();
        else
            StopConnection();
    }

    private void StartConnection()
    {
        try
        {
            // 1. Parse UI Inputs
            string ip = ipInput.text;
            int sPort = int.Parse(sendPortInput.text);
            int rPort = int.Parse(recvPortInput.text);

            // 2. Setup Clients
            sendClient = new UdpClient();
            sendClient.Connect(ip, sPort);

            receiveClient = new UdpClient(rPort);
            receiveClient.Client.ReceiveTimeout = 1000;

            // 3. Start Background Thread
            receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
            receiveThread.Start();

            // 4. Update UI State
            isConnected = true;
            if (buttonLabel != null) buttonLabel.text = "Disconnect";
            Debug.Log($"UDP Tunnel Opened: {ip} | S:{sPort} R:{rPort}");
        }
        catch (Exception e)
        {
            Debug.LogError($"Connection Failed: {e.Message}");
        }
    }

    private void StopConnection()
    {
        isConnected = false;

        receiveThread?.Abort();
        receiveClient?.Close();
        sendClient?.Close();

        if (buttonLabel != null) buttonLabel.text = "Connect";
        Debug.Log("UDP Tunnel Closed.");
    }

    // --- Data Loop ---

    void FixedUpdate()
    {
        if (!isConnected || DataBridge.Instance == null) return;

        // PING: Pack current time into buffer
        float now = DataBridge.Instance.timeSinceStart;
        Buffer.BlockCopy(BitConverter.GetBytes(now), 0, sendBuffer, 4, 4);

        // (Optional: Fill Lidar data at index 8+)

        try
        {
            sendClient.Send(sendBuffer, sendBuffer.Length);
        }
        catch { /* Handle transient send errors */ }
    }

    private void ReceiveLoop()
    {
        IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
        while (isConnected)
        {
            try
            {
                byte[] data = receiveClient.Receive(ref remoteEP);
                if (data.Length == 12 && DataBridge.Instance != null)
                {
                    // PONG: Update DataBridge with Python's response
                    DataBridge.Instance.steering = BitConverter.ToSingle(data, 4);
                    DataBridge.Instance.throttle = BitConverter.ToSingle(data, 8);
                }
            }
            catch (SocketException) { /* Timeout or Close */ }
            catch (Exception e) { Debug.LogWarning(e); }
        }
    }

    void OnApplicationQuit() => StopConnection();
}