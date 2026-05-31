using UnityEngine;
using TMPro;

public class ModeManager : MonoBehaviour
{
    [Header("Scene References")]
    public GameObject videoScreenQuad;
    public OVRPassthroughLayer passthroughLayer;
    public Camera mainCamera;
    public DetectionOverlay detectionOverlay;

    [Header("Controller Button - default: A button")]
    public OVRInput.Button toggleButton =
        OVRInput.Button.One;

    [Header("UI Button Label (optional)")]
    public TextMeshProUGUI uiButtonLabel;

    public bool IsPassthrough { get; private set; }
        = false;

    void Start()
    {
        // Make sure passthrough layer starts disabled
        if (passthroughLayer != null)
            passthroughLayer.enabled = false;

        UpdateUILabel();
    }

    void Update()
    {
        // Controller button toggle
        if (OVRInput.GetDown(toggleButton))
            Toggle();
    }

    // Called by UI button OnClick OR controller
    public void Toggle()
    {
        IsPassthrough = !IsPassthrough;
        Apply();
    }

    void Apply()
    {
        // ── Video quad ───────────────────────────────
        if (videoScreenQuad != null)
            videoScreenQuad.SetActive(!IsPassthrough);

        // ── Passthrough layer ────────────────────────
        if (passthroughLayer != null)
            passthroughLayer.enabled = IsPassthrough;

        // ── Camera background ────────────────────────
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
                // Restore to whatever you had before
                // (Skybox if you use one, else SolidColor black)
                mainCamera.clearFlags =
                    CameraClearFlags.Skybox;
            }
        }

        // ── Tell DetectionOverlay to switch mode ─────
        if (detectionOverlay != null)
            detectionOverlay.SetPassthroughMode(
                IsPassthrough
            );

        UpdateUILabel();

        Debug.Log(
            "[ModeManager] Mode = " +
            (IsPassthrough ? "PASSTHROUGH" : "CAMERA FEED")
        );
    }

    void UpdateUILabel()
    {
        if (uiButtonLabel == null) return;

        uiButtonLabel.text = IsPassthrough
            ? "Switch to Camera Feed"
            : "Switch to Passthrough";
    }
}