using UnityEngine;

[CreateAssetMenu(menuName = "RD/Mapping Config", fileName = "RDMappingConfig")]
public class RDMappingConfig : ScriptableObject
{
    [Header("Idle Baseline")]
    public float idleRadius = 0.05f;
    public float idleFeed = 0.031f;
    public float idleKill = 0.059f;

    [Header("Reset")]
    public float resetTarget = 0.75f;
    public float resetEnterDelay = 0.35f;
    public float resetReturnTime = 2.5f;

    [Header("Gaze Growth")]
    public float gazeMaxRadius = 0.10f;
    public float gazeMaxFeed = 0.043f;
    public float gazeBaseKill = 0.055f;

    [Header("Dynamic Change")]
    public float gazeNoveltyKill = 0.050f;
    public float agitationFeedBoost = 0.003f;
}