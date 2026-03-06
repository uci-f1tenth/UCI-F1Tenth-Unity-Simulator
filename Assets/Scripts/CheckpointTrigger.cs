using UnityEngine;

public class CheckpointTrigger : MonoBehaviour
{
    private int index;
    private CheckpointManager manager;

    public void Init(int i, CheckpointManager m)
    {
        index = i;
        manager = m;
    }

    private void OnTriggerEnter(Collider other)
    {
        // Detects the player by tag. Adjust "Player" to match your vehicle's tag.
        if (other.CompareTag("Agent Tag"))
        {
            manager.OnCheckpointEntered(index);
        }
    }
}