using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;

// ── JSON shapes (unchanged) ───────────────────────────────────
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
    public GameObject labelPrefab;      // ORIGINAL prefab — untouched

    // ── NEW: 3D wireframe box prefab ──────────────────────────
    [Header("3D Wireframe Box (Passthrough mode)")]
    public GameObject wireframeBoxPrefab; // DetectionBox3D prefab

    // How deep to make the box when no SLAM Z size is available
    public float defaultBoxDepth = 0.3f;

    [Header("World anchor")]
    public Vector3 worldOrigin = Vector3.zero;
    public float smoothSpeed = 5f;

    // ── internals (original) ──────────────────────────────────
    private Dictionary<int, GameObject> _pool = new();
    private Dictionary<int, Detection> _lastDet = new();
    private bool _slamTracking = false;

    // ── NEW internals ─────────────────────────────────────────
    private bool _passthroughMode = false;
    private Dictionary<int, GameObject> _boxPool = new();

    // ─────────────────────────────────────────────────────────
    void Start() => StartCoroutine(PollLoop());

    // ── NEW: called by ModeManager ────────────────────────────
    public void SetPassthroughMode(bool passthrough)
    {
        _passthroughMode = passthrough;

        // Hide whichever pool is NOT active
        if (_passthroughMode)
        {
            // Hide old 2D labels
            foreach (var go in _pool.Values)
                if (go != null) go.SetActive(false);
        }
        else
        {
            // Hide 3D boxes
            foreach (var go in _boxPool.Values)
                if (go != null) go.SetActive(false);
        }
    }

    // ── Polling coroutine (unchanged) ─────────────────────────
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
                var data =
                    JsonUtility.FromJson<DetectionResponse>(
                        req.downloadHandler.text);

                if (data?.detections != null)
                    UpdateOverlay(data.detections);
            }
            else
            {
                Debug.LogWarning(
                    "[Detections] " + req.error);
                HideAll();
            }

            yield return new WaitForSeconds(pollInterval);
        }
    }

    // ── Main update ───────────────────────────────────────────
    void UpdateOverlay(List<Detection> detections)
    {
        _slamTracking =
            detections.Count > 0 &&
            detections[0].tracking;

        if (_passthroughMode)
            UpdateBoxes(detections);     // NEW path
        else
            UpdateLabels(detections);    // ORIGINAL path
    }

    // ── ORIGINAL label logic — completely unchanged ───────────
    void UpdateLabels(List<Detection> detections)
    {
        for (int i = 0; i < detections.Count; i++)
        {
            if (!_pool.ContainsKey(i))
                _pool[i] = Instantiate(
                    labelPrefab, transform);

            var go = _pool[i];
            var det = detections[i];
            go.SetActive(true);

            var tmp =
                go.GetComponentInChildren<TextMeshPro>();
            if (tmp != null)
            {
                string trackTag =
                    det.tracking ? "" : " [no pose]";
                tmp.text = det.label + trackTag;
                tmp.color = det.tracking
                    ? Color.white
                    : Color.yellow;
            }

            Vector3 targetPos;

            if (det.tracking &&
                (det.world_x != 0 ||
                 det.world_y != 0 ||
                 det.world_z != 0))
            {
                targetPos = worldOrigin + new Vector3(
                    -det.world_x,
                    -det.world_y,
                     det.world_z);
            }
            else
            {
                float u =
                    (det.x1 + det.x2) * 0.5f /
                    streamWidth;
                float v =
                    (det.y1 + det.y2) * 0.5f /
                    streamHeight;
                targetPos = PixelToWorld(u, v, det.y1);
            }

            go.transform.position = Vector3.Lerp(
                go.transform.position,
                targetPos,
                Time.deltaTime * smoothSpeed);

            _lastDet[i] = det;
        }

        for (int i = detections.Count;
             i < _pool.Count; i++)
            if (_pool.ContainsKey(i))
                _pool[i].SetActive(false);
    }

    // ── NEW: 3D wireframe box logic ───────────────────────────
    void UpdateBoxes(List<Detection> detections)
    {
        for (int i = 0; i < detections.Count; i++)
        {
            // Lazy-instantiate from pool
            if (!_boxPool.ContainsKey(i) ||
                _boxPool[i] == null)
            {
                _boxPool[i] = Instantiate(
                    wireframeBoxPrefab, transform);
            }

            var go = _boxPool[i];
            var det = detections[i];
            go.SetActive(true);

            var box =
                go.GetComponent<WireframeBox>();
            if (box == null) continue;

            Vector3 targetCenter;
            Vector3 boxSize;

            if (det.tracking &&
                (det.world_x != 0 ||
                 det.world_y != 0 ||
                 det.world_z != 0))
            {
                // World-space anchor from SLAM
                targetCenter = worldOrigin +
                    new Vector3(
                        -det.world_x,
                        -det.world_y,
                         det.world_z);

                // Estimate physical size from pixel
                // bbox + depth
                float d = det.depth_m > 0
                    ? det.depth_m : 1f;

                // Rough metric size via similar triangles
                // assuming ~60 deg horizontal FOV
                float fovFactor = d * 0.93f;

                float wMeters =
                    ((det.x2 - det.x1) /
                    (float)streamWidth) * fovFactor;

                float hMeters =
                    ((det.y2 - det.y1) /
                    (float)streamHeight) * fovFactor;

                boxSize = new Vector3(
                    Mathf.Max(wMeters, 0.1f),
                    Mathf.Max(hMeters, 0.1f),
                    defaultBoxDepth
                );
            }
            else
            {
                // SLAM not tracking — place box on
                // video quad surface as fallback
                float u =
                    (det.x1 + det.x2) * 0.5f /
                    streamWidth;
                float v =
                    (det.y1 + det.y2) * 0.5f /
                    streamHeight;

                targetCenter =
                    PixelToWorld(u, v, det.y1);

                // Scale box to match quad proportions
                if (videoScreen != null)
                {
                    var b = videoScreen.bounds;
                    float wM = ((det.x2 - det.x1) /
                        (float)streamWidth) *
                        b.size.x;
                    float hM = ((det.y2 - det.y1) /
                        (float)streamHeight) *
                        b.size.y;
                    boxSize = new Vector3(
                        Mathf.Max(wM, 0.05f),
                        Mathf.Max(hM, 0.05f),
                        0.001f // flat on quad surface
                    );
                }
                else
                {
                    boxSize = new Vector3(
                        0.2f, 0.2f, defaultBoxDepth);
                }
            }

            // Smooth position
            Vector3 smoothedCenter = Vector3.Lerp(
                go.transform.position,
                targetCenter,
                Time.deltaTime * smoothSpeed);

            // Extract just the class name before
            // the confidence/depth suffix for clean label
            string cleanLabel =
                det.label.Split(' ')[0];

            box.UpdateBox(
                smoothedCenter,
                boxSize,
                cleanLabel);
        }

        // Hide unused pool entries
        for (int i = detections.Count;
             i < _boxPool.Count; i++)
            if (_boxPool.ContainsKey(i))
                _boxPool[i].SetActive(false);
    }

    // ── Fallback pixel→world (unchanged) ─────────────────────
    Vector3 PixelToWorld(float u, float v, int pixelY)
    {
        if (videoScreen == null) return Vector3.zero;
        var b = videoScreen.bounds;
        float x = Mathf.Lerp(b.min.x, b.max.x, u);
        float topV =
            (float)pixelY / streamHeight;
        float y =
            Mathf.Lerp(b.max.y, b.min.y, topV) +
            b.size.y * 0.03f;
        float z = b.min.z - 0.01f;
        return new Vector3(x, y, z);
    }

    // ── HideAll hides both pools ──────────────────────────────
    void HideAll()
    {
        foreach (var go in _pool.Values)
            if (go != null) go.SetActive(false);

        foreach (var go in _boxPool.Values)
            if (go != null) go.SetActive(false);
    }

    // ── Debug gizmo (unchanged) ───────────────────────────────
    void OnDrawGizmos()
    {
        Gizmos.color = _slamTracking
            ? Color.green : Color.red;
        Gizmos.DrawWireSphere(worldOrigin, 0.1f);
        Gizmos.DrawLine(
            worldOrigin,
            worldOrigin + Vector3.up * 0.3f);
    }
}