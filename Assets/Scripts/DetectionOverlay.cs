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
    [Tooltip("Camera position in metres relative to Quest world origin.")]
    public Vector3 cameraWorldPosition = Vector3.zero;

    [Tooltip("Camera orientation as Euler angles (degrees). " +
             "Yaw: compass heading (0=+Z, 90=+X). " +
             "Pitch: tilt down is positive. Roll: rarely needed.")]
    public Vector3 cameraWorldEuler = Vector3.zero;

    [Tooltip("Horizontal FOV of the camera in degrees (e.g. 90 for A8 mini).")]
    public float cameraHFov = 90f;

    [Tooltip("Depth clamp range (metres). " +
             "MiDaS values outside this are replaced with fallbackDepth.")]
    public float minDepth = 0.3f;
    public float maxDepth = 15f;
    public float fallbackDepth = 3f;

    [Header("Calibration Trim")]
    [Tooltip("Fine-tune left/right alignment. Negative = shift marker left, Positive = right.")]
    public float yawTrimDegrees = 0f;

    [Tooltip("Fine-tune up/down alignment. Negative = shift marker down, Positive = up.")]
    public float pitchTrimDegrees = 0f;

    [Header("Scene refs")]
    public Renderer videoScreen;
    public GameObject markerPrefab;

    private Quaternion _cameraWorldRotation;
    private Quaternion _baseRotation;

    private Dictionary<int, ThroughWallMarker> _markerPool
        = new Dictionary<int, ThroughWallMarker>();

    private bool _passthroughMode = false;
    private bool _lastPollFailed = false;

    void Start()
    {
        // Base rotation from headset at launch
        _baseRotation = Camera.main.transform.rotation;

        // Apply trim on top
        _cameraWorldRotation = _baseRotation
                             * Quaternion.Euler(-pitchTrimDegrees, yawTrimDegrees, 0f);

        Debug.Log($"[Calib] Camera world rotation set to headset rotation at start");
        Debug.Log($"[Calib] Camera forward in world: {_cameraWorldRotation * Vector3.forward}");

        _passthroughMode = true;
        StartCoroutine(PollLoop());
    }

    void OnValidate()
    {
        _cameraWorldRotation = Quaternion.Euler(cameraWorldEuler);
    }

    void Update()
    {
        // Right thumbstick X to trim yaw in real time
        float stickX = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick).x;
        if (Mathf.Abs(stickX) > 0.1f)
        {
            yawTrimDegrees += stickX * Time.deltaTime * 20f;

            _cameraWorldRotation = _baseRotation
                                 * Quaternion.Euler(-pitchTrimDegrees, yawTrimDegrees, 0f);

            Debug.Log($"[Calib] Yaw trim: {yawTrimDegrees:F1}°  " +
                      $"Camera forward: {_cameraWorldRotation * Vector3.forward}");
        }

        // Right thumbstick Y to trim pitch in real time
        float stickY = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick).y;
        if (Mathf.Abs(stickY) > 0.1f)
        {
            pitchTrimDegrees += stickY * Time.deltaTime * 20f;

            _cameraWorldRotation = _baseRotation
                                 * Quaternion.Euler(-pitchTrimDegrees, yawTrimDegrees, 0f);

            Debug.Log($"[Calib] Pitch trim: {pitchTrimDegrees:F1}°  " +
                      $"Camera forward: {_cameraWorldRotation * Vector3.forward}");
        }
    }

    public void SetPassthroughMode(bool passthrough)
    {
        _passthroughMode = passthrough;
        if (_passthroughMode)
        {
            // 2-D label pool would be hidden here
        }
        else
        {
            foreach (var m in _markerPool.Values)
                if (m != null) m.Hide();
        }
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
        if (_passthroughMode)
            UpdateMarkers(detections);
        // camera-feed mode: Python already drew boxes on the MJPEG stream
    }

    Vector3 DetectionToWorldPosition(Detection det)
    {
        // ① Detection centre
        float cx = (det.x1 + det.x2) * 0.5f;
        float cy = (det.y1 + det.y2) * 0.5f;

        // ② NDC — Y is flipped: pixel (0,0) is top-left, Unity Y is up
        float ndcX = (cx / streamWidth) - 0.5f;
        float ndcY = -(cy / streamHeight) + 0.5f;

        // ③ Half-tangents
        float tanH = Mathf.Tan(cameraHFov * 0.5f * Mathf.Deg2Rad);
        float tanV = tanH * ((float)streamHeight / streamWidth);

        // Local-space ray in camera frame (Z forward, X right, Y up)
        Vector3 localRay = new Vector3(
            ndcX * 2f * tanH,
            ndcY * 2f * tanV,
            1f
        ).normalized;

        // ④ Rotate into world space
        Vector3 worldRay = _cameraWorldRotation * localRay;

        // ⑤⑥ Apply depth from camera world position
        float depth = (det.depth_m > minDepth && det.depth_m < maxDepth)
                      ? det.depth_m
                      : fallbackDepth;

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
                    Debug.LogError("ThroughWallMarker component missing on prefab");
                    Destroy(go);
                    continue;
                }
            }

            Detection det = detections[i];
            Vector3 worldPos = DetectionToWorldPosition(det);
            string className = det.label.Split(' ')[0];

            _markerPool[i].SetData(className, det.depth_m, worldPos);
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

        Gizmos.color = Color.green;
        Gizmos.DrawLine(cameraWorldPosition,
                        cameraWorldPosition + rot * Vector3.up * 0.5f);
    }
}