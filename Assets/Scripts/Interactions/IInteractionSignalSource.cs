using UnityEngine;

public interface IInteractionSignalSource
{
    bool IsGazing { get; }
    float GazeDuration { get; }
    float NoGazeDuration { get; }
    int SwitchCount { get; }
    float HeadMotion { get; }
    bool EyeJump { get; }
    Vector3 InteractionDirection { get; }
}