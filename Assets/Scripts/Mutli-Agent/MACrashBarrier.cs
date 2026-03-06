using UnityEngine;

public class MACrashBarrier : MonoBehaviour
{
    // OPTIMIZATION NOTE:
    // Since you set up the Layer Collision Matrix to only allow "Agents" to hit this layer,
    // we assume ANY collision is a valid agent. No Tag check needed!

    private void OnCollisionEnter(Collision collision)
    {
        // 1. GET COMPONENT (Fastest method)
        // We use GetComponentInParent in case the collider hit a wheel or child part
        // instead of the main body.
        var agent = collision.gameObject.GetComponentInParent<MAVehicleController>();

        // 2. TRIGGER RESET
        if (agent != null)
        {
            // The agent script handles the cooldown and penalty logic internally
            agent.HandleReset();
        }
    }
}