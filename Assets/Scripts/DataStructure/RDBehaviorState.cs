using UnityEngine;

[System.Serializable]
public struct RDBehaviorState
{
    public bool isGazing;
    public float gazeDuration;
    public float noGazeDuration;
    public int switchCount;
    public float headMotion;
    public bool eyeJump;

    public float attention;
    public float agitation;
    public float stability;
    public float novelty;
}