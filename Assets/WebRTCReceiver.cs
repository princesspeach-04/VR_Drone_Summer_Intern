using UnityEngine;
using Unity.WebRTC;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public class WebRTCReceiver : MonoBehaviour
{
    [Header("Server Settings")]
    public string url = "http://172.20.10.2:8080/offer";
    public Renderer screen;

    RTCPeerConnection pc;
    MediaStream receiveStream;
    VideoStreamTrack videoTrack;
    Coroutine texturePoller;

    void Start() => StartCoroutine(StartWebRTC());
    void Update() => WebRTC.Update();

    void OnDestroy()
    {
        if (texturePoller != null) StopCoroutine(texturePoller);
        videoTrack?.Dispose();
        receiveStream?.Dispose();
        pc?.Close();
        pc?.Dispose();
    }

    IEnumerator StartWebRTC()
    {
        Debug.Log("[WebRTC] Starting...");

        var config = new RTCConfiguration
        {
            iceServers = new[] { new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } } }
        };

        pc = new RTCPeerConnection(ref config);
        receiveStream = new MediaStream();

        pc.OnIceConnectionChange = s => Debug.Log($"[WebRTC] ICE: {s}");
        pc.OnConnectionStateChange = s => Debug.Log($"[WebRTC] Conn: {s}");

        receiveStream.OnAddTrack = e =>
        {
            Debug.Log($"[WebRTC] OnAddTrack: {e.Track.Kind}");
            if (e.Track is VideoStreamTrack track)
            {
                videoTrack = track;
                texturePoller = StartCoroutine(PollTexture(track));
            }
        };

        pc.OnTrack = e =>
        {
            Debug.Log($"[WebRTC] OnTrack: {e.Track.Kind}");
            if (e.Track.Kind == TrackKind.Video)
                receiveStream.AddTrack(e.Track);
        };

        // ✅ Add transceiver — codec filtering happens AFTER offer is created
        var transceiver = pc.AddTransceiver(TrackKind.Video, new RTCRtpTransceiverInit
        {
            direction = RTCRtpTransceiverDirection.RecvOnly
        });

        // ✅ Force VP8 only — Unity v3 on Windows DX11 decodes VP8/VP9, NOT H.264
        var caps = RTCRtpReceiver.GetCapabilities(TrackKind.Video);
        Debug.Log("[WebRTC] Available codecs: " + string.Join(", ", caps.codecs.Select(c => c.mimeType)));

        var vp8Codecs = caps.codecs
            .Where(c => c.mimeType.ToLower() == "video/vp8")
            .ToArray();

        if (vp8Codecs.Length > 0)
        {
            Debug.Log($"[WebRTC] Forcing VP8 codec");
            transceiver.SetCodecPreferences(vp8Codecs);
        }
        else
        {
            Debug.LogWarning("[WebRTC] VP8 not found! Available: " +
                string.Join(", ", caps.codecs.Select(c => c.mimeType)));
        }

        // Create offer
        var offerOp = pc.CreateOffer();
        yield return offerOp;
        if (offerOp.IsError) { Debug.LogError($"CreateOffer: {offerOp.Error.message}"); yield break; }

        var offer = offerOp.Desc;
        var setLocalOp = pc.SetLocalDescription(ref offer);
        yield return setLocalOp;
        if (setLocalOp.IsError) { Debug.LogError($"SetLocal: {setLocalOp.Error.message}"); yield break; }

        // Wait for ICE
        float elapsed = 0f;
        while (pc.GatheringState != RTCIceGatheringState.Complete && elapsed < 5f)
        { elapsed += Time.deltaTime; yield return null; }
        Debug.Log($"[WebRTC] ICE gathered in {elapsed:F2}s");

        // Send offer
        var json = JsonUtility.ToJson(new SDP { sdp = pc.LocalDescription.sdp, type = "offer" });
        var req = new UnityWebRequest(url, "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = 15;
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        { Debug.LogError($"[WebRTC] HTTP: {req.error}"); yield break; }

        Debug.Log("[WebRTC] Got answer");
        var answer = JsonUtility.FromJson<SDP>(req.downloadHandler.text);
        var desc = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = answer.sdp };
        var setRemoteOp = pc.SetRemoteDescription(ref desc);
        yield return setRemoteOp;
        if (setRemoteOp.IsError) { Debug.LogError($"SetRemote: {setRemoteOp.Error.message}"); yield break; }

        Debug.Log("[WebRTC] Handshake complete — waiting for video...");

        // Watchdog
        while (true)
        {
            yield return new WaitForSeconds(3f);
            var state = pc.ConnectionState;
            if (state == RTCPeerConnectionState.Failed || state == RTCPeerConnectionState.Disconnected)
            {
                Debug.LogWarning("[WebRTC] Lost — restarting...");
                yield return new WaitForSeconds(2f);
                pc?.Close(); pc?.Dispose();
                receiveStream?.Dispose();
                StartCoroutine(StartWebRTC());
                yield break;
            }
        }
    }

    IEnumerator PollTexture(VideoStreamTrack track)
    {
        Debug.Log("[WebRTC] Texture poller running...");
        bool assigned = false;
        float waited = 0f;
        RenderTexture rt = null;

        while (track != null)
        {
            var tex = track.Texture;
            if (tex != null && screen != null)
            {
                // Create RenderTexture once
                if (rt == null)
                {
                    rt = new RenderTexture(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
                    rt.Create();
                    screen.material.SetTexture("_BaseMap", rt);
                    Debug.Log($"[WebRTC] ✅ RenderTexture created {tex.width}x{tex.height} | shader={screen.material.shader.name}");
                    assigned = true;
                }

                // Blit native texture into RenderTexture every frame
                Graphics.Blit(tex, rt);
            }
            else if (!assigned)
            {
                waited += Time.deltaTime;
                if (waited > 10f)
                {
                    Debug.LogError("[WebRTC] ❌ track.Texture still null after 10s");
                    if (rt != null) rt.Release();
                    yield break;
                }
            }
            yield return null;
        }

        if (rt != null) rt.Release();
    }

    [System.Serializable]
    class SDP { public string sdp; public string type; }
}