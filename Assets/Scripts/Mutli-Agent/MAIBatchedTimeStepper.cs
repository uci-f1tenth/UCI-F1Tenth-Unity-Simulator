using Unity.Jobs;

/// <summary>
/// Implement this on manager scripts that utilize Burst/Job System across multiple agents.
/// </summary>
public interface IBatchedTimeStepper
{
    /// <summary>
    /// Phase 1: Setup data and schedule Jobs. Return the JobHandle. DO NOT call .Complete() here.
    /// </summary>
    JobHandle ScheduleStepJob();

    /// <summary>
    /// Phase 2: Extract data from NativeArrays to standard arrays. Guaranteed to run after Jobs finish.
    /// </summary>
    void ResolveStepData();
}