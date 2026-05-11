using UnityEngine;
using Unity.WebRTC;
using System.Collections;

public class WebRTCReceiver : MonoBehaviour
{
    public Renderer screen;

    private RTCPeerConnection pc;

    IEnumerator Start()
    {
        pc = new RTCPeerConnection();

        pc.OnTrack = e =>
        {
            Debug.Log("Track received!");

            if (e.Track is VideoStreamTrack track)
            {
                track.OnVideoReceived += tex =>
                {
                    Debug.Log("Video frame received");
                    screen.material.mainTexture = tex;
                };
            }
        };

        yield return null;
    }

    void OnDestroy()
    {
        pc.Close();
    }
}