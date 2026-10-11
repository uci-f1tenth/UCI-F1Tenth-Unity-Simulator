using UnityEngine;
using System.Collections.Generic;

public class MAAgentManager : MonoBehaviour
{
    public static MAAgentManager Instance { get; private set; }

    [Header("--- Configuration ---")]
    public GameObject agentPrefab;
    public Transform agentContainer;

    [Header("--- Spawning ---")]
    [Tooltip("Agents will be distributed evenly across these points (stacked on top of each other).")]
    public List<Transform> spawnPoints = new List<Transform>();

    [Header("--- Runtime ---")]
    public List<MAVehicleController> activeAgents = new List<MAVehicleController>();

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        if (agentContainer == null)
        {
            GameObject container = new GameObject("--- Agents Container ---");
            agentContainer = container.transform;
        }
    }

    private void Start()
    {
        // Wait for DataHub to be ready
        if (MADataHub.Instance == null) return;

        SpawnAgents();
    }

    public void SpawnAgents()
    {
        ClearExistingAgents();

        int agentCount = MADataHub.Instance.numberOfAgents;

        if (spawnPoints.Count == 0)
        {
            Debug.LogWarning("[MAAgentManager] No spawn points! Spawning at (0,0,0).");
        }

        for (int i = 0; i < agentCount; i++)
        {
            // 1. Determine Position (Stacked)
            Vector3 pos = Vector3.zero;
            Quaternion rot = Quaternion.identity;

            if (spawnPoints.Count > 0)
            {
                // Simple Round-Robin: Agent 0->Pt0, Agent 1->Pt1...
                // Agent 20->Pt0 (Stacked directly inside Agent 0)
                Transform pt = spawnPoints[i % spawnPoints.Count];
                pos = pt.position;
                rot = pt.rotation;
            }

            // 2. Instantiate
            GameObject newAgentObj = Instantiate(agentPrefab, pos, rot, agentContainer);
            newAgentObj.name = $"Agent_{i:00}";

            // 3. Setup Controller
            MAVehicleController controller = newAgentObj.GetComponent<MAVehicleController>();
            if (controller != null)
            {
                controller.agentIndex = i;
                activeAgents.Add(controller);
            }
        }

        MATimeStepManager.Instance.RefreshStepperList();
        Debug.Log($"<color=green>[MAAgentManager] Spawned {activeAgents.Count} ghost agents.</color>");
    }

    private void ClearExistingAgents()
    {
        foreach (var agent in activeAgents)
        {
            if (agent != null) Destroy(agent.gameObject);
        }
        activeAgents.Clear();

        if (agentContainer.childCount > 0)
        {
            foreach (Transform child in agentContainer) Destroy(child.gameObject);
        }
    }
}