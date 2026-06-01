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
    public float cameraHFov = 90f;
    public float cameraVFov = 60f;

    [Header("World Position")]
    [Tooltip("Y height of the floor in Unity world space. Set to 0 if your floor is at origin.")]
    public float floorY = 0f;
    [Tooltip("How high above the floor to place markers (metres). ~1.2 = chest height.")]
    public float markerHeightAboveFloor = 1.2f;
    [Tooltip("Additional Y offset on top of floor + height.")]
    public float markerYOffset = 0.2f;

    // ── internals ──────────────────────────────────────────
    private Dictionary<int, GameObject> _pool = new Dictionary<int, GameObject>();
    private Dictionary<int, ThroughWallMarker> _markerPool = new Dictionary<int, ThroughWallMarker>();

    private bool _passthroughMode = true;   // default TRUE — must match ModeManager
    private bool _lastPollFailed = false;
    private Camera _headsetCam;

    void Start()
    {
        _passthroughMode = true;
        _headsetCam = Camera.main;
        Debug.Log("[DetectionOverlay] Start — passthrough=TRUE " +
                  $"markerPrefab={(markerPrefab == null ? "NULL" : "OK")} " +
                  $"stream={streamWidth}x{streamHeight}");
        StartCoroutine(PollLoop());
    }

    public void SetPassthroughMode(bool passthrough)
    {
        _passthroughMode = passthrough;
        Debug.Log("[DetectionOverlay] SetPassthroughMode → " + (passthrough ? "PASSTHROUGH" : "CAMERA FEED"));

        if (_passthroughMode)
        {
            // Entering passthrough: hide 2D labels, show 3D markers
            foreach (var go in _pool.Values)
                if (go != null) go.SetActive(false);
        }
        else
        {
            // Entering camera feed: hide 3D markers, show 2D labels
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
                {
                    Debug.Log($"[DetectionOverlay] Got {data.detections.Count} detections | passthrough={_passthroughMode}");
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
        // Passthrough mode → 3D world-space markers (visible through walls via OVR)
        // Camera feed mode → 2D labels overlaid on the video quad
        if (_passthroughMode)
            UpdateMarkers(detections);
        else
            UpdateLabels(detections);
    }

    // ── YAW-ONLY direction — strips camera pitch completely ──────────────────
    // Using only yaw prevents the ray from tilting downward when the user looks
    // slightly down, which was causing markers to drift in Z and then in Y.
    Vector3 PixelToWorldDirection(float pixelX)
    {
        float nx = (pixelX / streamWidth) - 0.5f;                    // -0.5..+0.5
        float tanH = Mathf.Tan(cameraHFov * 0.5f * Mathf.Deg2Rad);

        // Only use the headset's yaw — no pitch, no roll
        float yaw = _headsetCam.transform.eulerAngles.y;
        Quaternion yawOnly = Quaternion.Euler(0f, yaw, 0f);

        Vector3 flatForward = yawOnly * Vector3.forward;
        Vector3 flatRight = yawOnly * Vector3.right;

        return (flatForward + flatRight * (nx * 2f * tanH)).normalized;
    }

    // ── Passthrough: 3D world-space markers ─────────────────────────────────
    void UpdateMarkers(List<Detection> detections)
    {
        if (markerPrefab == null)
        {
            Debug.LogError("[DetectionOverlay] markerPrefab is NULL — assign it in the Inspector!");
            return;
        }

        if (_headsetCam == null) _headsetCam = Camera.main;

        for (int i = 0; i < detections.Count; i++)
        {
            var det = detections[i];

            float cx = (det.x1 + det.x2) * 0.5f;
            Vector3 dir = PixelToWorldDirection(cx);

            float dist = (det.depth_m > 0.1f && det.depth_m < 20f) ? det.depth_m : 2.0f;

            Vector3 camPos = _headsetCam.transform.position;

            // ── FIX: Y is ALWAYS pinned to a fixed world-space height ────────
            // Never use camPos.y here — that drifts as the headset moves up/down.
            // floorY + markerHeightAboveFloor places the marker at a stable height
            // regardless of where the user's head is vertically.
            Vector3 target = new Vector3(
                camPos.x + dir.x * dist,
                floorY + markerHeightAboveFloor + markerYOffset,
                camPos.z + dir.z * dist
            );

            // Get or create marker
            if (!_markerPool.ContainsKey(i) || _markerPool[i] == null)
            {
                var go = Instantiate(markerPrefab, transform);
                _markerPool[i] = go.GetComponent<ThroughWallMarker>();
                if (_markerPool[i] == null)
                {
                    Debug.LogError("[DetectionOverlay] markerPrefab is missing ThroughWallMarker component!");
                    continue;
                }
            }

            string className = det.label.Split(' ')[0];
            Debug.Log($"[DetectionOverlay] Marker {i} → {className} depth={det.depth_m:F2}m pos={target}");
            _markerPool[i].SetData(className, det.depth_m, target);
        }

        // Hide unused pool slots
        for (int i = detections.Count; i < _markerPool.Count; i++)
            if (_markerPool.ContainsKey(i) && _markerPool[i] != null)
                _markerPool[i].Hide();
    }

    // ── Camera feed: 2D labels on video quad ────────────────────────────────
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

            go.transform.position = PixelToWorld(u, v, det.y1);
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
        float y = Mathf.Lerp(b.max.y, b.min.y, (float)pixelY / streamHeight)
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