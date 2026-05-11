using UnityEngine;

public class HeadTracker : MonoBehaviour
{
    public Transform headset;

    void Update()
    {
        Vector3 rot = headset.eulerAngles;

        float yaw = rot.y;
        float pitch = rot.x;

        Debug.Log("Yaw: " + yaw + " Pitch: " + pitch);
    }
}