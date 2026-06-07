using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

[System.Serializable]
public class Detection
{
    public string label;
    public int x1, y1, x2, y2;
    public float depth_m;
    public bool tracking;
}

[System.Serializable]
public class DetectionResponse
{
    public List<Detection> detections;
}

public class DetectionOverlay : MonoBehaviour
{
    [Header("Network")]
    public string jetsonHostname = "100.94.87.80";
    public int jetsonPort = 8080;
    public float pollInterval = 0.15f;

    [Header("Stream resolution (must match Python)")]
    public int streamWidth = 640;
    public int streamHeight = 360;

    [Header("Fixed Camera — World Geometry")]
    public Vector3 cameraWorldPosition = Vector3.zero;
    public Vector3 cameraWorldEuler = Vector3.zero;
    public float cameraHFov = 90f;
    public float minDepth = 0.3f;
    public float maxDepth = 15f;
    public float fallbackDepth = 3f;

    [Header("Calibration Trim")]
    [Tooltip("Each thumbstick step changes trim by this many degrees.")]
    public float trimStepDegrees = 1f;

    [Tooltip("How long (seconds) before the next step triggers while holding the stick.")]
    public float trimRepeatDelay = 0.2f;

    public float yawTrimDegrees = 0f;
    public float pitchTrimDegrees = 0f;

    [Header("Scene refs")]
    public Renderer videoScreen;
    public GameObject markerPrefab;

    // ── internals ──────────────────────────────────────────────
    private Quaternion _baseRotation;
    private Quaternion _cameraWorldRotation;

    private float _yawRepeatTimer = 0f;
    private float _pitchRepeatTimer = 0f;
    private bool _yawWasNeutral = true;
    private bool _pitchWasNeutral = true;

    private Dictionary<int, ThroughWallMarker> _markerPool = new();
    private bool _passthroughMode = false;
    private bool _lastPollFailed = false;

    void Start()
    {
        _baseRotation = Camera.main.transform.rotation;
        RebuildRotation();

        _passthroughMode = true;
        StartCoroutine(PollLoop());
    }

    void Update()
    {
        Vector2 stick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);

        bool yawChanged = HandleAxis(stick.x, ref yawTrimDegrees,
                                       ref _yawWasNeutral, ref _yawRepeatTimer);
        bool pitchChanged = HandleAxis(stick.y, ref pitchTrimDegrees,
                                       ref _pitchWasNeutral, ref _pitchRepeatTimer);

        if (yawChanged || pitchChanged)
        {
            RebuildRotation();
            Debug.Log($"[Calib] Yaw={yawTrimDegrees:F1}°  Pitch={pitchTrimDegrees:F1}°" +
                      $"  Forward={_cameraWorldRotation * Vector3.forward}");
        }

        // B button = reset trim to zero
        if (OVRInput.GetDown(OVRInput.Button.Two))
        {
            yawTrimDegrees = 0f;
            pitchTrimDegrees = 0f;
            RebuildRotation();
            Debug.Log("[Calib] Trim reset to 0");
        }
    }

    bool HandleAxis(float axis, ref float trim,
                    ref bool wasNeutral, ref float repeatTimer)
    {
        const float deadzone = 0.5f;
        bool active = Mathf.Abs(axis) > deadzone;

        if (!active)
        {
            wasNeutral = true;
            repeatTimer = 0f;
            return false;
        }

        float sign = Mathf.Sign(axis);

        if (wasNeutral)
        {
            trim += sign * trimStepDegrees;
            wasNeutral = false;
            repeatTimer = 0f;
            return true;
        }

        repeatTimer += Time.deltaTime;
        if (repeatTimer >= trimRepeatDelay)
        {
            trim += sign * trimStepDegrees;
            repeatTimer = 0f;
            return true;
        }

        return false;
    }

    void RebuildRotation()
    {
        _cameraWorldRotation = _baseRotation
            * Quaternion.Euler(-pitchTrimDegrees, yawTrimDegrees, 0f);
    }

    public void SetPassthroughMode(bool passthrough)
    {
        _passthroughMode = passthrough;
        if (!_passthroughMode)
            foreach (var m in _markerPool.Values)
                if (m != null) m.Hide();
    }

    IEnumerator PollLoop()
    {
        string url = $"http://{jetsonHostname}:{jetsonPort}/detections";
        while (true)
        {
            using var req = UnityWebRequest.Get(url);
            req.timeout = 2;
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                _lastPollFailed = false;
                var data = JsonUtility.FromJson<DetectionResponse>(req.downloadHandler.text);
                if (data?.detections != null)
                    UpdateOverlay(data.detections);
            }
            else
            {
                if (!_lastPollFailed)
                    Debug.LogWarning("[DetectionOverlay] Poll failed: " + req.error);
                _lastPollFailed = true;
                HideAll();
            }

            yield return new WaitForSeconds(pollInterval);
        }
    }

    void UpdateOverlay(List<Detection> detections)
    {
        if (_passthroughMode) UpdateMarkers(detections);
    }

    Vector3 DetectionToWorldPosition(Detection det)
    {
        float cx = (det.x1 + det.x2) * 0.5f;
        float cy = (det.y1 + det.y2) * 0.5f;

        float ndcX = -(cx / streamWidth) - 0.5f;
        float ndcY = (cy / streamHeight) + 0.5f;

        float tanH = Mathf.Tan(cameraHFov * 0.5f * Mathf.Deg2Rad);
        float tanV = tanH * ((float)streamHeight / streamWidth);

        Vector3 localRay = new Vector3(
            ndcX * 2f * tanH,
            ndcY * 2f * tanV,
            1f
        ).normalized;

        Vector3 worldRay = _cameraWorldRotation * localRay;

        float depth = (det.depth_m > minDepth && det.depth_m < maxDepth)
                      ? det.depth_m : fallbackDepth;

        return cameraWorldPosition + worldRay * depth;
    }

    void UpdateMarkers(List<Detection> detections)
    {
        if (markerPrefab == null) { Debug.LogError("markerPrefab is NULL"); return; }

        for (int i = 0; i < detections.Count; i++)
        {
            if (!_markerPool.ContainsKey(i) || _markerPool[i] == null)
            {
                var go = Instantiate(markerPrefab, transform);
                _markerPool[i] = go.GetComponent<ThroughWallMarker>();
                if (_markerPool[i] == null)
                {
                    Destroy(go);
                    continue;
                }
            }

            Detection det = detections[i];
            Vector3 worldPos = DetectionToWorldPosition(det);
            _markerPool[i].SetData(det.label.Split(' ')[0], det.depth_m, worldPos);
        }

        for (int i = detections.Count; i < _markerPool.Count; i++)
            if (_markerPool.ContainsKey(i) && _markerPool[i] != null)
                _markerPool[i].Hide();
    }

    void HideAll()
    {
        foreach (var m in _markerPool.Values)
            if (m != null) m.Hide();
    }

    void OnDrawGizmos()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireCube(cameraWorldPosition, Vector3.one * 0.1f);
        Quaternion rot = Quaternion.Euler(cameraWorldEuler);
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(cameraWorldPosition,
                        cameraWorldPosition + rot * Vector3.forward * 2f);
    }
}