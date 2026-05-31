using UnityEngine;
using TMPro;

public class WireframeBox : MonoBehaviour
{
    [Header("Visual")]
    public Color boxColor = Color.green;
    public float lineWidth = 0.005f;

    [Header("References")]
    public TextMeshPro label;

    private LineRenderer[] _lines;

    // 12 edges of a box, each defined by 2 corner indices
    // Corners: 0=FTL 1=FTR 2=FBR 3=FBL 4=BTL 5=BTR 6=BBR 7=BBL
    // F=front B=back T=top B=bottom L=left R=right
    private static readonly int[,] Edges = new int[12, 2]
    {
        // Front face
        {0,1},{1,2},{2,3},{3,0},
        // Back face
        {4,5},{5,6},{6,7},{7,4},
        // Connecting edges
        {0,4},{1,5},{2,6},{3,7}
    };

    void Awake()
    {
        _lines = new LineRenderer[12];

        for (int i = 0; i < 12; i++)
        {
            GameObject lineObj = new GameObject("Edge_" + i);
            lineObj.transform.SetParent(transform, false);

            LineRenderer lr = lineObj.AddComponent<LineRenderer>();
            lr.positionCount = 2;
            lr.startWidth = lineWidth;
            lr.endWidth = lineWidth;
            lr.useWorldSpace = true;
            lr.material = new Material(
                Shader.Find("Sprites/Default")
            );
            lr.startColor = boxColor;
            lr.endColor = boxColor;
            lr.shadowCastingMode =
                UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;

            _lines[i] = lr;
        }
    }

    /// <summary>
    /// Call this to update the box size/position and label text.
    /// center      = world-space center of the box
    /// size        = world-space extents (width, height, depth)
    /// labelText   = string shown above the box
    /// </summary>
    public void UpdateBox(
        Vector3 center,
        Vector3 size,
        string labelText)
    {
        transform.position = center;

        Vector3 h = size * 0.5f;

        // 8 corners relative to center
        Vector3[] corners = new Vector3[8]
        {
            center + new Vector3(-h.x,  h.y, -h.z), // 0 FTL
            center + new Vector3( h.x,  h.y, -h.z), // 1 FTR
            center + new Vector3( h.x, -h.y, -h.z), // 2 FBR
            center + new Vector3(-h.x, -h.y, -h.z), // 3 FBL
            center + new Vector3(-h.x,  h.y,  h.z), // 4 BTL
            center + new Vector3( h.x,  h.y,  h.z), // 5 BTR
            center + new Vector3( h.x, -h.y,  h.z), // 6 BBR
            center + new Vector3(-h.x, -h.y,  h.z)  // 7 BBL
        };

        for (int i = 0; i < 12; i++)
        {
            _lines[i].SetPosition(0, corners[Edges[i, 0]]);
            _lines[i].SetPosition(1, corners[Edges[i, 1]]);
        }

        // Float label above top of box
        if (label != null)
        {
            label.transform.position =
                center + Vector3.up * (h.y + 0.08f);
            label.text = labelText;

            // Always face the camera
            label.transform.LookAt(
                label.transform.position +
                Camera.main.transform.rotation *
                Vector3.forward,
                Camera.main.transform.rotation *
                Vector3.up
            );
        }
    }

    public void SetVisible(bool visible)
    {
        foreach (var lr in _lines)
            if (lr != null) lr.enabled = visible;

        if (label != null)
            label.gameObject.SetActive(visible);
    }

    public void SetColor(Color c)
    {
        boxColor = c;
        foreach (var lr in _lines)
        {
            if (lr != null)
            {
                lr.startColor = c;
                lr.endColor = c;
            }
        }
    }
}