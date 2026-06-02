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

    [Tooltip("Where YOU (the headset) stand in the room, " +
             "facing the drone's room.\n" +
             "Set X=0 Y=0 Z=0 if you stand at the Unity origin.")]
    public Vector3 viewerWorldPosition = Vector3.zero;

    [Tooltip("The compass direction (Y rotation in degrees) " +
             "that the drone camera faces in YOUR world.\n" +
             "0 = Unity +Z (forward), 90 = Unity +X (right), etc.")]
    public float droneFacingYaw = 0f;

    [Tooltip("Height in metres at which markers float " +
             "(world Y, e.g. 1.6 = eye level).")]
    public float markerWorldY = 1.6f;

    [Tooltip("Vertical gap between stacked markers " +
             "when multiple people are detected.")]
    public float markerStackOffset = 0.35f;

    // ── internals ──────────────────────────────────────────────
    private Dictionary<int, GameObject> _labelPool = new Dictionary<int, GameObject>();
    private Dictionary<int, ThroughWallMarker> _markerPool = new Dictionary<int, ThroughWallMarker>();

    private bool _passthroughMode = false;
    private bool _lastPollFailed = false;
    private Camera _cam;

    void Start()
    {
        _cam = Camera.main;
        _passthroughMode = true;
        Debug.Log($"[DetectionOverlay] Start — markerPrefab=" +
                  $"{(markerPrefab == null ? "NULL" : "OK")}");
        StartCoroutine(PollLoop());
    }

    // ── Called by ModeManager ──────────────────────────────────
    public void SetPassthroughMode(bool passthrough)
    {
        _passthroughMode = passthrough;
        Debug.Log("[DetectionOverlay] Mode → " +
                  (passthrough ? "PASSTHROUGH" : "CAMERA FEED"));

        if (_passthroughMode)
        {
            // Entering passthrough: hide 2-D labels
            foreach (var go in _labelPool.Values)
                if (go != null) go.SetActive(false);
        }
        else
        {
            // Entering camera feed: hide ALL 3-D markers
            foreach (var m in _markerPool.Values)
                if (m != null) m.Hide();
        }
    }

    // ── Poll /detections ───────────────────────────────────────
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
                    Debug.Log($"[DetectionOverlay] {data.detections.Count}" +
                              $" detections passthrough={_passthroughMode}");
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

    // ── Convert drone-camera pixel → world position ────────────
    //
    // The drone sees the room from a fixed position/angle.
    // We treat the drone camera like a virtual camera:
    //   • droneFacingYaw  = which direction it points in your world
    //   • depth_m         = distance from drone to person
    //   • pixel X         = left/right angle within the drone's FOV
    //
    // Result is a flat world-space position (Y is always markerWorldY).
    //
    Vector3 DetectionToWorldPosition(Detection det, int slotIndex)
    {
        // Normalise pixel centre to -0.5 .. +0.5
        float cx = (det.x1 + det.x2) * 0.5f;
        float nx = (cx / streamWidth) - 0.5f;

        // Horizontal angle offset from drone centre
        float tanH = Mathf.Tan(cameraHFov * 0.5f * Mathf.Deg2Rad);
        float angleOffset = Mathf.Atan(nx * 2f * tanH) * Mathf.Rad2Deg;

        Debug.Log(
            $"[WORLDMAP_TEST] " +
            $"x1={det.x1} " +
            $"x2={det.x2} " +
            $"cx={cx:F0} " +
            $"angle={angleOffset:F1} " +
            $"depth={det.depth_m:F2}"
        );

        // Final bearing = drone's facing yaw + left/right offset
        float bearing = droneFacingYaw + angleOffset;

        // Flat direction in world space
        Quaternion rot = Quaternion.Euler(0f, bearing, 0f);
        Vector3 dir = rot * Vector3.forward;

        float dist = (det.depth_m > 0.1f && det.depth_m < 20f)
                     ? det.depth_m : 2.0f;

        // Stack multiple markers vertically so they don't overlap
        float y = markerWorldY + slotIndex * markerStackOffset;

        return new Vector3(
            viewerWorldPosition.x + dir.x * dist,
            y,
            viewerWorldPosition.z + dir.z * dist
        );
    }

    // ── PASSTHROUGH: 3-D world-space markers ──────────────────
    void UpdateMarkers(List<Detection> detections)
    {
        if (markerPrefab == null)
        {
            Debug.LogError("[DetectionOverlay] markerPrefab is NULL!");
            return;
        }

        for (int i = 0; i < detections.Count; i++)
        {
            if (!_markerPool.ContainsKey(i) || _markerPool[i] == null)
            {
                var go = Instantiate(markerPrefab, transform);
                _markerPool[i] = go.GetComponent<ThroughWallMarker>();
                if (_markerPool[i] == null)
                {
                    Debug.LogError("[DetectionOverlay] ThroughWallMarker missing!");
                    Destroy(go);
                    continue;
                }
            }

            var det = detections[i];
            Vector3 pos = DetectionToWorldPosition(det, i);

            string className = det.label.Split(' ')[0];
            Debug.Log($"[DetectionOverlay] Marker {i} {className}" +
                      $" depth={det.depth_m:F2}m pos={pos}");

            _markerPool[i].SetData(className, det.depth_m, pos);
        }

        // Hide unused slots
        for (int i = detections.Count; i < _markerPool.Count; i++)
            if (_markerPool.ContainsKey(i) && _markerPool[i] != null)
                _markerPool[i].Hide();
    }

    // ── CAMERA FEED: 2-D labels on video quad ─────────────────
    // (Python already draws bounding boxes on the stream itself;
    //  these labels just float above each box on the video quad)
    void UpdateLabels(List<Detection> detections)
    {

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
        foreach (var go in _labelPool.Values)
            if (go != null) go.SetActive(false);
        foreach (var m in _markerPool.Values)
            if (m != null) m.Hide();
    }

    void OnDrawGizmos()
    {
        // Viewer origin
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(viewerWorldPosition, 0.15f);

        // Drone facing direction arrow
        Gizmos.color = Color.yellow;
        Quaternion rot = Quaternion.Euler(0f, droneFacingYaw, 0f);
        Vector3 fwd = rot * Vector3.forward;
        Gizmos.DrawLine(viewerWorldPosition,
                        viewerWorldPosition + fwd * 1.5f);
    }
}