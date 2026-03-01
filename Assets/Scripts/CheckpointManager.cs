using System.Collections.Generic;
using UnityEngine;

public class CheckpointManager : MonoBehaviour
{
    [Header("Track Configuration")]
    [Tooltip("Add checkpoints in order. The last one is the Finish Line.")]
    public List<GameObject> checkpointObjects = new List<GameObject>();

    [Header("Teleport Settings")]
    public float respawnHeightOffset = 0.5f;

    private int nextCheckpointIndex = 0;
    private float lapStartTime;
    private Transform lastPassedCheckpointTransform;

    void Start()
    {
        InitializeCheckpoints();
        lapStartTime = Time.time;

        // Default respawn point is the first checkpoint (Start Line)
        if (checkpointObjects.Count > 0)
            lastPassedCheckpointTransform = checkpointObjects[0].transform;

        if (DataBridge.Instance != null)
            DataBridge.Instance.bestLapTime = float.MaxValue;
    }

    void Update()
    {
        if (DataBridge.Instance == null) return;

        // Update the live timer in the DataBridge
        DataBridge.Instance.currentLapTime = Time.time - lapStartTime;
    }

    private void InitializeCheckpoints()
    {
        for (int i = 0; i < checkpointObjects.Count; i++)
        {
            // Auto-setup the trigger scripts on the box colliders
            CheckpointTrigger trigger = checkpointObjects[i].GetComponent<CheckpointTrigger>();
            if (trigger == null)
                trigger = checkpointObjects[i].AddComponent<CheckpointTrigger>();

            trigger.Init(i, this);
        }
    }

    public void OnCheckpointEntered(int index)
    {
        if (DataBridge.Instance == null) return;

        // Only progress if hitting the CORRECT next checkpoint
        if (index == nextCheckpointIndex)
        {
            lastPassedCheckpointTransform = checkpointObjects[index].transform;
            nextCheckpointIndex++;

            // Lap is complete (crossed the last checkpoint in the list)
            if (nextCheckpointIndex >= checkpointObjects.Count)
            {
                CompleteLap();
                nextCheckpointIndex = 0;
            }
        }
    }

    private void CompleteLap()
    {
        float lapTime = Time.time - lapStartTime;

        DataBridge.Instance.lapCount++;
        DataBridge.Instance.lastLapTime = lapTime;

        if (lapTime < DataBridge.Instance.bestLapTime)
        {
            DataBridge.Instance.bestLapTime = lapTime;
        }

        lapStartTime = Time.time;
    }

    /// <summary>
    /// Teleports a specific Rigidbody to the last successfully passed checkpoint.
    /// </summary>
    /// <param name="targetRb">The Rigidbody of the vehicle to teleport.</param>
    public void TeleportToLastCheckpoint(Rigidbody targetRb)
    {
        if (targetRb == null || lastPassedCheckpointTransform == null)
        {
            Debug.LogWarning("Teleport failed: Rigidbody or Checkpoint Transform is null.");
            return;
        }

        // 1. Kill all momentum to prevent "flying" after spawn
        targetRb.linearVelocity = Vector3.zero;
        targetRb.angularVelocity = Vector3.zero;

        // 2. Set Position with a small vertical offset to avoid floor clipping
        targetRb.position = lastPassedCheckpointTransform.position + (Vector3.up * respawnHeightOffset);

        // 3. Match the rotation of the checkpoint (Ensure checkpoint Z-axis faces 'Forward')
        targetRb.rotation = lastPassedCheckpointTransform.rotation;

        Debug.Log($"Teleported {targetRb.name} to {lastPassedCheckpointTransform.name}");
    }
}