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

    public bool IsPassthrough { get; private set; } = false;

    // We keep the quad ALWAYS active —
    // just swap its material between
    // visible and invisible
    private Renderer _quadRenderer;
    private Material _visibleMat;
    private Material _hiddenMat;      // fully transparent
    private MJPEGStream _mjpeg;

    void Start()
    {
        ALog("ModeManager", "Start()");

        if (videoScreenQuad != null)
        {
            _quadRenderer = videoScreenQuad
                                .GetComponent<Renderer>();
            _mjpeg = videoScreenQuad
                                .GetComponent<MJPEGStream>();

            if (_quadRenderer != null)
            {
                // Cache the original visible material
                _visibleMat = _quadRenderer.material;

                // Create a fully transparent material
                // for hiding the quad without disabling it
                _hiddenMat = new Material(_visibleMat);
                _hiddenMat.color = new Color(0, 0, 0, 0);

                // Use URP transparent shader if available
                _hiddenMat.SetFloat("_Surface", 1);
                _hiddenMat.SetFloat("_Blend", 0);
                _hiddenMat.renderQueue = 3000;
            }

            ALog("ModeManager",
                 $"MJPEG found: {(_mjpeg != null ? "YES" : "NO")}");
        }

        if (passthroughLayer != null)
            passthroughLayer.enabled = false;

        // Start in passthrough mode
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
                ALog("ModeManager",
                     "Toggle ignored — cooldown");
                return;
            }
            _lastToggleTime = now;
            Toggle();
        }
    }

    public void Toggle()
    {
        IsPassthrough = !IsPassthrough;
        ALog("ModeManager",
             $"Toggle → {(IsPassthrough ? "PASSTHROUGH" : "CAMERA FEED")}");
        ApplyMode();
    }

    void ApplyMode()
    {
        if (IsPassthrough)
            EnterPassthrough();
        else
            EnterCameraFeed();

        if (detectionOverlay != null)
            detectionOverlay.SetPassthroughMode(
                IsPassthrough);

        UpdateUILabel();
    }

    void EnterPassthrough()
    {
        ALog("ModeManager", "EnterPassthrough");

        // Hide quad by making it transparent
        // — DO NOT disable it so stream keeps running
        if (_quadRenderer != null && _hiddenMat != null)
            _quadRenderer.material = _hiddenMat;

        if (passthroughLayer != null)
            passthroughLayer.enabled = true;

        if (mainCamera != null)
        {
            mainCamera.clearFlags =
                CameraClearFlags.SolidColor;
            mainCamera.backgroundColor =
                new Color(0, 0, 0, 0);
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
            mainCamera.clearFlags =
                CameraClearFlags.SolidColor;
            mainCamera.backgroundColor = Color.black;
        }

        // Stream is still running since quad was
        // never disabled — but reassign texture
        // just in case material swap lost it
        if (_mjpeg != null)
        {
            ALog("ModeManager",
                 "Reassigning stream texture");
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
            using var c = new AndroidJavaClass(
                "android.util.Log");
            c.CallStatic<int>("d", tag, msg);
        }
        catch { }
#endif
    }
}