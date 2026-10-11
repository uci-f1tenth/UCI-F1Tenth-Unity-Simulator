using UnityEngine;

public class MACheckpointTrigger : MonoBehaviour
{
    [HideInInspector] public int checkpointIndex;
    [HideInInspector] public MACheckpointManager manager;

    private void OnTriggerEnter(Collider other)
    {
        if (manager == null) return;

        // 1. Try to find the Agent efficiently
        // Note: Using GetComponentInParent is safer if colliders are on child objects (like wheels/body)
        MAVehicleController agent = other.GetComponentInParent<MAVehicleController>();

        if (agent != null && agent.agentIndex != -1)
        {
            manager.OnAgentHitCheckpoint(checkpointIndex, agent.agentIndex);
        }
    }
}