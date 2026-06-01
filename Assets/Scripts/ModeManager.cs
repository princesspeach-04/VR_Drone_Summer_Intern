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

    [Header("Controller Button")]
    public OVRInput.Button toggleButton = OVRInput.Button.One;

    [Header("UI")]
    public TextMeshProUGUI uiButtonLabel;

    // Prevent rapid toggling
    private const float TOGGLE_COOLDOWN = 1.0f;
    private float _lastToggleTime = -99f;

    public bool IsPassthrough { get; private set; } = true;  // start in passthrough

    private Renderer _quadRenderer;
    private Material _visibleMat;
    private Material _hiddenMat;   // fully transparent — keeps stream alive
    private MJPEGStream _mjpeg;

    void Start()
    {
        ALog("ModeManager", "Start()");

        if (videoScreenQuad != null)
        {
            _quadRenderer = videoScreenQuad.GetComponent<Renderer>();
            _mjpeg = videoScreenQuad.GetComponent<MJPEGStream>();

            if (_quadRenderer != null)
            {
                _visibleMat = _quadRenderer.material;

                // Transparent clone — hides quad WITHOUT disabling it so
                // the MJPEG stream keeps decoding in the background.
                _hiddenMat = new Material(_visibleMat);
                _hiddenMat.color = new Color(0, 0, 0, 0);
                _hiddenMat.SetFloat("_Surface", 1);
                _hiddenMat.SetFloat("_Blend", 0);
                _hiddenMat.renderQueue = 3000;
            }

            ALog("ModeManager", $"MJPEG found: {(_mjpeg != null ? "YES" : "NO")}");
        }

        if (passthroughLayer != null)
            passthroughLayer.enabled = false;

        // ── Start in PASSTHROUGH mode ──────────────────────────────────────
        // DetectionOverlay.Start() also defaults to passthrough=true,
        // so both scripts agree from the very first frame.
        IsPassthrough = true;
        ApplyMode();
        UpdateUILabel();
    }

    void Update()
    {
        if (OVRInput.GetDown(toggleButton))
        {
            float now = Time.time;
            if (now - _lastToggleTime < TOGGLE_COOLDOWN)
            {
                ALog("ModeManager", "Toggle ignored — cooldown");
                return;
            }
            _lastToggleTime = now;
            Toggle();
        }
    }

    public void Toggle()
    {
        IsPassthrough = !IsPassthrough;
        ALog("ModeManager", $"Toggle → {(IsPassthrough ? "PASSTHROUGH" : "CAMERA FEED")}");
        ApplyMode();
    }

    void ApplyMode()
    {
        if (IsPassthrough)
            EnterPassthrough();
        else
            EnterCameraFeed();

        // Keep DetectionOverlay in sync so it routes to
        // UpdateMarkers (passthrough) or UpdateLabels (camera feed)
        if (detectionOverlay != null)
            detectionOverlay.SetPassthroughMode(IsPassthrough);

        UpdateUILabel();
    }

    void EnterPassthrough()
    {
        ALog("ModeManager", "EnterPassthrough");

        // Hide quad visually but keep it alive for the stream
        if (_quadRenderer != null && _hiddenMat != null)
            _quadRenderer.material = _hiddenMat;

        if (passthroughLayer != null)
            passthroughLayer.enabled = true;

        if (mainCamera != null)
        {
            mainCamera.clearFlags = CameraClearFlags.SolidColor;
            mainCamera.backgroundColor = new Color(0, 0, 0, 0);
        }
    }

    void EnterCameraFeed()
    {
        ALog("ModeManager", "EnterCameraFeed");

        // Restore visible material
        if (_quadRenderer != null && _visibleMat != null)
            _quadRenderer.material = _visibleMat;

        if (passthroughLayer != null)
            passthroughLayer.enabled = false;

        if (mainCamera != null)
        {
            mainCamera.clearFlags = CameraClearFlags.SolidColor;
            mainCamera.backgroundColor = Color.black;
        }

        // Stream was never paused, but reassign texture in case the
        // material swap lost the reference.
        if (_mjpeg != null)
        {
            ALog("ModeManager", "Reassigning stream texture");
            _mjpeg.ReassignTexture();
        }
    }

    void UpdateUILabel()
    {
        if (uiButtonLabel == null) return;
        uiButtonLabel.text = IsPassthrough
            ? "Switch to Camera Feed"
            : "Switch to Passthrough";
    }

    static void ALog(string tag, string msg)
    {
        Debug.Log($"[{tag}] {msg}");
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var c = new AndroidJavaClass("android.util.Log");
            c.CallStatic<int>("d", tag, msg);
        }
        catch { }
#endif
    }
}