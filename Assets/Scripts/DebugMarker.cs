using UnityEngine;
using TMPro;

public class DebugMarker : MonoBehaviour
{
    void Update()
    {
        // Press X button on controller to spawn
        // a test marker RIGHT in front of you
        if (OVRInput.GetDown(OVRInput.Button.Two))
        {
            SpawnTestMarker();
        }
    }

    void SpawnTestMarker()
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            Debug.LogError("[Debug] No main camera found");
            return;
        }

        // Place marker 2 metres directly in front of camera
        Vector3 spawnPos = cam.transform.position
                         + cam.transform.forward * 2f;

        Debug.Log($"[Debug] Spawning test marker at {spawnPos}");

        // Create a simple sphere so we KNOW
        // something is visible regardless of prefab issues
        GameObject sphere = GameObject.CreatePrimitive(
            PrimitiveType.Sphere);
        sphere.transform.position = spawnPos;
        sphere.transform.localScale = Vector3.one * 0.2f;

        // Make it bright red so it's impossible to miss
        sphere.GetComponent<Renderer>().material.color
            = Color.red;

        // Also create a world-space text above it
        GameObject textGo = new GameObject("DebugText");
        textGo.transform.position = spawnPos + Vector3.up * 0.3f;

        var canvas = textGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = cam;

        var rect = textGo.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(1f, 0.5f);
        rect.localScale = Vector3.one * 0.005f;

        var textComp = textGo.AddComponent<TextMeshProUGUI>();
        textComp.text = "TEST MARKER\n2.0 m";
        textComp.fontSize = 200;
        textComp.color = Color.white;
        textComp.alignment = TextAlignmentOptions.Center;

        textGo.transform.LookAt(
            textGo.transform.position +
            cam.transform.forward);

        Debug.Log("[Debug] Test marker spawned — " +
                  "you should see a red sphere");

        // Destroy after 10 seconds
        Destroy(sphere, 10f);
        Destroy(textGo, 10f);
    }
}