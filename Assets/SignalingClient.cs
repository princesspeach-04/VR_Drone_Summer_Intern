using UnityEngine;
using Unity.WebRTC;
using System;
using System.Text;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

public class SignalingClient : MonoBehaviour
{
    private ClientWebSocket ws;
    private RTCPeerConnection pc;

    public Renderer screen;

    async void Start()
    {
        // ✅ REQUIRED
        StartCoroutine(WebRTC.Update());

        pc = new RTCPeerConnection();

        // 🔥 CRITICAL FIX — FORCE VIDEO RECEIVER
        var config = new RTCRtpTransceiverInit
        {
            direction = RTCRtpTransceiverDirection.RecvOnly
        };
        pc.AddTransceiver(TrackKind.Video, config);

        // ✅ RECEIVE VIDEO TRACK
        pc.OnTrack = e =>
        {
            Debug.Log("Track received!");

            if (e.Track is VideoStreamTrack track)
            {
                track.OnVideoReceived += tex =>
                {
                    Debug.Log("🔥 FRAME RECEIVED 🔥");
                    screen.material.mainTexture = tex;
                };
            }
        };

        // ✅ SEND ICE
        pc.OnIceCandidate = candidate =>
        {
            if (candidate == null) return;

            string json = "{\"ice\":{\"candidate\":\""
                + candidate.Candidate
                + "\",\"sdpMLineIndex\":"
                + candidate.SdpMLineIndex
                + "}}";

            _ = Send(json);
        };

        ws = new ClientWebSocket();

        Uri serverUri = new Uri("ws://192.168.137.231:8443");

        await ws.ConnectAsync(serverUri, CancellationToken.None);
        Debug.Log("Connected to signaling server");

        await Send("HELLO 5678");

        _ = ReceiveLoop();
    }

    async Task ReceiveLoop()
    {
        byte[] buffer = new byte[8192];

        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            string message = Encoding.UTF8.GetString(buffer, 0, result.Count);

            Debug.Log("Received: " + message);

            if (!message.StartsWith("{")) continue;

            // ICE
            if (message.Contains("\"ice\""))
            {
                var iceMsg = JsonUtility.FromJson<IceMessage>(message);

                RTCIceCandidate candidate = new RTCIceCandidate(new RTCIceCandidateInit
                {
                    candidate = iceMsg.ice.candidate,
                    sdpMLineIndex = iceMsg.ice.sdpMLineIndex
                });

                pc.AddIceCandidate(candidate);
                continue;
            }

            // SDP OFFER
            var msg = JsonUtility.FromJson<SDPMessage>(message);

            if (msg.sdp != null)
            {
                var desc = new RTCSessionDescription
                {
                    type = RTCSdpType.Offer,
                    sdp = msg.sdp.sdp
                };

                var op = pc.SetRemoteDescription(ref desc);
                while (!op.IsDone) await Task.Yield();

                StartCoroutine(CreateAnswer());
            }
        }
    }

    System.Collections.IEnumerator CreateAnswer()
    {
        var op = pc.CreateAnswer();
        yield return op;

        var desc = op.Desc;

        var opLocal = pc.SetLocalDescription(ref desc);
        yield return opLocal;

        Debug.Log("Sending Answer");

        string json = "{\"sdp\":{\"type\":\"answer\",\"sdp\":\""
            + desc.sdp.Replace("\r", "").Replace("\n", "\\n")
            + "\"}}";

        _ = Send(json);
    }

    async Task Send(string msg)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(msg);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    void OnDestroy()
    {
        pc.Close();
        pc.Dispose();
    }

    // ===== JSON =====

    [Serializable]
    public class SDPMessage { public SDP sdp; }

    [Serializable]
    public class SDP { public string sdp; }

    [Serializable]
    public class IceMessage { public Ice ice; }

    [Serializable]
    public class Ice
    {
        public string candidate;
        public int sdpMLineIndex;
    }
}