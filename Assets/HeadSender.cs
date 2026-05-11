using UnityEngine;
using System.Net.Sockets;
using System.Text;

public class HeadSender : MonoBehaviour
{
    public Transform headset;
    UdpClient client;

    public string jetsonIP = "192.168.137.231";
    public int port = 5005;

    void Start()
    {
        client = new UdpClient();
    }

    void Update()
    {
        Vector3 rot = headset.eulerAngles;

        string msg = rot.y + "," + rot.x; // yaw,pitch

        byte[] data = Encoding.UTF8.GetBytes(msg);
        client.Send(data, data.Length, jetsonIP, port);
    }
}