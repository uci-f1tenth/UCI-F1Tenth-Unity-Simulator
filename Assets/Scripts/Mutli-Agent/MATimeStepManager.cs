using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

public class MATimeStepManager : MonoBehaviour
{
    public static MATimeStepManager Instance { get; private set; }

    [Header("--- Virtual Time API ---")]
    [Tooltip("Current simulation time (starts at 0). Replaces Time.fixedTime.")]
    public float fixedTime;

    [Tooltip("Current simulation step size. Replaces Time.fixedDeltaTime.")]
    public float fixedDeltaTime;

    public enum ControlMode { Automatic, Manual }

    [Header("--- Simulation Settings ---")]
    [Tooltip("Automatic: Unity runs physics (Real-time).\nManual: Python/Script triggers physics (Training).")]
    public ControlMode mode = ControlMode.Automatic;

    [Tooltip("The time step used when in Manual mode. Initialized from Project Settings.")]
    public float fixedStepSize;

    [Header("--- UI References ---")]
    public TMP_InputField timestepInput;

    // Internal State
    private List<ITimeStepper> steppers = new List<ITimeStepper>();
    private Coroutine heartbeatCoroutine;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;

        // Initialize defaults
        fixedDeltaTime = Time.fixedDeltaTime;
        fixedTime = 0f;

        // 1. Capture the Project's native physics step (usually 0.02)
        if (fixedStepSize <= 0)
        {
            fixedStepSize = Time.fixedDeltaTime;
            if (timestepInput != null) timestepInput.text = fixedStepSize.ToString();
        }

        // 2. Find all objects that need stepping (Hub, Agents, etc.)
        RefreshStepperList();

        // 3. Initialize the Physics Engine mode
        UpdatePhysicsMode();
    }

    private void Start()
    {
        // 1. Initialize the InputField text to match current settings
        if (timestepInput != null)
        {
            timestepInput.text = fixedStepSize.ToString("F4");

            // 2. Add the listener via code (safer than manual Inspector linking)
            timestepInput.onEndEdit.AddListener(OnTimestepEntryEnd);
        }

        // Start the heartbeat loop
        if (heartbeatCoroutine != null) StopCoroutine(heartbeatCoroutine);
        heartbeatCoroutine = StartCoroutine(TelemetryHeartbeat());
    }

    /// <summary>
    /// Triggered only when the user finishes editing the UI field.
    /// </summary>
    public void OnTimestepEntryEnd(string input)
    {
        if (float.TryParse(input, out float val))
        {
            // Clamp to a tiny positive value to prevent physics crashes (dt cannot be 0)
            fixedStepSize = Mathf.Max(val, 0.0001f);

            // If we are in Automatic mode, we actually need to update Unity's engine
            if (mode == ControlMode.Automatic)
            {
                Time.fixedDeltaTime = fixedStepSize;
            }

            Debug.Log($"<color=cyan>TimeStepManager: Step size updated to {fixedStepSize}s</color>");
        }

        // Refresh the text in case it was clamped or invalid
        timestepInput.text = fixedStepSize.ToString("F4");
    }

    // Called whenever the UI Checkbox is clicked
    public void OnToggleChanged(bool isAutomatic)
    {
        mode = isAutomatic ? ControlMode.Automatic : ControlMode.Manual;
        UpdatePhysicsMode();
    }

    // Called automatically when you change 'mode' in the Inspector
    private void OnValidate()
    {
        if (Application.isPlaying)
        {
            UpdatePhysicsMode();
        }
    }

    // ------------------------------------------------------------------
    // Core Logic
    // ------------------------------------------------------------------

    private void UpdatePhysicsMode()
    {
        // Switch Unity's internal engine state
        if (mode == ControlMode.Automatic)
        {
            Physics.simulationMode = SimulationMode.FixedUpdate;
            Debug.Log($"<color=green>[TimeStepManager] Mode: AUTOMATIC (Real-time {Time.fixedDeltaTime * 1000}ms)</color>");
        }
        else
        {
            Physics.simulationMode = SimulationMode.Script;
            Debug.Log($"<color=yellow>[TimeStepManager] Mode: MANUAL (Waiting for commands...)</color>");
        }
    }

    public void RefreshStepperList()
    {
        // Expensive operation, only call on Awake or Reset
        // Finds MADataHub and any other script implementing ITimeStepper
        steppers = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)
            .OfType<ITimeStepper>()
            .ToList();

        Debug.Log($"[TimeStepManager] Registered {steppers.Count} steppers.");
    }

    // Unity's Internal Loop (Runs only if mode == Automatic)
    void FixedUpdate()
    {
        if (mode == ControlMode.Automatic)
        {
            // In Auto mode, we simply mirror Unity's internal clock
            PerformLogicStep(Time.fixedDeltaTime);
            // Note: We do NOT call Physics.Simulate here; Unity does it automatically.
        }
    }

    // The Bridge calls this function (Runs only if mode == Manual)
    public void StepManual(float dt)
    {
        if (mode != ControlMode.Manual) return;

        // 1. Run Logic & Advance Virtual Time
        PerformLogicStep(dt);

        // 2. Run Physics (The "Script" mode requires explicit simulation)
        Physics.Simulate(dt);

        // 3. Send telemetry immediately after physics resolves
        if (MATCPTunnel.Instance != null)
        {
            MATCPTunnel.Instance.SendTelemetry();
        }
    }

    // Shared logic for both modes
    private void PerformLogicStep(float dt)
    {
        // 1. Update the virtual clock
        if (mode == ControlMode.Automatic)
        {
            fixedTime = Time.fixedTime;
            fixedDeltaTime = Time.fixedDeltaTime;
        }
        else
        {
            fixedDeltaTime = dt;
            fixedTime += dt;
        }

        // 2. Notify all listeners (Hub, Agents, Recorders)
        for (int i = 0; i < steppers.Count; i++)
        {
            if (steppers[i] != null)
            {
                steppers[i].TimeStep();
            }
        }
    }

    private IEnumerator TelemetryHeartbeat()
    {
        while (true)
        {
            if (mode == ControlMode.Automatic)
            {
                // Wait until the end of the physics frame
                yield return new WaitForFixedUpdate();

                // Check for connectivity before sending
                if (MATCPTunnel.Instance != null && MATCPTunnel.Instance.isConnected)
                {
                    MATCPTunnel.Instance.SendTelemetry();
                }
            }
            else
            {
                // In Manual mode, StepManual() handles the sending.
                // We just wait for the next frame so we don't spin the CPU.
                yield return null;
            }
        }
    }
}