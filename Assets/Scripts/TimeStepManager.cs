using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

// Execution Order -100 ensures this initializes BEFORE the Car/Bridge
[DefaultExecutionOrder(-100)]
public class TimeStepManager : MonoBehaviour
{
    // --- The Virtual Time API ---
    // Mirroring Unity's Time.fixedTime and Time.fixedDeltaTime
    public static float fixedTime { get; private set; }
    public static float fixedDeltaTime { get; private set; }

    public enum ControlMode { Automatic, Manual }

    [Header("Simulation Settings")]
    [Tooltip("Automatic: Unity runs physics (Real-time).\nManual: Python/Script triggers physics (Training).")]
    public ControlMode mode = ControlMode.Automatic;

    [Tooltip("The time step used when in Manual mode. Initialized from Project Settings.")]
    public float fixedStepSize;
    public TMP_InputField timestepInput;

    // Cache the list to avoid finding objects every frame
    private List<ITimeStepper> steppers = new List<ITimeStepper>();

    // Called whenever the UI Checkbox is clicked
    public void OnToggleChanged(bool isAutomatic)
    {
        mode = isAutomatic ? ControlMode.Automatic : ControlMode.Manual;
        UpdatePhysicsMode();
    }

    void Awake()
    {
        // Initialize fixedDeltaTime from Project Settings
        fixedDeltaTime = Time.fixedDeltaTime;
        fixedTime = 0f;

        // 1. Capture the Project's native physics step (usually 0.02)
        // This ensures math consistency between Auto and Manual modes.
        if (fixedStepSize <= 0)
        {
            fixedStepSize = Time.fixedDeltaTime;
            timestepInput.text = fixedStepSize.ToString();
        }

        // 2. Find all objects that need stepping
        RefreshStepperList();

        // 3. Initialize the Physics Engine mode
        UpdatePhysicsMode();

        // Always start the heartbeat; the coroutine itself will check if it should send
        StartCoroutine(TelemetryHeartbeat());
    }

    // Called automatically when you change 'mode' in the Inspector
    void OnValidate()
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
            Debug.Log($"[TimeStepManager] Mode: AUTOMATIC (Real-time {Time.fixedDeltaTime * 1000}ms)");
        }
        else
        {
            Physics.simulationMode = SimulationMode.Script;
            Debug.Log($"[TimeStepManager] Mode: MANUAL (Waiting for commands...)");
        }
    }

    public void RefreshStepperList()
    {
        // Expensive operation, only call on Awake or Reset
        steppers = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)
            .OfType<ITimeStepper>()
            .ToList();

        Debug.Log($"[TimeStepManager] Registered {steppers.Count} steppers.");
    }

    // Unity's Internal Loop (Runs only if mode == Automatic)
    void FixedUpdate()
    {
        fixedStepSize = float.Parse(timestepInput.text);
        if (fixedStepSize < 0f)
            fixedStepSize = 0.000005f;

        if (mode == ControlMode.Automatic)
        {
            // In Auto mode, we pass the Unity clock time
            // We do NOT call Physics.Simulate here because Unity does it automatically after this returns
            PerformLogicStep(Time.fixedDeltaTime);
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
                if (TCPTunnel.Instance != null && TCPTunnel.Instance.isConnected)
                {
                    TCPTunnel.Instance.SendTelemetry();
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

    // The Bridge calls this function (Runs only if mode == Manual)
    public void StepManual(float dt)
    {
        if (mode != ControlMode.Manual) return;

        // 1. Run Logic
        PerformLogicStep(dt);

        // 2. Run Physics (The "Script" mode requires explicit simulation)
        Physics.Simulate(dt);

        // 3. Sync Transforms ensures raycasts hit the object's new position immediately
        Physics.SyncTransforms();

        // Send telemetry after everything settles
        TCPTunnel.Instance.SendTelemetry();
    }

    // Shared logic for both modes
    private void PerformLogicStep(float dt)
    {
        // 1. Update the virtual clock BEFORE calling scripts
        if (mode == ControlMode.Automatic)
        {
            // Sync exactly with Unity's internal physics clock
            fixedTime = Time.fixedTime;
            fixedDeltaTime = Time.fixedDeltaTime;
        }
        else
        {
            // Maintain manual deterministic increments
            fixedDeltaTime = dt;
            fixedTime += dt;
        }

        // Use a standard for-loop to avoid Garbage Collection (GC) in hot path
        for (int i = 0; i < steppers.Count; i++)
        {
            if (steppers[i] != null)
            {
                steppers[i].TimeStep();
            }
        }
    }
}