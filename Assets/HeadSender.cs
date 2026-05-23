using UnityEngine;
using Unity.XR.CoreUtils;
using System.Collections;
using System.Net.Sockets;
using System.Text;

public class HeadSender : MonoBehaviour
{
    [Header("XR")]
    public XROrigin xrOrigin;

    [Header("UDP")]
    public string jetsonIP = "100.94.87.80";
    public int port = 5005;

    [Header("Smoothing")]
    [Range(0.01f, 1f)]
    public float smoothing = 0.1f;

    private UdpClient client;
    private float smoothYaw = 0f;
    private float smoothPitch = 0f;
    private float baseYaw = 0f;
    private float basePitch = 0f;
    private bool calibrated = false;

    void Start()
    {
        // Initialize immediately — no coroutine delay
        try
        {
            client = new UdpClient();
            client.Connect(jetsonIP, port); // pre-connect so Send is simpler
            Debug.Log("[HEAD] UDP client connected to " + jetsonIP + ":" + port);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[HEAD] Failed to create UDP client: " + e.Message);
        }
    }

    void Update()
    {
        // Guard against null client
        if (client == null)
        {
            Debug.LogError("[HEAD] UDP client is null!");
            return;
        }

        if (xrOrigin == null)
        {
            Debug.LogError("[HEAD] XR Origin missing!");
            return;
        }

        if (xrOrigin.Camera == null)
        {
            Debug.LogError("[HEAD] XR Camera missing!");
            return;
        }

        Transform headset = xrOrigin.Camera.transform;
        Vector3 forward = headset.forward;

        float yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        float pitch = Mathf.Asin(forward.y) * Mathf.Rad2Deg;

        if (!calibrated)
        {
            baseYaw = yaw;
            basePitch = pitch;
            calibrated = true;
            Debug.Log("[HEAD] CALIBRATED");
        }

        float relativeYaw = Mathf.DeltaAngle(baseYaw, yaw);
        float relativePitch = pitch - basePitch;

        smoothYaw = Mathf.Lerp(smoothYaw, relativeYaw, smoothing);
        smoothPitch = Mathf.Lerp(smoothPitch, relativePitch, smoothing);

        string msg = smoothYaw.ToString("F2") + "," + smoothPitch.ToString("F2");
        byte[] data = Encoding.UTF8.GetBytes(msg);

        try
        {
            client.Send(data, data.Length); // uses pre-connected address
            Debug.Log("[HEAD] Sent: " + msg);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[HEAD] Send failed: " + e.Message);
        }
    }

    void OnApplicationQuit()
    {
        if (client != null)
            client.Close();
    }
}