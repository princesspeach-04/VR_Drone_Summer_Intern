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
    public string jetsonIP = "192.168.137.91";
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

    IEnumerator Start()
    {
        client = new UdpClient();

        Debug.Log("[HEAD] Waiting for XR initialization...");

        // Wait for OpenXR tracking to stabilize
        yield return new WaitForSeconds(2f);

        calibrated = false;

        Debug.Log("[HEAD] Ready");
    }

    void Update()
    {
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

        // Calculate yaw + pitch
        float yaw =
            Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;

        float pitch =
            Mathf.Asin(forward.y) * Mathf.Rad2Deg;

        // Initial calibration
        if (!calibrated)
        {
            baseYaw = yaw;
            basePitch = pitch;

            calibrated = true;

            Debug.Log("[HEAD] CALIBRATED");
        }

        // Relative movement
        float relativeYaw =
            Mathf.DeltaAngle(baseYaw, yaw);

        float relativePitch =
            pitch - basePitch;

        // Smoothing
        smoothYaw = Mathf.Lerp(
            smoothYaw,
            relativeYaw,
            smoothing
        );

        smoothPitch = Mathf.Lerp(
            smoothPitch,
            relativePitch,
            smoothing
        );

        // Create message
        string msg =
            smoothYaw.ToString("F2") + "," +
            smoothPitch.ToString("F2");

        // Send UDP packet
        byte[] data =
            Encoding.UTF8.GetBytes(msg);

        client.Send(
            data,
            data.Length,
            jetsonIP,
            port
        );

        // Debug logs
        Debug.Log(
            "[HEAD TRACKING] " +
            "Yaw: " + smoothYaw.ToString("F2") +
            " Pitch: " + smoothPitch.ToString("F2")
        );

        // Visual debug ray
        Debug.DrawRay(
            headset.position,
            headset.forward * 3f,
            Color.red
        );
    }

    void OnApplicationQuit()
    {
        if (client != null)
        {
            client.Close();
        }
    }
}