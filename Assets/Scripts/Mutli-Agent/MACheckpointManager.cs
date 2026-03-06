using System.Collections.Generic;
using UnityEngine;

public class MACheckpointManager : MonoBehaviour, ITimeStepper
{
    public static MACheckpointManager Instance { get; private set; }

    // Static References
    private MATimeStepManager timeStepManager;

    [Header("--- Track Configuration ---")]
    [Tooltip("Drag Checkpoint GameObjects here in order. Index 0 is Start/Finish.")]
    public List<GameObject> checkpointObjects = new List<GameObject>();

    [Header("--- Reset Settings ---")]
    public float respawnHeightOffset = -0.1f;
    [Tooltip("If true, crashing sends agent to Start Line. If false, sends to last passed checkpoint.")]
    public bool hardResetToStart = false;

    [Header("--- RL Rewards ---")]
    public float checkpointPassReward = 1.0f;
    public float wrongCheckpointPenalty = -1.0f;
    public float lapCompletionReward = 5.0f;
    public float bestLapBonus = 10.0f;

    [Header("--- Visualization ---")]
    public bool showVisualizer = true;
    public Color pathColor = Color.green;
    public Color finishLineColor = Color.red;

    // --- Multi-Agent State Arrays ---
    // Index matches Agent Index
    private int[] _nextCheckpointIndices;
    private int[] _lastPassedCheckpointIndices;
    private float[] _lapStartTimes;
    private bool[] _ignoreNextWrongCheckpoint; // Used after a reset to prevent double penalty

    // Cached References
    private MADataHub _dataHub;
    private MATCPTunnel _tcpTunnel;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        timeStepManager = MATimeStepManager.Instance;
    }

    private void Start()
    {
        _dataHub = MADataHub.Instance;
        _tcpTunnel = MATCPTunnel.Instance;

        InitializeCheckpoints();
        InitializeAgentStates();
    }

    private void InitializeAgentStates()
    {
        int count = _dataHub != null ? _dataHub.numberOfAgents : 1;

        _nextCheckpointIndices = new int[count];
        _lastPassedCheckpointIndices = new int[count];
        _lapStartTimes = new float[count];
        _ignoreNextWrongCheckpoint = new bool[count];

        float startTime = timeStepManager.fixedTime;

        for (int i = 0; i < count; i++)
        {
            _nextCheckpointIndices[i] = 1; // Assuming they start at Index 0
            _lastPassedCheckpointIndices[i] = 0;
            _lapStartTimes[i] = startTime;
            _ignoreNextWrongCheckpoint[i] = false;

            // Initialize Best Lap to Infinity if not already
            if (_dataHub != null && _dataHub.bestLapTime.Length > i)
            {
                _dataHub.bestLapTime[i] = float.MaxValue;
            }
        }
    }

    private void InitializeCheckpoints()
    {
        for (int i = 0; i < checkpointObjects.Count; i++)
        {
            if (checkpointObjects[i] == null) continue;

            // Ensure collider is a trigger
            Collider col = checkpointObjects[i].GetComponent<Collider>();
            if (col != null) col.isTrigger = true;

            // Add or Init the lightweight trigger script
            MACheckpointTrigger trigger = checkpointObjects[i].GetComponent<MACheckpointTrigger>();
            if (trigger == null) trigger = checkpointObjects[i].AddComponent<MACheckpointTrigger>();

            trigger.checkpointIndex = i;
            trigger.manager = this;
        }
    }

    // =================================================================================================
    //                                      CORE LOGIC
    // =================================================================================================

    public void TimeStep()
    {
        if (_dataHub == null) return;

        float now = timeStepManager.fixedTime;

        // Update current lap time for all agents
        for (int i = 0; i < _dataHub.numberOfAgents; i++)
        {
            if (i < _lapStartTimes.Length)
            {
                _dataHub.currentLapTime[i] = now - _lapStartTimes[i];
            }
        }
    }

    /// <summary>
    /// Called by the MACheckpointTrigger script when an agent enters.
    /// </summary>
    public void OnAgentHitCheckpoint(int checkpointId, int agentId)
    {
        // Safety checks
        if (agentId < 0 || agentId >= _nextCheckpointIndices.Length) return;

        // 1. Correct Checkpoint Reached
        if (checkpointId == _nextCheckpointIndices[agentId])
        {
            // LOG: Valid Progress
            Debug.Log($"<color=green>[Agent {agentId}] Passed Checkpoint {checkpointId}</color>");

            // Valid progress updates
            _lastPassedCheckpointIndices[agentId] = checkpointId;
            _nextCheckpointIndices[agentId]++;
            _ignoreNextWrongCheckpoint[agentId] = false;

            // Reward
            if (_tcpTunnel) _tcpTunnel.AddReward(agentId, checkpointPassReward);

            // Check for Wrap-around (End of Array -> 0)
            if (_nextCheckpointIndices[agentId] >= checkpointObjects.Count)
            {
                _nextCheckpointIndices[agentId] = 0; // Wrap around to index 0
            }

            // Lap Complete Logic (Hitting Index 0)
            // Check time > 1.0f to prevent immediate trigger at spawn
            if (checkpointId == 0 && _dataHub.currentLapTime[agentId] > 1.0f)
            {
                // LOG: Lap Complete
                Debug.Log($"<color=cyan>[Agent {agentId}] LAP COMPLETE! Time: {_dataHub.currentLapTime[agentId]:F2}s</color>");

                CompleteLap(agentId);
                _nextCheckpointIndices[agentId] = 1; // Next target is checkpoint 1
            }
        }
        // 2. Wrong Checkpoint (Backwards or Skipped)
        else
        {
            // Scenario: We just reset/spawned and are inside a trigger we shouldn't be
            if (_ignoreNextWrongCheckpoint[agentId])
            {
                Debug.Log($"<color=grey>[Agent {agentId}] Ignored penalty at Checkpoint {checkpointId} (Just Reset)</color>");
                _ignoreNextWrongCheckpoint[agentId] = false;
                return;
            }

            // Scenario: Jittering (Hitting the one we just passed again) -> Ignore log/penalty
            if (checkpointId == _lastPassedCheckpointIndices[agentId])
            {
                return;
            }

            // Scenario: Actual Wrong Way / Skipped
            Debug.Log($"<color=orange>[Agent {agentId}] WRONG CHECKPOINT! Hit {checkpointId}, Expected {_nextCheckpointIndices[agentId]}</color>");

            if (_tcpTunnel) _tcpTunnel.AddReward(agentId, wrongCheckpointPenalty);
        }
    }

    private void CompleteLap(int agentId)
    {
        float now = timeStepManager.fixedTime;
        float lapTime = now - _lapStartTimes[agentId];

        _dataHub.lapCount[agentId]++;
        _dataHub.lastLapTime[agentId] = lapTime;
        _lapStartTimes[agentId] = now; // Reset timer

        float reward = lapCompletionReward;

        // Best Lap Check
        if (lapTime < _dataHub.bestLapTime[agentId])
        {
            _dataHub.bestLapTime[agentId] = lapTime;
            reward += bestLapBonus;
            Debug.Log($"<color=#FFD700>[Agent {agentId}] New Best Lap: {lapTime:F3}s</color>");
        }
        else
        {
            Debug.Log($"[Agent {agentId}] Lap Complete: {lapTime:F3}s");
        }

        if (_tcpTunnel) _tcpTunnel.AddReward(agentId, reward);
    }

    // =================================================================================================
    //                                      RESET / TELEPORT
    // =================================================================================================

    public void TeleportAgent(int agentIndex, Rigidbody agentRb)
    {
        if (agentIndex < 0 || agentIndex >= _lastPassedCheckpointIndices.Length) return;

        // Determine where to respawn
        int targetCpIndex = hardResetToStart ? 0 : _lastPassedCheckpointIndices[agentIndex];

        Transform targetCp = checkpointObjects[targetCpIndex].transform;

        // Physics Reset
        agentRb.linearVelocity = Vector3.zero;
        agentRb.angularVelocity = Vector3.zero;
        agentRb.Sleep(); // Force sleep to kill any internal physics momentum

        // Position Reset
        agentRb.position = targetCp.position + (Vector3.up * respawnHeightOffset);
        agentRb.rotation = targetCp.rotation;

        // Logic Reset
        if (hardResetToStart)
        {
            _lastPassedCheckpointIndices[agentIndex] = 0;
            _nextCheckpointIndices[agentIndex] = 1;
            _lapStartTimes[agentIndex] = timeStepManager.fixedTime; // Reset lap timer on crash? Optional.
        }

        // Prevent immediate "Wrong Checkpoint" penalty if they spawn inside a trigger
        _ignoreNextWrongCheckpoint[agentIndex] = true;

        agentRb.WakeUp();
    }

    // =================================================================================================
    //                                      VISUALIZER
    // =================================================================================================
    private void OnDrawGizmos()
    {
        if (!showVisualizer || checkpointObjects == null || checkpointObjects.Count < 2) return;

        for (int i = 0; i < checkpointObjects.Count; i++)
        {
            if (checkpointObjects[i] == null) continue;

            Vector3 current = checkpointObjects[i].transform.position;

            // Draw Line to next
            if (i < checkpointObjects.Count - 1 && checkpointObjects[i + 1] != null)
            {
                Gizmos.color = pathColor;
                Gizmos.DrawLine(current, checkpointObjects[i + 1].transform.position);
            }
            // Draw Line to start (Loop)
            else if (i == checkpointObjects.Count - 1 && checkpointObjects[0] != null)
            {
                Gizmos.color = finishLineColor;
                Gizmos.DrawLine(current, checkpointObjects[0].transform.position);
            }

            Gizmos.color = (i == 0) ? finishLineColor : pathColor;
            Gizmos.DrawWireSphere(current, 0.1f);
        }
    }
}