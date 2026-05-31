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
    public float world_x;
    public float world_y;
    public float world_z;
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
    public string jetsonHostname = "100.116.179.56";
    public int jetsonPort = 8080;
    public float pollInterval = 0.15f;

    [Header("Stream resolution (must match Python)")]
    public int streamWidth = 512;
    public int streamHeight = 288;

    [Header("Scene refs")]
    public Renderer videoScreen;
    public GameObject labelPrefab;

    [Header("Through-Wall Marker")]
    public GameObject markerPrefab;

    [Tooltip("Metres from headset room origin to drone. " +
             "Z = forward, Y = height, X = left/right.")]
    public Vector3 droneWorldPosition = new Vector3(0f, 1.2f, 3f);

    [Header("Smoothing")]
    public float smoothSpeed = 5f;

    // ── internals ─────────────────────────────────────────────
    private Dictionary<int, GameObject> _pool
        = new Dictionary<int, GameObject>();
    private Dictionary<int, ThroughWallMarker> _markerPool
        = new Dictionary<int, ThroughWallMarker>();

    private bool _passthroughMode = false;
    private bool _lastPollFailed = false;

    // ─────────────────────────────────────────────────────────
    void Start()
    {
        Debug.Log("[DetectionOverlay] Start — " +
                  $"markerPrefab={(markerPrefab == null ? " NULL" : "OK")}" +
                  $" dronePos={droneWorldPosition}");
        StartCoroutine(PollLoop());
    }

    // ── Called by ModeManager ─────────────────────────────────
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

    // ── Polling ───────────────────────────────────────────────
    IEnumerator PollLoop()
    {
        string url =
            $"http://{jetsonHostname}:{jetsonPort}/detections";

        while (true)
        {
            using var req = UnityWebRequest.Get(url);
            req.timeout = 2;
            yield return req.SendWebRequest();

            if (req.result ==
                UnityWebRequest.Result.Success)
            {
                _lastPollFailed = false;
                var data =
                    JsonUtility.FromJson<DetectionResponse>(
                        req.downloadHandler.text);

                if (data?.detections != null)
                {
                    Debug.Log(
                        $"[DetectionOverlay] Got " +
                        $"{data.detections.Count} detections" +
                        $" | passthrough={_passthroughMode}");
                    UpdateOverlay(data.detections);
                }
            }
            else
            {
                if (!_lastPollFailed)
                    Debug.LogWarning(
                        "[DetectionOverlay] Poll failed: " +
                        req.error);
                _lastPollFailed = true;
                HideAll();
            }

            yield return new WaitForSeconds(pollInterval);
        }
    }

    // ── Route ─────────────────────────────────────────────────
    void UpdateOverlay(List<Detection> detections)
    {
        if (_passthroughMode)
            UpdateMarkers(detections);
        else
            UpdateLabels(detections);
    }

    // ── Camera-feed: 2D labels on video quad ──────────────────
    void UpdateLabels(List<Detection> detections)
    {
        for (int i = 0; i < detections.Count; i++)
        {
            if (!_pool.ContainsKey(i))
                _pool[i] = Instantiate(labelPrefab, transform);

            var go = _pool[i];
            var det = detections[i];
            go.SetActive(true);

            var tmp =
                go.GetComponentInChildren<TextMeshProUGUI>();
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

    // ── Passthrough: through-wall marker ─────────────────────
    void UpdateMarkers(List<Detection> detections)
    {
        if (markerPrefab == null)
        {
            Debug.LogError(
                "[DetectionOverlay] markerPrefab is NULL — " +
                "assign ThroughWallMarkerPrefab in Inspector");
            return;
        }

        for (int i = 0; i < detections.Count; i++)
        {
            if (!_markerPool.ContainsKey(i) ||
                _markerPool[i] == null)
            {
                var go = Instantiate(markerPrefab, transform);
                _markerPool[i] =
                    go.GetComponent<ThroughWallMarker>();

                if (_markerPool[i] == null)
                {
                    Debug.LogError(
                        "[DetectionOverlay] markerPrefab has " +
                        "no ThroughWallMarker component!");
                    continue;
                }
            }

            var marker = _markerPool[i];
            var det = detections[i];

            string className = det.label.Split(' ')[0];

            // Stack multiple detections vertically
            Vector3 pos = droneWorldPosition +
                          Vector3.up * (i * 0.4f);

            Debug.Log(
                $"[DetectionOverlay] Marker {i} → " +
                $"{className} depth={det.depth_m}m " +
                $"pos={pos}");

            marker.SetData(className, det.depth_m, pos);
        }

        for (int i = detections.Count;
             i < _markerPool.Count; i++)
            if (_markerPool.ContainsKey(i) &&
                _markerPool[i] != null)
                _markerPool[i].Hide();
    }

    // ── Pixel → world on video quad ──────────────────────────
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

    // ── Hide all ──────────────────────────────────────────────
    void HideAll()
    {
        foreach (var go in _pool.Values)
            if (go != null) go.SetActive(false);
        foreach (var m in _markerPool.Values)
            if (m != null) m.Hide();
    }

    // ── Scene view gizmo — shows drone anchor ────────────────
    void OnDrawGizmos()
    {
        // Cyan sphere at drone position
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(droneWorldPosition, 0.2f);
        Gizmos.DrawLine(droneWorldPosition,
            droneWorldPosition + Vector3.up * 0.5f);
    }
}