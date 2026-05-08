using UnityEngine;
using System.Net.Sockets;
using System.Text;

public class HeadTracker : MonoBehaviour
{
    [Header("Clamp Limits")]
    public float maxYaw = 90f;
    public float maxPitch = 45f;

    [Header("Smoothing")]
    public float smoothSpeed = 5f;

    [Header("Network")]
    public string jetsonIP = "192.168.137.160";
    public int port = 6000;

    private UdpClient udp;

    public float currentYaw;
    public float currentPitch;

    private float smoothYaw;
    private float smoothPitch;

    void Start()
    {
        Debug.Log("HeadTracker STARTED");

        try
        {
            udp = new UdpClient();
        }
        catch (System.Exception e)
        {
            Debug.LogError("UDP init failed: " + e.Message);
        }
    }

    void Update()
    {
        // Safety check
        if (udp == null)
        {
            Debug.LogError("UDP is NULL!");
            return;
        }

        // Get headset rotation
        Vector3 raw = transform.localEulerAngles;

        float yaw = NormalizeAngle(raw.y);
        float pitch = NormalizeAngle(raw.x);

        // Clamp
        yaw = Mathf.Clamp(yaw, -maxYaw, maxYaw);
        pitch = Mathf.Clamp(pitch, -maxPitch, maxPitch);

        // Smooth
        smoothYaw = Mathf.Lerp(smoothYaw, yaw, Time.deltaTime * smoothSpeed);
        smoothPitch = Mathf.Lerp(smoothPitch, pitch, Time.deltaTime * smoothSpeed);

        currentYaw = smoothYaw;
        currentPitch = smoothPitch;

        // Send data
        string message = currentYaw.ToString("F2") + "," + currentPitch.ToString("F2");
        byte[] data = Encoding.UTF8.GetBytes(message);

        try
        {
            udp.Send(data, data.Length, jetsonIP, port);
        }
        catch (System.Exception e)
        {
            Debug.LogError("UDP send failed: " + e.Message);
        }
    }

    float NormalizeAngle(float angle)
    {
        if (angle > 180f)
            angle -= 360f;
        return angle;
    }

    void OnApplicationQuit()
    {
        if (udp != null)
        {
            udp.Close();
        }
    }
}