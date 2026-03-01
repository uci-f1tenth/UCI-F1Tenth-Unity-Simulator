using UnityEngine;

public class IMUSensor : MonoBehaviour
{
    [Header("Hardware Link")]
    public Rigidbody targetRigidbody;

    [Header("Sensor Settings")]
    public bool useGravity = false;
    [Tooltip("Target frequency in Hz (e.g., 40 for 40Hz)")]
    public float updateRate = 40f;

    [Header("Noise Settings (Normalized 0-1)")]
    [Range(0f, 1f)] public float noiseIntensity = 0.05f;
    [Range(0f, 1f)] public float driftIntensity = 0.01f;

    private float nextUpdateTime;
    private Vector3 currentDrift;
    private Vector3 lastLocalVelocity;
    private Vector3 lastLocalAngularVelocity;
    private float lastSampleTime;

    void Start()
    {
        if (targetRigidbody == null)
            targetRigidbody = GetComponentInParent<Rigidbody>();

        lastSampleTime = Time.fixedTime;
    }

    void FixedUpdate()
    {
        if (DataBridge.Instance == null || targetRigidbody == null) return;

        // Only run logic if enough time has passed to match the target Hz
        if (Time.fixedTime >= nextUpdateTime)
        {
            float deltaTime = Time.fixedTime - lastSampleTime;
            // Prevent division by zero on first frame
            if (deltaTime <= 0) deltaTime = Time.fixedDeltaTime;

            UpdateSensorData(deltaTime);

            // Schedule next update based on frequency (1 / Hz)
            if (updateRate > 0f)
            {
                nextUpdateTime = Time.fixedTime + (1f / updateRate);
            }
            lastSampleTime = Time.fixedTime;
        }
    }

    private void UpdateSensorData(float dt)
    {
        // 1. LOCAL ANGLES (Based on Rigidbody)
        Vector3 localAnglesRad = targetRigidbody.transform.localEulerAngles * Mathf.Deg2Rad;

        // 2. LOCAL LINEAR ACCELERATION
        Vector3 localVelocity = transform.InverseTransformDirection(targetRigidbody.linearVelocity);
        Vector3 localLinearAccel = (localVelocity - lastLocalVelocity) / dt;

        if (useGravity)
        {
            Vector3 localGravity = transform.InverseTransformDirection(Physics.gravity);
            localLinearAccel -= localGravity;
        }

        // 3. LOCAL ANGULAR ACCELERATION
        Vector3 localAngularVel = transform.InverseTransformDirection(targetRigidbody.angularVelocity);
        Vector3 localAngularAccel = (localAngularVel - lastLocalAngularVelocity) / dt;

        // 4. APPLY CORRUPTION
        Vector3 finalAngles = ApplySensorEffects(localAnglesRad, 0.02f, dt);
        Vector3 finalLin = ApplySensorEffects(localLinearAccel, 1.5f, dt);
        Vector3 finalAng = ApplySensorEffects(localAngularAccel, 0.5f, dt);

        // 5. UPDATE DATABRIDGE
        DataBridge.Instance.imuAngles = finalAngles;
        DataBridge.Instance.linAccel = finalLin;
        DataBridge.Instance.angAccel = finalAng;

        lastLocalVelocity = localVelocity;
        lastLocalAngularVelocity = localAngularVel;
    }

    private Vector3 ApplySensorEffects(Vector3 cleanData, float scale, float dt)
    {
        if (driftIntensity > 0)
        {
            currentDrift += new Vector3(
                Random.Range(-1f, 1f),
                Random.Range(-1f, 1f),
                Random.Range(-1f, 1f)
            ) * driftIntensity * dt;
        }
        else
        {
            currentDrift = Vector3.zero;
        }

        Vector3 whiteNoise = Vector3.zero;
        if (noiseIntensity > 0)
        {
            whiteNoise = new Vector3(
                Random.Range(-1f, 1f),
                Random.Range(-1f, 1f),
                Random.Range(-1f, 1f)
            ) * noiseIntensity * scale;
        }

        return cleanData + whiteNoise + currentDrift;
    }
}