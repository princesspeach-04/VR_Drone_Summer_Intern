using UnityEngine;
using Unity.WebRTC;
using UnityEngine.Networking;
using System.Collections;
using System.Linq;
using System.Text;

public class WebRTCReceiver : MonoBehaviour
{
    [Header("Server")]
    public string url = "http://172.20.10.2:8080/offer";

    [Header("Display")]
    public Renderer videoRenderer;

    private RTCPeerConnection pc;
    private VideoStreamTrack videoTrack;

    private Texture currentTexture;
    private Material runtimeMaterial;

    void Start()
    {
        Application.runInBackground = true;
        Application.targetFrameRate = 120;
        QualitySettings.vSyncCount = 0;

        StartCoroutine(StartWebRTC());
    }

    void Update()
    {
        WebRTC.Update();

        // IMPORTANT:
        // Continuously refresh texture reference
        if (videoTrack != null)
        {
            Texture tex = videoTrack.Texture;

            if (tex != null)
            {
                if (currentTexture != tex)
                {
                    currentTexture = tex;

                    Debug.Log("[WebRTC] Texture updated");

                    if (runtimeMaterial == null)
                    {
                        runtimeMaterial =
                            new Material(
                                Shader.Find("Unlit/Texture"));

                        videoRenderer.material =
                            runtimeMaterial;
                    }

                    runtimeMaterial.mainTexture =
                        currentTexture;

                    runtimeMaterial.mainTextureScale =
                        new Vector2(1, -1);
                }
            }
        }
    }

    void OnDestroy()
    {
        videoTrack?.Dispose();

        if (runtimeMaterial != null)
            Destroy(runtimeMaterial);

        if (pc != null)
        {
            pc.Close();
            pc.Dispose();
        }
    }

    IEnumerator StartWebRTC()
    {
        Debug.Log("[WebRTC] Starting");

        RTCConfiguration config =
            new RTCConfiguration
            {
                iceServers = new[]
                {
                    new RTCIceServer
                    {
                        urls = new[]
                        {
                            "stun:stun.l.google.com:19302"
                        }
                    }
                }
            };

        pc = new RTCPeerConnection(ref config);

        pc.OnIceConnectionChange = state =>
        {
            Debug.Log("[WebRTC] ICE: " + state);
        };

        pc.OnConnectionStateChange = state =>
        {
            Debug.Log("[WebRTC] Conn: " + state);
        };

        pc.OnTrack = e =>
        {
            Debug.Log("[WebRTC] TRACK: " + e.Track.Kind);

            if (e.Track is VideoStreamTrack track)
            {
                videoTrack = track;

                Debug.Log("[WebRTC] Video track received");
            }
        };

        var transceiver =
            pc.AddTransceiver(
                TrackKind.Video,
                new RTCRtpTransceiverInit
                {
                    direction =
                        RTCRtpTransceiverDirection
                        .RecvOnly
                });

        var caps =
            RTCRtpReceiver.GetCapabilities(
                TrackKind.Video);

        var vp8 =
            caps.codecs
            .Where(c =>
                c.mimeType.ToLower() == "video/vp8")
            .ToArray();

        if (vp8.Length > 0)
        {
            Debug.Log("[WebRTC] Using VP8");

            transceiver.SetCodecPreferences(vp8);
        }

        var offerOp = pc.CreateOffer();

        yield return offerOp;

        if (offerOp.IsError)
        {
            Debug.LogError(
                offerOp.Error.message);

            yield break;
        }

        var offer = offerOp.Desc;

        var localOp =
            pc.SetLocalDescription(ref offer);

        yield return localOp;

        if (localOp.IsError)
        {
            Debug.LogError(
                localOp.Error.message);

            yield break;
        }

        while (pc.GatheringState !=
               RTCIceGatheringState.Complete)
        {
            yield return null;
        }

        string json =
            JsonUtility.ToJson(
                new SDP
                {
                    sdp =
                        pc.LocalDescription.sdp,

                    type = "offer"
                });

        UnityWebRequest req =
            new UnityWebRequest(url, "POST");

        req.uploadHandler =
            new UploadHandlerRaw(
                Encoding.UTF8.GetBytes(json));

        req.downloadHandler =
            new DownloadHandlerBuffer();

        req.SetRequestHeader(
            "Content-Type",
            "application/json");

        yield return req.SendWebRequest();

        if (req.result !=
            UnityWebRequest.Result.Success)
        {
            Debug.LogError(req.error);

            yield break;
        }

        SDP answer =
            JsonUtility.FromJson<SDP>(
                req.downloadHandler.text);

        RTCSessionDescription desc =
            new RTCSessionDescription
            {
                type = RTCSdpType.Answer,
                sdp = answer.sdp
            };

        var remoteOp =
            pc.SetRemoteDescription(ref desc);

        yield return remoteOp;

        if (remoteOp.IsError)
        {
            Debug.LogError(
                remoteOp.Error.message);

            yield break;
        }

        Debug.Log("[WebRTC] Connected");
    }

    [System.Serializable]
    public class SDP
    {
        public string sdp;
        public string type;
    }
}