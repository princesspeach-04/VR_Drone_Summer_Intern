using UnityEngine;
using TMPro;

public class ThroughWallMarker : MonoBehaviour
{
    [Header("UI")]
    public TextMeshProUGUI labelText;
    public LineRenderer lineToGround;

    [Header("Appearance")]
    public Color activeColor = new Color(0.2f, 0.8f, 1f, 1f);
    public Color staleColor = new Color(0.5f, 0.5f, 0.5f, 0.6f);
    public float pulseSpeed = 2f;
    public float pulseMinScale = 0.9f;
    public float pulseMaxScale = 1.1f;

    private Camera _cam;
    private float _timeSinceUpdate;
    private bool _active;
    private Vector3 _baseScale;

    void Awake()
    {
        _cam = Camera.main;
        _baseScale = transform.localScale;
    }

    void Update()
    {
        if (!_active) return;

        // Billboard: always face the headset camera
        if (_cam != null)
            transform.rotation = Quaternion.LookRotation(
                transform.position - _cam.transform.position);

        // Pulse scale
        float s = Mathf.Lerp(
            pulseMinScale, pulseMaxScale,
            (Mathf.Sin(Time.time * pulseSpeed) + 1f) * 0.5f);
        transform.localScale = _baseScale * s;

        // Fade to stale colour if no update for 1 s
        _timeSinceUpdate += Time.deltaTime;
        if (labelText != null)
            labelText.color =
                _timeSinceUpdate > 1f ? staleColor : activeColor;
    }

    /// <summary>Called every poll cycle by DetectionOverlay.</summary>
    public void SetData(string className,
                        float depthMeters,
                        Vector3 worldPosition)
    {
        transform.position = worldPosition;
        _timeSinceUpdate = 0f;
        _active = true;

        if (labelText != null)
        {
            string dist = depthMeters > 0
                ? $"{depthMeters:0.0} m"
                : "-- m";
            labelText.text = $"{className}\n{dist}";
            labelText.color = activeColor;
        }

        // Vertical drop-line to ground (optional)
        if (lineToGround != null)
        {
            lineToGround.SetPosition(0, worldPosition);
            lineToGround.SetPosition(1,
                new Vector3(worldPosition.x, 0f,
                            worldPosition.z));
        }

        gameObject.SetActive(true);
    }

    public void Hide()
    {
        _active = false;
        gameObject.SetActive(false);
    }
}