using UnityEngine;

public class RDBehaviorStateLayer : MonoBehaviour
{
    [SerializeField] private RDStateConfig config;

    [Tooltip("Optional. Supplies familiarity / fatigue that persist across visitors.")]
    [SerializeField] private RDLongTermMemory longTermMemory;

    public RDBehaviorState Current { get; private set; }

    private float noveltyTimer;

    // Sliding window of switch timestamps. Fixed-size ring buffer: no allocation per frame.
    private const int SwitchHistoryCapacity = 64;
    private readonly float[] switchTimes = new float[SwitchHistoryCapacity];
    private int switchHead;
    private int switchStored;
    private int lastSeenSwitchCount;
    private bool switchCountInitialised;

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

        next.switchActivity = Damp(next.switchActivity, MeasureSwitchRate(source.SwitchCount), config.switchActivitySmooth);

        if (longTermMemory != null)
        {
            next.familiarity = longTermMemory.Familiarity;
            next.fatigue = longTermMemory.Fatigue;
        }

        Current = next;
    }

    /// <summary>
    /// Called when a visitor leaves and the short-term reading of the room should start over.
    /// Long-term familiarity / fatigue are deliberately NOT cleared here.
    /// </summary>
    public void ResetShortTerm()
    {
        RDBehaviorState cleared = Current;

        cleared.attention = 0f;
        cleared.agitation = 0f;
        cleared.stability = 0f;
        cleared.novelty = 0f;
        cleared.switchActivity = 0f;
        cleared.gazeDuration = 0f;
        cleared.headMotion = 0f;
        cleared.eyeJump = false;
        cleared.isGazing = false;

        Current = cleared;

        noveltyTimer = 0f;
        switchHead = 0;
        switchStored = 0;
        switchCountInitialised = false;
    }

    /// <summary>
    /// Turns the input source's monotonic SwitchCount into a 0..1 rate over a sliding window,
    /// so it keeps responding after the sixth switch of the day.
    /// </summary>
    private float MeasureSwitchRate(int currentSwitchCount)
    {
        if (!switchCountInitialised)
        {
            lastSeenSwitchCount = currentSwitchCount;
            switchCountInitialised = true;
        }

        int newSwitches = currentSwitchCount - lastSeenSwitchCount;
        lastSeenSwitchCount = currentSwitchCount;

        // Record each new switch, but never let a burst overrun the buffer.
        newSwitches = Mathf.Clamp(newSwitches, 0, SwitchHistoryCapacity);

        for (int i = 0; i < newSwitches; i++)
        {
            switchTimes[switchHead] = Time.time;
            switchHead = (switchHead + 1) % SwitchHistoryCapacity;
            if (switchStored < SwitchHistoryCapacity) switchStored++;
        }

        float window = Mathf.Max(0.05f, config.switchWindow);
        float cutoff = Time.time - window;

        int insideWindow = 0;
        for (int i = 0; i < switchStored; i++)
        {
            int index = (switchHead - 1 - i + SwitchHistoryCapacity * 2) % SwitchHistoryCapacity;
            if (switchTimes[index] >= cutoff) insideWindow++;
            else break; // entries are in time order, so the rest are older still
        }

        int full = Mathf.Max(1, config.switchesForFullActivity);
        return Mathf.Clamp01(insideWindow / (float)full);
    }

    private float Damp(float current, float target, float sharpness)
    {
        return Mathf.Lerp(current, target, 1f - Mathf.Exp(-sharpness * Time.deltaTime));
    }
}
