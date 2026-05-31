using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;

public class ModeManager : MonoBehaviour
{
    [Header("Scene References")]
    public GameObject videoScreenQuad;
    public OVRPassthroughLayer passthroughLayer;
    public Camera mainCamera;
    public DetectionOverlay detectionOverlay;

    [Header("Stream")]
    public string jetsonHostname = "100.116.179.56";
    public int jetsonPort = 8080;

    [Header("Controller Button")]
    public OVRInput.Button toggleButton = OVRInput.Button.One;

    [Header("UI")]
    public TextMeshProUGUI uiButtonLabel;

    public bool IsPassthrough { get; private set; } = false;

    // ── Stream internals ──────────────────────────────────────
    private Renderer _quadRenderer;
    private Texture2D _texture;
    private Coroutine _streamCoroutine;
    private bool _streamRunning = false;
    private string _streamUrl;

    // ─────────────────────────────────────────────────────────
    void Start()
    {
        _streamUrl = $"http://{jetsonHostname}:{jetsonPort}/stream";

        if (videoScreenQuad != null)
        {
            _quadRenderer = videoScreenQuad
                                .GetComponent<Renderer>();
            _texture = new Texture2D(
                                2, 2,
                                TextureFormat.RGB24,
                                false);
        }

        if (passthroughLayer != null)
            passthroughLayer.enabled = false;

        UpdateUILabel();
        Apply();
    }

    void Update()
    {
        if (OVRInput.GetDown(toggleButton))
            Toggle();
    }

    public void Toggle()
    {
        IsPassthrough = !IsPassthrough;
        Apply();
    }

    void Apply()
    {
        // ── Video quad ───────────────────────────────────────
        if (videoScreenQuad != null)
        {
            videoScreenQuad.SetActive(!IsPassthrough);

            if (!IsPassthrough)
                RestartStream();
            else
                StopStream();
        }

        // ── Passthrough layer ────────────────────────────────
        if (passthroughLayer != null)
            passthroughLayer.enabled = IsPassthrough;

        // ── Camera background ────────────────────────────────
        if (mainCamera != null)
        {
            if (IsPassthrough)
            {
                mainCamera.clearFlags =
                    CameraClearFlags.SolidColor;
                mainCamera.backgroundColor =
                    new Color(0, 0, 0, 0);
            }
            else
            {
                mainCamera.clearFlags =
                    CameraClearFlags.SolidColor;
                mainCamera.backgroundColor = Color.black;
            }
        }

        // ── Detection overlay ────────────────────────────────
        if (detectionOverlay != null)
            detectionOverlay.SetPassthroughMode(
                IsPassthrough);

        UpdateUILabel();

        Debug.Log("[ModeManager] Mode = " +
            (IsPassthrough
                ? "PASSTHROUGH"
                : "CAMERA FEED"));
    }

    // ── Stream control ────────────────────────────────────────
    void RestartStream()
    {
        StopStream();
        StartCoroutine(DelayedStreamStart(0.2f));
    }

    IEnumerator DelayedStreamStart(float delay)
    {
        yield return new WaitForSeconds(delay);
        StartStream();
    }

    void StartStream()
    {
        if (_streamRunning) return;
        _streamRunning = true;
        _streamCoroutine = StartCoroutine(StreamLoop());
        Debug.Log($"[Stream] Started → {_streamUrl}");
    }

    void StopStream()
    {
        _streamRunning = false;
        if (_streamCoroutine != null)
        {
            StopCoroutine(_streamCoroutine);
            _streamCoroutine = null;
        }
        Debug.Log("[Stream] Stopped");
    }

    IEnumerator StreamLoop()
    {
        while (_streamRunning)
        {
            using var req = UnityWebRequest.Get(_streamUrl);
            req.timeout = 5;
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SendWebRequest();

            var buffer = new List<byte>();
            bool inJpeg = false;

            while (!req.isDone && _streamRunning)
            {
                if (req.result ==
                        UnityWebRequest.Result
                            .ConnectionError ||
                    req.result ==
                        UnityWebRequest.Result
                            .ProtocolError)
                {
                    Debug.LogWarning(
                        "[Stream] Error: " + req.error);
                    break;
                }

                byte[] data = req.downloadHandler.data;
                if (data == null || data.Length == 0)
                {
                    yield return null;
                    continue;
                }

                // Parse JPEG frames from MJPEG stream
                // by looking for FF D8 (start)
                // and FF D9 (end) markers
                for (int i = 0;
                     i < data.Length - 1; i++)
                {
                    if (!inJpeg &&
                        data[i] == 0xFF &&
                        data[i + 1] == 0xD8)
                    {
                        buffer.Clear();
                        inJpeg = true;
                    }

                    if (inJpeg)
                    {
                        buffer.Add(data[i]);

                        if (data[i] == 0xFF &&
                            data[i + 1] == 0xD9)
                        {
                            buffer.Add(data[i + 1]);
                            ApplyFrame(buffer.ToArray());
                            buffer.Clear();
                            inJpeg = false;
                            i++;
                        }
                    }
                }

                yield return null;
            }

            if (_streamRunning)
            {
                Debug.LogWarning(
                    "[Stream] Dropped — retrying in 1s");
                yield return new WaitForSeconds(1f);
            }
        }
    }

    void ApplyFrame(byte[] jpegBytes)
    {
        if (_quadRenderer == null ||
            jpegBytes == null ||
            jpegBytes.Length == 0) return;

        if (_texture.LoadImage(jpegBytes))
            _quadRenderer.material.mainTexture = _texture;
    }

    // ── UI ────────────────────────────────────────────────────
    void UpdateUILabel()
    {
        if (uiButtonLabel == null) return;
        uiButtonLabel.text = IsPassthrough
            ? "Switch to Camera Feed"
            : "Switch to Passthrough";
    }

    // ── Cleanup ───────────────────────────────────────────────
    void OnDestroy()
    {
        StopStream();
        if (_texture != null)
            Destroy(_texture);
    }
}