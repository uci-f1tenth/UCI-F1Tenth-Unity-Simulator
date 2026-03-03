using System.Collections.Generic;
using UnityEngine;

public class CheckpointManager : MonoBehaviour, ITimeStepper
{
    [Header("Track Configuration")]
    [Tooltip("Add checkpoints in order. The last one is the Finish Line.")]
    public List<GameObject> checkpointObjects = new List<GameObject>();

    [Header("Teleport Settings")]
    public float respawnHeightOffset = 0.5f;

    [Header("RL Reward Values")]
    [Tooltip("Reward for passing the correct next checkpoint.")]
    public float checkpointPassReward = 1.0f;
    [Tooltip("Penalty for hitting checkpoints out of order.")]
    public float wrongCheckpointPenalty = -1.0f;
    [Tooltip("Bonus reward for completing a full lap.")]
    public float lapCompletionReward = 5.0f;
    [Tooltip("Additional bonus if the lap was the best time yet.")]
    public float bestLapBonus = 10.0f;

    [Header("Visualizer Settings")]
    public bool showVisualizer = true;
    public Color pathColor = Color.green;
    public Color finishLineColor = Color.red;

    private int nextCheckpointIndex = 0;
    private float lapStartTime;
    private Transform lastPassedCheckpointTransform;
    private bool ignoreNextWrongCheckpoint = false;

    void Awake()
    {
        InitializeCheckpoints();
        lapStartTime = TimeStepManager.fixedTime;

        if (checkpointObjects.Count > 0)
            lastPassedCheckpointTransform = checkpointObjects[0].transform;

        if (DataBridge.Instance != null)
            DataBridge.Instance.bestLapTime = float.MaxValue;
    }

    public void TimeStep()
    {
        if (DataBridge.Instance == null) return;
        DataBridge.Instance.currentLapTime = TimeStepManager.fixedTime - lapStartTime;
    }

    private void InitializeCheckpoints()
    {
        for (int i = 0; i < checkpointObjects.Count; i++)
        {
            if (checkpointObjects[i] == null) continue;

            CheckpointTrigger trigger = checkpointObjects[i].GetComponent<CheckpointTrigger>();
            if (trigger == null)
                trigger = checkpointObjects[i].AddComponent<CheckpointTrigger>();

            trigger.Init(i, this);
        }
    }

    public void OnCheckpointEntered(int index)
    {
        if (DataBridge.Instance == null) return;

        if (index == nextCheckpointIndex)
        {
            Debug.Log($"<color=green>Checkpoint {index} reached!</color>");

            // Use Variable Reward
            TCPTunnel.Instance.AddReward(checkpointPassReward);

            lastPassedCheckpointTransform = checkpointObjects[index].transform;
            nextCheckpointIndex++;
            ignoreNextWrongCheckpoint = false;

            if (nextCheckpointIndex >= checkpointObjects.Count)
            {
                CompleteLap();
                nextCheckpointIndex = 0;
            }
        }
        else if (ignoreNextWrongCheckpoint)
        {
            ignoreNextWrongCheckpoint = false;
        }
        else
        {
            Debug.Log($"<color=red>Wrong Checkpoint! Need {nextCheckpointIndex}, but hit {index}</color>");

            // Use Variable Penalty
            TCPTunnel.Instance.AddReward(wrongCheckpointPenalty);
        }
    }

    private void CompleteLap()
    {
        float lapTime = TimeStepManager.fixedTime - lapStartTime;
        DataBridge.Instance.lapCount++;
        DataBridge.Instance.lastLapTime = lapTime;

        // 1. Give standard lap completion reward
        TCPTunnel.Instance.AddReward(lapCompletionReward);

        // 2. Check for Best Lap
        if (lapTime < DataBridge.Instance.bestLapTime)
        {
            DataBridge.Instance.bestLapTime = lapTime;
            Debug.Log($"<color=#FFD700>New Best Lap! {lapTime:F3}s</color>");

            // Use Variable Best Lap Bonus
            TCPTunnel.Instance.AddReward(bestLapBonus);
        }

        Debug.Log($"<color=#FFD700>Lap {DataBridge.Instance.lapCount} Complete! Time: {lapTime:F3}s</color>");
        lapStartTime = TimeStepManager.fixedTime;
    }

    public void TeleportToLastCheckpoint(Rigidbody targetRb)
    {
        if (targetRb == null || lastPassedCheckpointTransform == null) return;

        ignoreNextWrongCheckpoint = true;

        // Use Unity 6 property names (linearVelocity/angularVelocity)
        targetRb.linearVelocity = Vector3.zero;
        targetRb.angularVelocity = Vector3.zero;

        targetRb.position = lastPassedCheckpointTransform.position + (Vector3.up * respawnHeightOffset);
        targetRb.rotation = lastPassedCheckpointTransform.rotation;

        Debug.Log($"<color=cyan>Teleported {targetRb.name}. Muffling next 'Wrong Checkpoint' log.</color>");
    }

    // --- EDITOR VISUALIZATION ---
    private void OnDrawGizmos()
    {
        if (!showVisualizer || checkpointObjects == null || checkpointObjects.Count < 2) return;

        for (int i = 0; i < checkpointObjects.Count; i++)
        {
            if (checkpointObjects[i] == null) continue;

            if (i < checkpointObjects.Count - 1)
            {
                if (checkpointObjects[i + 1] == null) continue;
                Gizmos.color = pathColor;
                Gizmos.DrawLine(checkpointObjects[i].transform.position, checkpointObjects[i + 1].transform.position);
            }
            else
            {
                if (checkpointObjects[0] == null) continue;
                Gizmos.color = finishLineColor;
                Gizmos.DrawLine(checkpointObjects[i].transform.position, checkpointObjects[0].transform.position);
            }

            Gizmos.DrawWireSphere(checkpointObjects[i].transform.position, 0.1f);
        }
    }
}