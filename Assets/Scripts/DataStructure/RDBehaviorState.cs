using UnityEngine;

[System.Serializable]
public struct RDBehaviorState
{
    // --- raw signals from the input source ---
    public bool isGazing;
    public float gazeDuration;
    public float noGazeDuration;
    public int switchCount;
    public float headMotion;
    public bool eyeJump;

    // --- derived, smoothed behaviour ---
    public float attention;
    public float agitation;
    public float stability;
    public float novelty;

    /// <summary>
    /// Recent rate of gaze switching, normalised 0..1 over a sliding time window.
    /// Use this instead of <see cref="switchCount"/> for any modulation: switchCount is a
    /// lifetime total and saturates permanently, this one rises and falls with the visitor.
    /// </summary>
    public float switchActivity;

    // --- long-term, survives across visitors (see RDLongTermMemory) ---
    /// <summary>How well the machine "knows" this kind of visitor. 0 = naive, 1 = saturated.</summary>
    public float familiarity;

    /// <summary>How worn out the machine is by being looked at. 0 = fresh, 1 = exhausted.</summary>
    public float fatigue;
}
