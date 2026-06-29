using UnityEngine;

[CreateAssetMenu(menuName = "RD/State Config", fileName = "RDStateConfig")]
public class RDStateConfig : ScriptableObject
{
    [Header("Attention")]
    public float fullAttentionAfter = 1.5f;

    [Header("Head Motion")]
    public float calmHeadMotion = 0.02f;
    public float intenseHeadMotion = 0.30f;

    [Header("Novelty")]
    public float noveltyHoldTime = 0.2f;

    [Header("Smoothing")]
    [Range(0f, 30f)] public float attentionSmooth = 10f;
    [Range(0f, 30f)] public float agitationSmooth = 12f;
}