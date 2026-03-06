using UnityEngine;

public class MAWheelControl : MonoBehaviour
{
    [Header("--- Configuration ---")]
    public Transform wheelModel;
    public bool steerable;
    public bool motorized;

    // Cache the collider once
    [HideInInspector] public WheelCollider WheelCollider;

    // Cache transform to avoid .transform access overhead
    private Transform _modelTransform;

    public void Initialize()
    {
        WheelCollider = GetComponent<WheelCollider>();
        if (wheelModel != null) _modelTransform = wheelModel.transform;
    }

    /// <summary>
    /// Manually called by the VehicleController. 
    /// Only call this when you actually need to SEE the wheels move.
    /// </summary>
    public void SyncVisuals()
    {
        if (_modelTransform == null || WheelCollider == null) return;

        Vector3 pos;
        Quaternion rot;

        // The expensive physics query
        WheelCollider.GetWorldPose(out pos, out rot);

        _modelTransform.position = pos;
        _modelTransform.rotation = rot;
    }
}