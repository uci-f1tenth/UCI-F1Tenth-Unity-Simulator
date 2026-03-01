using UnityEngine;

public class IMUSensor : MonoBehaviour
{
    [Header("Hardware Link")]
    public Rigidbody targetRigidbody;

    [Header("Noise Settings (Normalized 0-1)")]
    [Range(0f, 1f)] public float noiseIntensity = 0.05f;
    [Range(0f, 1f)] public float driftIntensity = 0.01f;

    private Vector3 currentDrift;
    private Vector3 lastLocalVelocity;
    private Vector3 lastLocalAngularVelocity;

    void Start()
    {
        if (targetRigidbody == null)
            targetRigidbody = GetComponentInParent<Rigidbody>();
    }

    void FixedUpdate()
    {
        if (DataBridge.Instance == null || targetRigidbody == null) return;

        // --- RESET LOGIC ---
        // If drift or noise is turned off, clear the accumulated bias (calibration)
        if (driftIntensity <= 0 || noiseIntensity <= 0)
        {
            currentDrift = Vector3.zero;
        }

        // 1. LOCAL ANGLES (Radians)
        Vector3 localAnglesRad = transform.localEulerAngles * Mathf.Deg2Rad;

        // 2. LOCAL LINEAR ACCELERATION
        Vector3 localVelocity = transform.InverseTransformDirection(targetRigidbody.linearVelocity);
        Vector3 localLinearAccel = (localVelocity - lastLocalVelocity) / Time.fixedDeltaTime;

        // Simulate Gravity (Normal Force)
        //Vector3 localGravity = transform.InverseTransformDirection(Physics.gravity);
        //localLinearAccel -= localGravity;

        // 3. LOCAL ANGULAR ACCELERATION
        Vector3 localAngularVel = transform.InverseTransformDirection(targetRigidbody.angularVelocity);
        Vector3 localAngularAccel = (localAngularVel - lastLocalAngularVelocity) / Time.fixedDeltaTime;

        // 4. APPLY CORRUPTION
        Vector3 finalAngles = ApplySensorEffects(localAnglesRad, 0.02f);
        Vector3 finalLin = ApplySensorEffects(localLinearAccel, 1.5f);
        Vector3 finalAng = ApplySensorEffects(localAngularAccel, 0.5f);

        // 5. UPDATE DATABRIDGE
        DataBridge.Instance.imuAngles = finalAngles;
        DataBridge.Instance.linAccel = finalLin;
        DataBridge.Instance.angAccel = finalAng;

        lastLocalVelocity = localVelocity;
        lastLocalAngularVelocity = localAngularVel;
    }

    private Vector3 ApplySensorEffects(Vector3 cleanData, float scale)
    {
        // Random Walk Drift (only accumulate if driftIntensity > 0)
        if (driftIntensity > 0)
        {
            currentDrift += new Vector3(
                Random.Range(-1f, 1f),
                Random.Range(-1f, 1f),
                Random.Range(-1f, 1f)
            ) * driftIntensity * Time.fixedDeltaTime;
        }

        // White Noise (only add if noiseIntensity > 0)
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