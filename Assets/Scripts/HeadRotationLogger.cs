using UnityEngine;

public class HeadTracker : MonoBehaviour
{
    [Header("Clamp Limits")]
    public float maxYaw = 90f;
    public float maxPitch = 45f;

    [Header("Smoothing")]
    public float smoothSpeed = 5f;

    [Header("Debug")]
    public bool enableDebugLogs = true;
    public float logInterval = 0.1f; // seconds

    public float currentYaw;
    public float currentPitch;

    private float smoothYaw;
    private float smoothPitch;

    private float logTimer;

    void Update()
    {
        Vector3 raw = transform.localEulerAngles;

        // Convert 0-360 to -180 to +180
        float yaw = NormalizeAngle(raw.y);
        float pitch = NormalizeAngle(raw.x);

        // Clamp to realistic gimbal limits
        yaw = Mathf.Clamp(yaw, -maxYaw, maxYaw);
        pitch = Mathf.Clamp(pitch, -maxPitch, maxPitch);

        // Smooth values
        smoothYaw = Mathf.Lerp(smoothYaw, yaw, Time.deltaTime * smoothSpeed);
        smoothPitch = Mathf.Lerp(smoothPitch, pitch, Time.deltaTime * smoothSpeed);

        // Final usable outputs
        currentYaw = smoothYaw;
        currentPitch = smoothPitch;

        // Controlled debug logging
        if (enableDebugLogs)
        {
            logTimer += Time.deltaTime;

            if (logTimer >= logInterval)
            {
                Debug.Log(
                    $"Yaw: {currentYaw:F1}° | Pitch: {currentPitch:F1}°"
                );

                logTimer = 0f;
            }
        }
    }

    float NormalizeAngle(float angle)
    {
        if (angle > 180f)
            angle -= 360f;

        return angle;
    }
}