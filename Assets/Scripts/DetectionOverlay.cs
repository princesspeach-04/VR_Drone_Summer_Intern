using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;

[System.Serializable]
public class Detection
{
    public string label;
    public int x1, y1, x2, y2;
    public float depth_m;
    public float world_x, world_y, world_z;
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

    [Header("Scene refs")]
    public Renderer videoScreen;
    public GameObject labelPrefab;

    [Header("Through-Wall Marker")]
    public GameObject markerPrefab;

    [Header("Camera FOV (degrees) — match A8 mini lens")]
    public float cameraHFov = 90f;   // horizontal FOV of A8 mini
    public float cameraVFov = 60f;   // vertical FOV

    [Header("Smoothing")]
    public float smoothSpeed = 5f;

    // ── internals ──────────────────────────────────────────
    private Dictionary<int, GameObject> _pool
        = new Dictionary<int, GameObject>();
    private Dictionary<int, ThroughWallMarker> _markerPool
        = new Dictionary<int, ThroughWallMarker>();
    private Dictionary<int, Vector3> _smoothedPositions
        = new Dictionary<int, Vector3>();

    private bool _passthroughMode = false;
    private bool _lastPollFailed = false;
    private Camera _headsetCam;

    void Start()
    {
        // Always start in passthrough mode
        _passthroughMode = true;

        _headsetCam = Camera.main;

        Debug.Log("[DetectionOverlay] Start — " +
                  $"markerPrefab={(markerPrefab == null ? "NULL" : "OK")}");
        StartCoroutine(PollLoop());
    }

    public void SetPassthroughMode(bool passthrough)
    {
        _passthroughMode = passthrough;
        Debug.Log($"[DetectionOverlay] Mode → " +
                  (passthrough ? "PASSTHROUGH" : "CAMERA FEED"));

        if (_passthroughMode)
        {
            foreach (var go in _pool.Values)
                if (go != null) go.SetActive(false);
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
                var data = JsonUtility.FromJson<DetectionResponse>(
                    req.downloadHandler.text);

                if (data?.detections != null)
                {
                    Debug.Log($"[DetectionOverlay] Got " +
                              $"{data.detections.Count} detections" +
                              $" | passthrough={_passthroughMode}");
                    UpdateOverlay(data.detections);
                }
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
        else
            UpdateLabels(detections);
    }

    // ── Passthrough: place marker in world space ───────────
    void UpdateMarkers(List<Detection> detections)
    {
        if (markerPrefab == null)
        {
            Debug.LogError("[DetectionOverlay] markerPrefab is NULL!");
            return;
        }

        if (_headsetCam == null)
            _headsetCam = Camera.main;

        for (int i = 0; i < detections.Count; i++)
        {
            var det = detections[i];

            // Centre pixel of bounding box
            float cx = (det.x1 + det.x2) * 0.5f;
            float cy = (det.y1 + det.y2) * 0.5f;

            // Normalised -0.5..+0.5
            float nx = (cx / streamWidth) - 0.5f;
            float ny = (cy / streamHeight) - 0.5f;   // +y = down in image

            // Convert pixel offset to angles
            float yawOffset = nx * cameraHFov;    // left/right
            float pitchOffset = -ny * cameraVFov;    // up/down (flip Y)

            // Build a direction from headset forward + those offsets
            // We rotate the headset's forward vector by these angles
            Quaternion rot = _headsetCam.transform.rotation
                * Quaternion.Euler(-pitchOffset, yawOffset, 0f);
            Vector3 dir = rot * Vector3.forward;

            // Place marker at detected depth, or 2m fallback
            float dist = (det.depth_m > 0.1f && det.depth_m < 20f)
                ? det.depth_m : 2.0f;

            Vector3 rawTarget = _headsetCam.transform.position
                              + dir * dist;

            // Lift label slightly above centre of bounding box
            // (move it toward the top of the box in view space)
            float topNy = (det.y1 / (float)streamHeight) - 0.5f;
            float topPitch = -topNy * cameraVFov;
            Quaternion topRot = _headsetCam.transform.rotation
                * Quaternion.Euler(-topPitch, yawOffset, 0f);
            Vector3 topDir = topRot * Vector3.forward;
            Vector3 topTarget = _headsetCam.transform.position
                              + topDir * dist
                              + Vector3.up * 0.15f;   // extra nudge up

            // Smooth position
            if (!_smoothedPositions.ContainsKey(i))
                _smoothedPositions[i] = topTarget;

            _smoothedPositions[i] = Vector3.Lerp(
                _smoothedPositions[i],
                topTarget,
                Time.deltaTime * smoothSpeed);

            // Get or create marker
            if (!_markerPool.ContainsKey(i) || _markerPool[i] == null)
            {
                var go = Instantiate(markerPrefab, transform);
                _markerPool[i] = go.GetComponent<ThroughWallMarker>();

                if (_markerPool[i] == null)
                {
                    Debug.LogError("[DetectionOverlay] ThroughWallMarker component missing!");
                    continue;
                }
            }

            string className = det.label.Split(' ')[0];

            Debug.Log($"[DetectionOverlay] Marker {i} → " +
                      $"{className} depth={det.depth_m}m " +
                      $"pos={_smoothedPositions[i]}");

            _markerPool[i].SetData(className, det.depth_m, _smoothedPositions[i]);
        }

        // Hide unused markers
        for (int i = detections.Count; i < _markerPool.Count; i++)
            if (_markerPool.ContainsKey(i) && _markerPool[i] != null)
                _markerPool[i].Hide();
    }

    // ── Camera feed: 2D labels on video quad ───────────────
    void UpdateLabels(List<Detection> detections)
    {
        for (int i = 0; i < detections.Count; i++)
        {
            if (!_pool.ContainsKey(i))
                _pool[i] = Instantiate(labelPrefab, transform);

            var go = _pool[i];
            var det = detections[i];
            go.SetActive(true);

            var tmp = go.GetComponentInChildren<TextMeshProUGUI>();
            if (tmp != null)
            {
                tmp.text = det.label;
                tmp.color = Color.white;
            }

            float u = (det.x1 + det.x2) * 0.5f / streamWidth;
            float v = (det.y1 + det.y2) * 0.5f / streamHeight;

            Vector3 target = PixelToWorld(u, v, det.y1);
            go.transform.position = Vector3.Lerp(
                go.transform.position,
                target,
                Time.deltaTime * smoothSpeed);
        }

        for (int i = detections.Count; i < _pool.Count; i++)
            if (_pool.ContainsKey(i))
                _pool[i].SetActive(false);
    }

    Vector3 PixelToWorld(float u, float v, int pixelY)
    {
        if (videoScreen == null) return Vector3.zero;
        var b = videoScreen.bounds;
        float x = Mathf.Lerp(b.min.x, b.max.x, u);
        float y = Mathf.Lerp(b.max.y, b.min.y,
                      (float)pixelY / streamHeight)
                  + b.size.y * 0.03f;
        float z = b.min.z - 0.01f;
        return new Vector3(x, y, z);
    }

    void HideAll()
    {
        foreach (var go in _pool.Values)
            if (go != null) go.SetActive(false);
        foreach (var m in _markerPool.Values)
            if (m != null) m.Hide();
    }

    void OnDrawGizmos()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, 0.1f);
    }
}