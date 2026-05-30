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
    public float pollInterval = 0.15f;   // seconds between polls

    [Header("Stream resolution (must match Python)")]
    public int streamWidth = 512;
    public int streamHeight = 288;

    [Header("Scene refs")]
    public Renderer videoScreen;    // the same quad your MJPEG stream renders on
    public GameObject labelPrefab;  // the TMP prefab you made above

    // internals
    private Dictionary<int, GameObject> _pool = new();
    private int _activeCount = 0;

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
        _activeCount = detections.Count;

        for (int i = 0; i < detections.Count; i++)
        {
            if (!_pool.ContainsKey(i))
                _pool[i] = Instantiate(labelPrefab, transform);

            var go = _pool[i];
            var det = detections[i];

            go.SetActive(true);
            go.GetComponentInChildren<TextMeshPro>().text = det.label;

            // Convert pixel bbox centre to world position on the video quad
            float cx = (det.x1 + det.x2) * 0.5f / streamWidth;   // 0..1
            float cy = (det.y1 + det.y2) * 0.5f / streamHeight;   // 0..1

            go.transform.position = PixelToWorld(cx, cy, det.y1);
        }

        // Hide unused pool entries
        for (int i = detections.Count; i < _pool.Count; i++)
            if (_pool.ContainsKey(i)) _pool[i].SetActive(false);
    }

    // ── Map normalised UV to a world position on the video quad ──
    Vector3 PixelToWorld(float u, float v, int pixelY)
    {
        if (videoScreen == null) return Vector3.zero;

        var b = videoScreen.bounds;

        // u=0 → left edge, u=1 → right edge
        // v=0 → top of image, v=1 → bottom  (flip Y)
        float x = Mathf.Lerp(b.min.x, b.max.x, u);
        float y = Mathf.Lerp(b.max.y, b.min.y, v);   // flipped
        float z = b.min.z - 0.01f;                    // just in front of screen

        // Stack labels above their box, not at centre
        float topV = (float)pixelY / streamHeight;
        float yTop = Mathf.Lerp(b.max.y, b.min.y, topV);
        y = yTop + (b.size.y * 0.03f);               // small offset above box

        return new Vector3(x, y, z);
    }

    void HideAll()
    {
        foreach (var go in _pool.Values) go.SetActive(false);
    }
}