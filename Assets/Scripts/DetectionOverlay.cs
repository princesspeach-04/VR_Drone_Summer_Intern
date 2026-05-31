using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;

// ── JSON shapes ───────────────────────────────────────────────
[System.Serializable]
public class Detection
{
    public string label;
    public int x1, y1, x2, y2;
    public float depth_m;
    // world_x/y/z and tracking kept for JSON
    // compatibility but not used in marker mode
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

// ── Main component ────────────────────────────────────────────
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

    [Tooltip("Physical offset in meters from the headset " +
             "room origin to where the drone sits. " +
             "Measure once and set in the Inspector.")]
    public Vector3 droneWorldPosition = new Vector3(0f, 1.2f, 5f);

    [Header("World anchor / smoothing")]
    public Vector3 worldOrigin = Vector3.zero;
    public float smoothSpeed = 5f;

    // ── internals ─────────────────────────────────────────────
    private Dictionary<int, GameObject> _pool = new();
    private Dictionary<int, Detection> _lastDet = new();
    private Dictionary<int, ThroughWallMarker> _markerPool = new();

    private bool _passthroughMode = false;

    // ─────────────────────────────────────────────────────────
    void Start() => StartCoroutine(PollLoop());

    // ── Called by ModeManager ─────────────────────────────────
    public void SetPassthroughMode(bool passthrough)
    {
        _passthroughMode = passthrough;

        if (_passthroughMode)
        {
            // Hide 2D label pool
            foreach (var go in _pool.Values)
                if (go != null) go.SetActive(false);
        }
        else
        {
            // Hide through-wall markers
            foreach (var m in _markerPool.Values)
                if (m != null) m.Hide();
        }
    }

    // ── Polling coroutine ─────────────────────────────────────
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
                var data = JsonUtility.FromJson<DetectionResponse>(
                    req.downloadHandler.text);

                if (data?.detections != null)
                    UpdateOverlay(data.detections);
            }
            else
            {
                Debug.LogWarning("[Detections] " + req.error);
                HideAll();
            }

            yield return new WaitForSeconds(pollInterval);
        }
    }

    // ── Route by mode ─────────────────────────────────────────
    void UpdateOverlay(List<Detection> detections)
    {
        if (_passthroughMode)
            UpdateMarkers(detections);
        else
            UpdateLabels(detections);
    }

    // ── Camera-feed mode: 2D labels on video quad ─────────────
    void UpdateLabels(List<Detection> detections)
    {
        for (int i = 0; i < detections.Count; i++)
        {
            if (!_pool.ContainsKey(i))
                _pool[i] = Instantiate(labelPrefab, transform);

            var go = _pool[i];
            var det = detections[i];
            go.SetActive(true);

            var tmp = go.GetComponentInChildren<TextMeshPro>();
            if (tmp != null)
            {
                tmp.text = det.label;
                tmp.color = Color.white;
            }

            // Position on video quad via UV mapping
            float u = (det.x1 + det.x2) * 0.5f / streamWidth;
            float v = (det.y1 + det.y2) * 0.5f / streamHeight;

            Vector3 targetPos = PixelToWorld(u, v, det.y1);

            go.transform.position = Vector3.Lerp(
                go.transform.position,
                targetPos,
                Time.deltaTime * smoothSpeed);

            _lastDet[i] = det;
        }

        for (int i = detections.Count; i < _pool.Count; i++)
            if (_pool.ContainsKey(i))
                _pool[i].SetActive(false);
    }

    // ── Passthrough mode: through-wall marker ─────────────────
    void UpdateMarkers(List<Detection> detections)
    {
        for (int i = 0; i < detections.Count; i++)
        {
            // Lazy-instantiate from pool
            if (!_markerPool.ContainsKey(i) ||
                _markerPool[i] == null)
            {
                var go = Instantiate(markerPrefab, transform);
                _markerPool[i] =
                    go.GetComponent<ThroughWallMarker>();
            }

            var marker = _markerPool[i];
            var det = detections[i];

            // Parse class name (strip confidence/depth suffix)
            string className = det.label.Split(' ')[0];

            // Stack multiple detections slightly upward
            // so labels don't overlap
            Vector3 pos = droneWorldPosition +
                          Vector3.up * (i * 0.35f);

            marker.SetData(className, det.depth_m, pos);
        }

        // Hide unused markers
        for (int i = detections.Count; i < _markerPool.Count; i++)
            if (_markerPool.ContainsKey(i) &&
                _markerPool[i] != null)
                _markerPool[i].Hide();
    }

    // ── Pixel → world-space on video quad ────────────────────
    Vector3 PixelToWorld(float u, float v, int pixelY)
    {
        if (videoScreen == null) return Vector3.zero;
        var b = videoScreen.bounds;
        float x = Mathf.Lerp(b.min.x, b.max.x, u);
        float topV = (float)pixelY / streamHeight;
        float y = Mathf.Lerp(b.max.y, b.min.y, topV)
                     + b.size.y * 0.03f;
        float z = b.min.z - 0.01f;
        return new Vector3(x, y, z);
    }

    // ── Hide everything ───────────────────────────────────────
    void HideAll()
    {
        foreach (var go in _pool.Values)
            if (go != null) go.SetActive(false);

        foreach (var m in _markerPool.Values)
            if (m != null) m.Hide();
    }

    // ── Editor gizmo: shows drone anchor in scene view ────────
    void OnDrawGizmos()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(droneWorldPosition, 0.15f);
        Gizmos.DrawLine(
            droneWorldPosition,
            droneWorldPosition + Vector3.up * 0.4f);
        Gizmos.color = Color.white;
        Gizmos.DrawWireSphere(worldOrigin, 0.08f);
    }
}