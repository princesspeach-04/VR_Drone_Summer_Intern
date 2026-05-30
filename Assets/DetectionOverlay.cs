using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;

// ── JSON shapes ──────────────────────────────────────────────
[System.Serializable]
public class Detection
{
    public string label;
    public int x1, y1, x2, y2;
    public float depth_m;
    public float world_x;
    public float world_y;
    public float world_z;
    public bool tracking;   // true = SLAM has a valid pose
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
    public Renderer videoScreen;    // quad showing the MJPEG stream
    public GameObject labelPrefab;    // TMP Billboard prefab

    [Header("World anchor")]
    // Set this to the real-world position of your drone's takeoff point
    // so Unity world space matches SLAM world space.
    public Vector3 worldOrigin = Vector3.zero;

    // Smooth speed for label movement (higher = snappier)
    public float smoothSpeed = 5f;

    // internals
    private Dictionary<int, GameObject> _pool = new();
    private Dictionary<int, Detection> _lastDet = new();
    private bool _slamTracking = false;

    void Start() => StartCoroutine(PollLoop());

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

    // ── Place / move labels ───────────────────────────────────
    void UpdateOverlay(List<Detection> detections)
    {
        _slamTracking = detections.Count > 0 && detections[0].tracking;

        for (int i = 0; i < detections.Count; i++)
        {
            if (!_pool.ContainsKey(i))
                _pool[i] = Instantiate(labelPrefab, transform);

            var go = _pool[i];
            var det = detections[i];
            go.SetActive(true);

            // Update label text
            var tmp = go.GetComponentInChildren<TextMeshPro>();
            if (tmp != null)
            {
                string trackTag = det.tracking ? "" : " [no pose]";
                tmp.text = det.label + trackTag;
                tmp.color = det.tracking ? Color.white : Color.yellow;
            }

            // ── Choose position source ────────────────────────
            Vector3 targetPos;

            if (det.tracking && (det.world_x != 0 || det.world_y != 0 || det.world_z != 0))
            {
                // SLAM has a valid pose — anchor label in world space.
                // ORB-SLAM3: right-handed, Z forward, Y down.
                // Unity:     left-handed,  Z forward, Y up.
                // Flip X to convert handedness, negate Y to flip up/down.
                targetPos = worldOrigin + new Vector3(
                    -det.world_x,
                     -det.world_y,
                      det.world_z);
            }
            else
            {
                // SLAM not yet tracking — fall back to screen-space
                // position on the video quad (same as before).
                float u = (det.x1 + det.x2) * 0.5f / streamWidth;
                float v = (det.y1 + det.y2) * 0.5f / streamHeight;
                targetPos = PixelToWorld(u, v, det.y1);
            }

            // Smooth movement — avoids jitter from SLAM noise
            go.transform.position = Vector3.Lerp(
                go.transform.position, targetPos,
                Time.deltaTime * smoothSpeed);

            _lastDet[i] = det;
        }

        // Hide unused pool entries
        for (int i = detections.Count; i < _pool.Count; i++)
            if (_pool.ContainsKey(i)) _pool[i].SetActive(false);
    }

    // ── Fallback: map UV → world pos on the video quad ───────
    Vector3 PixelToWorld(float u, float v, int pixelY)
    {
        if (videoScreen == null) return Vector3.zero;
        var b = videoScreen.bounds;
        float x = Mathf.Lerp(b.min.x, b.max.x, u);
        float topV = (float)pixelY / streamHeight;
        float y = Mathf.Lerp(b.max.y, b.min.y, topV) + b.size.y * 0.03f;
        float z = b.min.z - 0.01f;
        return new Vector3(x, y, z);
    }

    void HideAll()
    {
        foreach (var go in _pool.Values) go.SetActive(false);
    }

    // ── Debug gizmo — shows world origin in Scene view ───────
    void OnDrawGizmos()
    {
        Gizmos.color = _slamTracking ? Color.green : Color.red;
        Gizmos.DrawWireSphere(worldOrigin, 0.1f);
        Gizmos.DrawLine(worldOrigin, worldOrigin + Vector3.up * 0.3f);
    }
}