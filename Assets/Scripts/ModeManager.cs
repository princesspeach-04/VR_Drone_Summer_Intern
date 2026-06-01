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
    public MJPEGStream mjpegStream;

    [Header("Controller Button")]
    public OVRInput.Button toggleButton = OVRInput.Button.One;

    [Header("UI")]
    public TextMeshProUGUI uiButtonLabel;

    public bool IsPassthrough { get; private set; } = false;

    void Start()
    {
        // Auto-find MJPEGStream if not assigned
        if (mjpegStream == null && videoScreenQuad != null)
            mjpegStream =
                videoScreenQuad.GetComponent<MJPEGStream>();

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
        AndroidLog("ModeManager",
            "Apply mode = " +
            (IsPassthrough ? "PASSTHROUGH" : "CAMERA FEED"));

        // ── Video quad ───────────────────────────────────────
        if (videoScreenQuad != null)
        {
            videoScreenQuad.SetActive(!IsPassthrough);

            if (!IsPassthrough && mjpegStream != null)
            {
                // Re-enable quad first, then restart stream
                StartCoroutine(RestartStreamDelayed());
            }
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
    }

    IEnumerator RestartStreamDelayed()
    {
        yield return new WaitForSeconds(0.3f);
        AndroidLog("ModeManager",
            "Restarting MJPEGStream...");
        mjpegStream.RestartStream();
    }

    void UpdateUILabel()
    {
        if (uiButtonLabel == null) return;
        uiButtonLabel.text = IsPassthrough
            ? "Switch to Camera Feed"
            : "Switch to Passthrough";
    }

    // ── Android logcat helper ────────────────────────────────
    public static void AndroidLog(string tag, string msg)
    {
        Debug.Log($"[{tag}] {msg}");

#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var logClass =
                new AndroidJavaClass(
                    "android.util.Log");
            logClass.CallStatic<int>(
                "d", tag, msg);
        }
        catch { }
#endif
    }
}