using UnityEngine;

public class RDBehaviorStateLayer : MonoBehaviour
{
    [SerializeField] private RDStateConfig config;

    public RDBehaviorState Current { get; private set; }

    private float noveltyTimer;

    public void Tick(IInteractionSignalSource source)
    {
        if (source == null || config == null) return;

        RDBehaviorState next = Current;

        next.isGazing = source.IsGazing;
        next.gazeDuration = source.GazeDuration;
        next.noGazeDuration = source.NoGazeDuration;
        next.switchCount = source.SwitchCount;
        next.headMotion = source.HeadMotion;
        next.eyeJump = source.EyeJump;

        float rawAttention = source.IsGazing
            ? Mathf.Clamp01(source.GazeDuration / Mathf.Max(0.0001f, config.fullAttentionAfter))
            : 0f;

        next.attention = Damp(next.attention, rawAttention, config.attentionSmooth);

        float rawAgitation = Mathf.InverseLerp(
            config.calmHeadMotion,
            config.intenseHeadMotion,
            source.HeadMotion
        );

        if (source.EyeJump)
            rawAgitation = Mathf.Clamp01(rawAgitation + 0.25f);

        next.agitation = Damp(next.agitation, rawAgitation, config.agitationSmooth);

        float gazeFactor = source.IsGazing ? 1f : 0.25f;
        next.stability = Mathf.Clamp01(gazeFactor * (1f - next.agitation));

        if (source.EyeJump)
            noveltyTimer = config.noveltyHoldTime;
        else
            noveltyTimer = Mathf.Max(0f, noveltyTimer - Time.deltaTime);

        next.novelty = noveltyTimer > 0f ? 1f : 0f;

        Current = next;
    }

    private float Damp(float current, float target, float sharpness)
    {
        return Mathf.Lerp(current, target, 1f - Mathf.Exp(-sharpness * Time.deltaTime));
    }
}