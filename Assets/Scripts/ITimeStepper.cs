using UnityEngine;

// Interface for any script that needs synchronized physics steps
public interface ITimeStepper
{
    void TimeStep();
}