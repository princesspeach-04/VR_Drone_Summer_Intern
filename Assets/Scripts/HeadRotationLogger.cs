using UnityEngine;

public class HeadRotationLogger : MonoBehaviour
{
    void Update()
    {
        Vector3 rot = transform.localRotation.eulerAngles;

        float pitch = rot.x; // up-down
        float yaw = rot.y;   // left-right
        float roll = rot.z;  // tilt

        Debug.Log($"Pitch: {pitch:F2} | Yaw: {yaw:F2} | Roll: {roll:F2}");
    }
}