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
    public int[] contour;   // flat [x0,y0,x1,y1,...] in image pixel space
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

    [Header("Outline Appearance")]
    [Tooltip("Width of the person outline in world units (metres).")]
    public float outlineWidth = 0.03f;
    public Color outlineColor = new Color(0f, 1f, 0.4f, 1f);  // green

    [Header("Depth Smoothing (Unity-side EMA)")]
    [Tooltip("0 = frozen, 1 = no smoothing. 0.15 recommended for MiDaS.")]
    [Range(0.05f, 1.0f)]
    public float depthAlpha = 0.15f;

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

    // One LineRenderer per detection index for the person outline
    private Dictionary<int, LineRenderer> _outlinePool = new();

    // Unity-side depth EMA per detection key (index-based)
    private Dictionary<int, float> _depthEma = new();

    private bool _passthroughMode = false;
    private bool _lastPollFailed = false;

    // Shared material for all outlines — created once
    private Material _outlineMaterial;

    void Start()
    {
        // Capture the HMD's current orientation as the base (forward = world forward).
        // If you want world-fixed outlines regardless of HMD yaw, set this to
        // Quaternion.identity and dial in yawTrimDegrees manually.
        _baseRotation = Quaternion.identity;
        RebuildRotation();

        // ── Outline material ───────────────────────────────────────────────
        // "UI/Default" is unlit, alpha-blended, and crucially sets ZTest Always
        // so lines render on top of the passthrough layer even when occluded.
        // We bump renderQueue above 4000 (OVR passthrough sits at ~3000-3500).
        _outlineMaterial = new Material(Shader.Find("UI/Default"));
        _outlineMaterial.SetInt("_ZWrite", 0);
        _outlineMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        _outlineMaterial.renderQueue = 4500;

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
        // Euler order: yaw (Y-axis) then pitch (X-axis), applied to base orientation.
        // Negating pitch so "stick up = outline moves up" feels natural.
        _cameraWorldRotation = _baseRotation
            * Quaternion.Euler(-pitchTrimDegrees, yawTrimDegrees, 0f);
    }

    public void SetPassthroughMode(bool passthrough)
    {
        _passthroughMode = passthrough;
        if (!_passthroughMode)
        {
            foreach (var m in _markerPool.Values)
                if (m != null) m.Hide();
            HideAllOutlines();
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
        if (_passthroughMode) UpdateMarkers(detections);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Core projection: image-space pixel → world-space position.
    //
    // Coordinate conventions:
    //   • Image X:  0 = left edge,  streamWidth  = right edge
    //   • Image Y:  0 = top edge,   streamHeight = bottom edge
    //   • NDC X:   -1 = left,  +1 = right  (standard)
    //   • NDC Y:   -1 = bottom,+1 = top    (flipped vs image Y)
    //   • Camera local: +X right, +Y up, +Z forward (Unity convention)
    //
    // We negate ndcX when building localRay because Unity's camera looks down
    // +Z and image pixel X increases left→right, same as world X — so no flip
    // is needed there. BUT the fixed external camera may be mounted so its
    // image-left corresponds to world-right (depends on physical orientation).
    // If your outlines appear mirrored horizontally, negate ndcX below.
    // ─────────────────────────────────────────────────────────────────────────
    Vector3 PixelToWorldPosition(int px, int py, float depth_m)
    {
        // Map pixel → NDC in [-1, +1]
        // px=0 → ndcX=-1 (left), px=W → ndcX=+1 (right)
        // py=0 → ndcY=+1 (top),  py=H → ndcY=-1 (bottom)
        float ndcX = (px / (float)streamWidth) * 2f - 1f;
        float ndcY = -(py / (float)streamHeight) * 2f + 1f;

        // Half-extents of the view frustum at unit depth
        float tanH = Mathf.Tan(cameraHFov * 0.5f * Mathf.Deg2Rad);
        float tanV = tanH * ((float)streamHeight / streamWidth);

        // Local-space ray in camera coords (not yet normalised — depth applies along Z)
        Vector3 localRay = new Vector3(
            ndcX * tanH,
            ndcY * tanV,
            1f              // camera looks down +Z in local space
        );

        // Rotate into world space using the calibrated camera orientation
        Vector3 worldRay = _cameraWorldRotation * localRay;

        // Clamp depth to sane range; use fallback if MiDaS returned garbage
        float depth = (depth_m > minDepth && depth_m < maxDepth)
                      ? depth_m : fallbackDepth;

        // Scale ray so that its Z-component equals `depth` (i.e., depth along
        // the optical axis, not along the ray). This avoids fisheye distortion
        // at wide FOVs where ray length ≠ optical-axis depth.
        // world_point = cam_pos + worldRay * (depth / localRay.magnitude)
        // Since localRay.z == 1, localRay.magnitude == sqrt(ndcX²tanH² + ndcY²tanV² + 1)
        float rayScale = depth / localRay.magnitude;

        return cameraWorldPosition + worldRay.normalized * (depth);
        // Note: if you want perspective-correct depth (depth along optical axis),
        // replace the last line with:
        //   return cameraWorldPosition + worldRay * rayScale;
        // and remove the .normalized. Try both — for wide-FOV cameras the
        // non-normalized version places edge detections more accurately.
    }

    Vector3 DetectionToWorldPosition(Detection det)
    {
        float cx = (det.x1 + det.x2) * 0.5f;
        float cy = (det.y1 + det.y2) * 0.5f;
        return PixelToWorldPosition((int)cx, (int)cy, det.depth_m);
    }

    // Unity-side EMA to smooth rapidly-changing MiDaS depth readings.
    // depthAlpha=0.15 matches the server-side DEPTH_ALPHA for double smoothing.
    float SmoothedDepth(int idx, float rawDepth)
    {
        if (rawDepth <= minDepth || rawDepth >= maxDepth)
            return _depthEma.ContainsKey(idx) ? _depthEma[idx] : fallbackDepth;

        if (!_depthEma.ContainsKey(idx))
        {
            _depthEma[idx] = rawDepth;
            return rawDepth;
        }

        _depthEma[idx] = depthAlpha * rawDepth + (1f - depthAlpha) * _depthEma[idx];
        return _depthEma[idx];
    }

    void UpdateMarkers(List<Detection> detections)
    {
        if (markerPrefab == null) { Debug.LogError("markerPrefab is NULL"); return; }

        HashSet<int> activeIndices = new HashSet<int>();

        for (int i = 0; i < detections.Count; i++)
        {
            Detection det = detections[i];
            activeIndices.Add(i);

            // Apply Unity-side depth smoothing
            float smoothDepth = SmoothedDepth(i, det.depth_m);
            det.depth_m = smoothDepth;   // patch in-place for downstream use

            // ── Label marker ────────────────────────────────────────────
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

            Vector3 worldPos = DetectionToWorldPosition(det);
            _markerPool[i].SetData(det.label.Split(' ')[0], det.depth_m, worldPos);

            // ── Contour outline ──────────────────────────────────────────
            UpdateOutline(i, det);
        }

        // Hide stale entries (detections that disappeared this frame)
        foreach (var kvp in _markerPool)
        {
            if (!activeIndices.Contains(kvp.Key) && kvp.Value != null)
                kvp.Value.Hide();
        }

        foreach (var kvp in _outlinePool)
        {
            if (!activeIndices.Contains(kvp.Key) && kvp.Value != null)
                kvp.Value.positionCount = 0;
        }

        // Clean up EMA entries for gone detections
        List<int> staleEma = new List<int>();
        foreach (var k in _depthEma.Keys)
            if (!activeIndices.Contains(k)) staleEma.Add(k);
        foreach (var k in staleEma) _depthEma.Remove(k);
    }

    void UpdateOutline(int idx, Detection det)
    {
        // ── Get or create LineRenderer ───────────────────────────────────
        if (!_outlinePool.ContainsKey(idx) || _outlinePool[idx] == null)
        {
            var go = new GameObject($"PersonOutline_{idx}");
            go.transform.SetParent(transform);

            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.loop = true;
            lr.startWidth = outlineWidth;
            lr.endWidth = outlineWidth;
            lr.material = _outlineMaterial;
            lr.startColor = outlineColor;
            lr.endColor = outlineColor;
            lr.numCapVertices = 4;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;

            _outlinePool[idx] = lr;
        }

        LineRenderer line = _outlinePool[idx];

        // Ensure the line is enabled (it may have been disabled in a previous frame)
        line.enabled = true;

        // ── No contour yet → draw bounding box as placeholder ───────────
        if (det.contour == null || det.contour.Length < 4)
        {
            DrawBoundingBoxOutline(line, det);
            return;
        }

        int pointCount = det.contour.Length / 2;
        if (pointCount < 2)
        {
            DrawBoundingBoxOutline(line, det);
            return;
        }

        // ── Project every contour point into world space ─────────────────
        // All points share the detection's smoothed depth.  If you later switch
        // to a RealSense pipeline that gives per-pixel depth, pass each point's
        // own depth here instead.
        float depth = (det.depth_m > minDepth && det.depth_m < maxDepth)
                      ? det.depth_m : fallbackDepth;

        line.positionCount = pointCount;

        for (int p = 0; p < pointCount; p++)
        {
            int px = det.contour[p * 2];
            int py = det.contour[p * 2 + 1];
            line.SetPosition(p, PixelToWorldPosition(px, py, depth));
        }
    }

    // Fallback: 4-corner bounding-box rectangle shown while SAM2 warms up
    void DrawBoundingBoxOutline(LineRenderer line, Detection det)
    {
        float depth = (det.depth_m > minDepth && det.depth_m < maxDepth)
                      ? det.depth_m : fallbackDepth;

        line.positionCount = 4;
        line.SetPosition(0, PixelToWorldPosition(det.x1, det.y1, depth));
        line.SetPosition(1, PixelToWorldPosition(det.x2, det.y1, depth));
        line.SetPosition(2, PixelToWorldPosition(det.x2, det.y2, depth));
        line.SetPosition(3, PixelToWorldPosition(det.x1, det.y2, depth));
    }

    void HideAllOutlines()
    {
        foreach (var lr in _outlinePool.Values)
            if (lr != null) lr.positionCount = 0;
    }

    void HideAll()
    {
        foreach (var m in _markerPool.Values)
            if (m != null) m.Hide();
        HideAllOutlines();
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