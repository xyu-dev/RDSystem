using UnityEngine;

/// <summary>
/// What the room does after the headset comes off: it contracts, slowly, over a couple of
/// minutes, and then it breathes.
///
/// Two clocks, and keeping them separate is the whole fix.
///
/// The FIELD dynamics switch to resting almost at once - a third of a second, on absence itself
/// with no grace period. That has to be fast, because the thing that made the dent vanish the
/// instant the headset came off was diffusion, not decay: at 3.5 per second the field blurs
/// towards its own average three and a half times a second, which flattens a bump in well under a
/// second whatever the decay is set to, and left the slow fade downstream with nothing to fade.
/// Dropping diffusion almost to zero freezes the SHAPE.
///
/// It took two passes to get this right, and the second one is the instructive one. Shortening
/// the settle was not enough while the field still waited out the four-second grace before it
/// began settling at all - painting stops immediately, so those four seconds were pure smoothing
/// of a picture nobody was drawing, about fourteen diffusion units, and a bump does not survive
/// one. The grace period belongs to the picture. The field cannot afford it.
///
/// The CONTRACTION is then the decay alone, with a long time constant, so the dent pulls in over
/// a minute and a half rather than falling away. That is what "shrink slowly" actually needs: a
/// bump whose peak drops while its shape holds reads as contracting, because the saturated plateau
/// in the middle shrinks from the edges inward.
///
/// And then it does not stop. At rest the surface breathes - a slow drift in the ink, a few
/// percent, about nine seconds a cycle - and underneath that it pumps, once a beat, on the same
/// clock as the monitor beeping in the empty room. A room that goes completely still reads as
/// switched off; a room with a pulse on it reads as unattended, which is the harder thing and
/// the true one. The rate falls as the picture shrinks, so the recession is told twice: smaller,
/// and further apart.
/// </summary>
public class RDRecession : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Preferred. The headset gate knows whether anyone is wearing it.")]
    [SerializeField] private RDHeadsetSimulator headset;

    [Tooltip("Fallback when there is no headset gate: presence is read from the gaze instead.")]
    [SerializeField] private RDController controller;

    [SerializeField] private Material displayMaterial;
    [SerializeField] private RDGazeDepthDriver depthDriver;
    [SerializeField] private global::RDSystem.RDSystemUpdater simulationUpdater;

    [Tooltip("Optional. When assigned, the machine room swells on the same phase as the surface, " +
             "so the sound and the picture breathe together instead of beating against each other.")]
    [SerializeField] private RDAudioEngine audioEngine;

    [Header("Timing")]
    [Tooltip("Seconds from the headset coming off to the room being fully still. This is what " +
             "the visitor watches happen, so it is long on purpose.")]
    [SerializeField] private float recessionSeconds = 150f;

    [Tooltip("Seconds to come back when someone puts the headset on. Fast: arriving should feel " +
             "like the room noticing, not like it warming up.")]
    [SerializeField] private float returnSeconds = 2.5f;

    [Tooltip("Seconds for the field dynamics to settle into resting. This is the brake on the " +
             "instant collapse and it has to be SHORT - diffusion at 3.5 per second erases a " +
             "bump in well under a second, so every tenth of a second spent ramping is shape " +
             "that never reaches the slow contraction. 0.35 leaves about 0.6 diffusion units.")]
    [SerializeField] private float fieldSettleSeconds = 0.35f;

    [Tooltip("Seconds of absence before the PICTURE starts receding, so someone adjusting the " +
             "headset does not trigger it. The field does not wait for this - it freezes as soon " +
             "as the gaze stops painting, or there is nothing left by the time the grace ends.")]
    [SerializeField] private float graceSeconds = 4f;

    [Header("At rest")]
    [Tooltip("Diffusion per second at rest. Near zero: the shape freezes and the decay does the " +
             "contracting. This is the number the bug was in.")]
    [Range(0f, 2f)] [SerializeField] private float restingDiffuse = 0.12f;

    [Tooltip("Depth decay time constant at rest, in seconds. Long, so the dent pulls in over a " +
             "minute and a half instead of dropping away.")]
    [SerializeField] private float restingDecayTau = 70f;

    [Tooltip("Simulation steps per frame at rest. 1 keeps the field alive without it visibly " +
             "evolving; 0 is not offered because a frozen field looks broken.")]
    [Range(1, 8)] [SerializeField] private int restingSteps = 1;

    [Tooltip("Fraction of the tuned value gain left at rest. The ink thins rather than fading " +
             "to black: an empty room is pale, not switched off.")]
    [Range(0.2f, 1f)] [SerializeField] private float restingGainFraction = 0.62f;

    [Header("Idle swing")]
    [Tooltip("How far the empty room swings back towards its active look on each cycle, as a " +
             "fraction. This is the big one - it is the whole active-to-resting transform run " +
             "backwards and released, which is what pressing H on and off looks like.\n\n" +
             "That is why toggling H read so much better than the beat pump did: it moves the " +
             "value gain across 1.4 to 0.87 and restarts the simulation at four steps instead " +
             "of one, so the pattern itself comes alive and stills. The pump moved a threshold " +
             "by a tenth on a frozen field.\n\n" +
             "What it does NOT do is touch occupancy. Toggling the headset flag for real would " +
             "feed invented visitors to the presence tracker, the receipt counter and the " +
             "bandit, all of which persist across the whole exhibition on purpose - an empty " +
             "night would silently poison a week of data. It also leaves the field frozen, " +
             "where a real toggle would put diffusion back to 3.5 and flatten the dent within a " +
             "few cycles.\n\n" +
             "Keep it below about 0.8: at 1.0 the empty room looks exactly like an occupied " +
             "one at peak, and a passer-by can no longer tell whether anyone is inside.")]
    [Range(0f, 1f)] [SerializeField] private float idleSwing = 0.65f;

    [Header("Breathing")]
    [Tooltip("Slow swell in the value gain once the room is at rest, as a fraction. This is the " +
             "respiration underneath the pulse - a drift, not a beat.")]
    [Range(0f, 0.3f)] [SerializeField] private float breathAmount = 0.06f;

    [Tooltip("Seconds per breath. Human resting breathing is four to six; this is slower, " +
             "because the room is not a person.")]
    [SerializeField] private float breathSeconds = 9f;

    [Header("Pulse")]
    [Tooltip("How far the beat moves the ink THRESHOLDS. This is the one that makes a pump read " +
             "as a pump.\n\n" +
             "Pumping the value gain barely shows, and the shader says why: value is " +
             "pow(saturate(v * _ValueGain), p), so inside every blob v * gain is already past 1 " +
             "and saturate pins it - the gain only moves the thin band at the edge where the " +
             "value is still climbing. Lowering the threshold instead slides the whole edge " +
             "outward, so blobs visibly swell and shrink and nothing changes brightness.\n\n" +
             "Against a core softness of 0.05, a shift of 0.05 moves the edge by half the width " +
             "of its own transition. Go to 0.15 and it thumps.")]
    [Range(0f, 0.25f)] [SerializeField] private float pulseThreshold = 0.08f;

    [Tooltip("How far the beat moves the value gain, as a fraction. Secondary to the threshold " +
             "above - it adds a little lift at the blob edges, and nothing in their middles.")]
    [Range(0f, 0.3f)] [SerializeField] private float pulseAmount = 0.10f;

    [Tooltip("Beats per minute the moment the headset comes off. A room that has just lost " +
             "someone is not yet calm.")]
    [SerializeField] private float pulseBpmActive = 66f;

    [Tooltip("Beats per minute once it has fully receded. Slower than a resting human, and " +
             "slowing is the point: the recession is said twice, as a size and as a rate.")]
    [SerializeField] private float pulseBpmResting = 44f;

    [Header("Authored material values")]
    [Tooltip("What the material is supposed to look like with a visitor present. Set by the " +
             "scene setup and restored when play stops.\n\n" +
             "These are stored HERE rather than read from the material at Awake, and that is not " +
             "a style choice. This component writes to a shared material ASSET, and in the editor " +
             "those writes survive leaving play mode - so reading 'active' from the material meant " +
             "reading back whatever the last session had faded it down to. Relief and bloom stuck " +
             "at 0, and the gain ratcheted 1.4 to 0.87 to 0.54 to 0.33 to 0.21 over a handful of " +
             "runs until the surface had almost no ink left in it.")]
    [SerializeField] private float authoredGain = 1.4f;
    [SerializeField] private float authoredRelief = 0.8f;
    [SerializeField] private float authoredBloom = 0.35f;
    [SerializeField] private float authoredValueShift = 0.10f;

    [Tooltip("The ink thresholds as tuned. The pulse moves these and they are put back on exit - " +
             "same discipline as the four above, and for the same reason: these live on a shared " +
             "material asset and an editor write to one outlives play mode.")]
    [SerializeField] private float authoredCoreThreshold = 0.40f;
    [SerializeField] private float authoredHaloThreshold = 0.14f;

    [Header("Read-only")]
    [SerializeField] private float debugRecession;
    [SerializeField] private float debugFieldRest;
    [SerializeField] private float debugPulse;
    [SerializeField] private bool debugOccupied;

    private static readonly int ValueGainId = Shader.PropertyToID("_ValueGain");
    private static readonly int DepthReliefId = Shader.PropertyToID("_DepthRelief");
    private static readonly int DepthBloomId = Shader.PropertyToID("_DepthBloom");
    private static readonly int DepthValueShiftId = Shader.PropertyToID("_DepthValueShift");
    private static readonly int CoreThresholdId = Shader.PropertyToID("_CoreThreshold");
    private static readonly int HaloThresholdId = Shader.PropertyToID("_HaloThreshold");

    // Captured at Awake rather than hardcoded, so the recession always returns to whatever was
    // tuned on the material instead of to numbers written here months earlier.
    private float activeGain = 1.4f;
    private float activeRelief = 0.8f;
    private float activeBloom = 0.35f;
    private float activeValueShift = 0.10f;
    private float activeDecayTau = 10f;
    private float activeDiffuse = 3.5f;
    private int activeSteps = 4;

    private float raw;          // 0 occupied, 1 fully receded, linear
    private float fieldRest;    // 0 active dynamics, 1 resting dynamics - its own, faster clock
    private float absentFor;
    private float breathPhase;
    private float localBeat;    // fallback clock, only used when there is no audio engine

    /// <summary>0 while someone is present, 1 when the room has gone fully still. Eased.</summary>
    public float Recession { get; private set; }

    /// <summary>
    /// How far a beat moves the ink thresholds - the strength of the pump. Settable so a tuner
    /// can drive it; see RDRecessionTuner, which is the front end for this and the two below.
    /// </summary>
    public float PulseThreshold
    {
        get => pulseThreshold;
        set => pulseThreshold = Mathf.Clamp(value, 0f, 0.25f);
    }

    /// <summary>
    /// How far the empty room swings back towards its active look each cycle. The big visual
    /// move; the beat pump below is detail on top of it.
    /// </summary>
    public float IdleSwing
    {
        get => idleSwing;
        set => idleSwing = Mathf.Clamp01(value);
    }

    /// <summary>Seconds per idle cycle. Also the period of the machine room's swell.</summary>
    public float CycleSeconds
    {
        get => breathSeconds;
        set => breathSeconds = Mathf.Clamp(value, 1f, 120f);
    }

    /// <summary>How far a beat moves the value gain. Rides along with the threshold.</summary>
    public float PulseAmount
    {
        get => pulseAmount;
        set => pulseAmount = Mathf.Clamp(value, 0f, 0.3f);
    }

    /// <summary>Seconds from the headset coming off to the picture being fully still.</summary>
    public float RecessionSeconds
    {
        get => recessionSeconds;
        set => recessionSeconds = Mathf.Clamp(value, 1f, 900f);
    }

    /// <summary>
    /// Decay time constant of the dent once the room is at rest. This is the one that actually
    /// contracts the shape; the ramp above only fades what is left of it.
    /// </summary>
    public float RestingDecayTau
    {
        get => restingDecayTau;
        set => restingDecayTau = Mathf.Clamp(value, 1f, 240f);
    }

    /// <summary>True while the room is subsiding but not yet still - what the monitor labels.</summary>
    public bool Receding => Recession > 0.01f && Recession < 0.99f;

    /// <summary>True once the room has settled and is only breathing.</summary>
    public bool Breathing => Recession >= 0.99f;

    private void Awake()
    {
        if (headset == null) headset = GetComponent<RDHeadsetSimulator>();
        if (controller == null) controller = GetComponent<RDController>();
        if (depthDriver == null) depthDriver = GetComponent<RDGazeDepthDriver>();

        // From the serialized fields, never from the material. See the note on them.
        activeGain = authoredGain > 0.0001f ? authoredGain : 1.4f;
        activeRelief = authoredRelief;
        activeBloom = authoredBloom;
        activeValueShift = authoredValueShift;

        // Put the material back to the authored state immediately, in case a previous session
        // was killed before it could restore.
        Apply(0f, 0f, 0f, 0f);

        if (depthDriver != null)
        {
            activeDecayTau = depthDriver.DecayTau;
            activeDiffuse = depthDriver.DiffusePerSecond;
        }

        if (simulationUpdater != null) activeSteps = simulationUpdater.StepsPerFrame;
    }

    /// <summary>
    /// Start in whatever state the room is actually in.
    ///
    /// Nobody is wearing the headset when the piece launches, so the room is already empty - it
    /// should not spend the first two and a half minutes receding from an occupancy that never
    /// happened. In Start rather than Awake so the headset gate has initialised first.
    /// </summary>
    private void Start()
    {
        if (Occupied()) return;

        raw = 1f;
        fieldRest = 1f;
        Recession = 1f;
        absentFor = graceSeconds + 1f;

        Apply(1f, 1f, 0f, 0f);
    }

    private void Update()
    {
        bool occupied = Occupied();

        if (occupied) absentFor = 0f;
        else absentFor += Time.deltaTime;

        // The grace period is why a visitor can lift the headset to scratch their nose without
        // the room starting to die. It gates the PICTURE only - see the field clock below.
        bool receding = !occupied && absentFor >= graceSeconds;

        // --- the slow clock: what the picture does ---
        float target = receding ? 1f : 0f;
        float seconds = receding ? recessionSeconds : returnSeconds;

        raw = Mathf.MoveTowards(raw, target, Time.deltaTime / Mathf.Max(0.1f, seconds));

        // Eased, so the subsiding starts almost imperceptibly and settles rather than stopping.
        Recession = raw * raw * (3f - 2f * raw);

        // --- the fast clock: what the field does ---
        //
        // Triggered by absence ITSELF, with no grace, and this is the fix for the dent still
        // vanishing on contact. Painting stops the instant the headset comes off, but the field
        // kept its active dynamics until the grace period expired - so for four seconds nothing
        // was being drawn and diffusion at 3.5 per second was smoothing what was already there.
        //
        // Four seconds of that is about fourteen diffusion units, and a bump does not survive
        // one. The three-second settle that followed added five more. By the time the slow clock
        // began its hundred and fifty second contraction there was nothing left to contract: the
        // ramp was correct, it was just ramping an already-flat field.
        //
        // Stopping the smoothing has no transition to hide - the field value does not jump, only
        // its rate of change does - so this can be nearly instant, and has to be. At 0.35 s the
        // integral is about 0.6 diffusion units, which the shape survives.
        float fieldTarget = occupied ? 0f : 1f;
        float fieldSeconds = occupied ? returnSeconds : fieldSettleSeconds;

        fieldRest = Mathf.MoveTowards(fieldRest, fieldTarget, Time.deltaTime / Mathf.Max(0.05f, fieldSeconds));

        // --- breathing, only once it has settled ---
        breathPhase += Time.deltaTime / Mathf.Max(0.5f, breathSeconds);
        if (breathPhase > 1f) breathPhase -= 1f;

        // The idle swing. Fast up, slow down - toggling H by hand gives a 2.5 s return and a
        // long subside, and that asymmetry is most of why it reads as alive rather than as a
        // looping animation. A sine gives neither end of it.
        float swing = IdleEnvelope(breathPhase);

        // One wave, two consumers. The surface and the machine room are driven from the same
        // phase rather than each running its own oscillator at the same rate - two breaths that
        // drift apart read as two things, and the room has to read as one.
        float wave = (swing * 2f - 1f) * Recession;

        // --- the pulse, on top of the breath ---
        //
        // The rate falls as the room recedes, so the settling is told twice: the picture gets
        // smaller and the beat gets further apart. Two timescales, which is also how a body
        // works - respiration slow and drifting, pulse fast and sharp.
        float bpm = Mathf.Lerp(pulseBpmActive, pulseBpmResting, Recession);

        if (audioEngine != null) audioEngine.SetPulseRate(bpm);

        // Read the beat back off the audio thread wherever there is one. The beep cannot
        // tolerate frame jitter and the picture cannot hear it, so the sound keeps the clock
        // and the surface follows. Without an engine the same curve runs locally instead, so a
        // test scene with no audio still pumps.
        float pulse;

        if (audioEngine != null)
        {
            pulse = audioEngine.PulseEnvelope;
            localBeat = 0f;
        }
        else
        {
            localBeat += Time.deltaTime;
            if (localBeat >= 60f / Mathf.Max(20f, bpm)) localBeat = 0f;

            pulse = RDAudioEngine.CardiacEnvelope(localBeat);
        }

        // Centred, so a beat lifts the ink and the gap between beats lets it back down rather
        // than the surface sitting permanently swollen.
        float beat = (pulse - 0.28f) * Recession;

        // The swing pulls the DISPLAY transform back towards active - gain, relief, bloom, value
        // shift and the step count all together, which is the whole of what a toggle changes
        // that you can see. The field clock is left alone at rest, so the shape stays frozen
        // while all of that moves over it. A real toggle cannot do that half.
        float displayT = Recession * (1f - idleSwing * swing);

        Apply(displayT, fieldRest, wave * breathAmount + beat * pulseAmount, beat);

        if (audioEngine != null)
            audioEngine.SetRoomBreath(wave);

        debugPulse = pulse;

        debugRecession = Recession;
        debugFieldRest = fieldRest;
        debugOccupied = occupied;
    }

    /// <summary>
    /// One idle cycle, 0 to 1 to 0. Rises over the first fifth and falls over the rest, because
    /// that is the shape of the thing it is imitating: a toggle snaps the room back in a couple
    /// of seconds and then lets it subside for as long as it likes.
    /// </summary>
    private static float IdleEnvelope(float p)
    {
        const float rise = 0.18f;

        if (p < rise)
        {
            float x = p / rise;
            return x * x * (3f - 2f * x);
        }

        float y = (p - rise) / (1f - rise);
        return 1f - y * y * (3f - 2f * y);
    }

    private bool Occupied()
    {
        if (headset != null) return headset.Worn;

        if (controller != null)
        {
            RDBehaviorState state = controller.CurrentState;
            return state.isGazing || state.noGazeDuration < 2.5f;
        }

        return true;
    }

    private void Apply(float t, float field, float breath, float beat)
    {
        if (displayMaterial != null)
        {
            if (displayMaterial.HasProperty(ValueGainId))
            {
                float gain = Mathf.Lerp(activeGain, activeGain * restingGainFraction, t);
                displayMaterial.SetFloat(ValueGainId, gain * (1f + breath));
            }

            // The pump itself. Both thresholds move together and by the same absolute amount, so
            // the core and its halo swell as one shape rather than the glow sliding off the blob.
            // Floored above zero: at a threshold of 0 everything is ink and the picture fills in.
            float shift = beat * pulseThreshold;

            if (displayMaterial.HasProperty(CoreThresholdId))
                displayMaterial.SetFloat(CoreThresholdId, Mathf.Max(0.02f, authoredCoreThreshold - shift));

            if (displayMaterial.HasProperty(HaloThresholdId))
                displayMaterial.SetFloat(HaloThresholdId, Mathf.Max(0.01f, authoredHaloThreshold - shift));

            if (displayMaterial.HasProperty(DepthReliefId))
                displayMaterial.SetFloat(DepthReliefId, Mathf.Lerp(activeRelief, 0f, t));

            if (displayMaterial.HasProperty(DepthBloomId))
                displayMaterial.SetFloat(DepthBloomId, Mathf.Lerp(activeBloom, 0f, t));

            if (displayMaterial.HasProperty(DepthValueShiftId))
                displayMaterial.SetFloat(DepthValueShiftId, Mathf.Lerp(activeValueShift, 0f, t));
        }

        // The field dynamics follow the FAST clock. Freezing the shape has to happen in seconds;
        // waiting for the slow ramp is what let the dent collapse before the fade began.
        if (depthDriver != null)
        {
            depthDriver.DiffusePerSecond = Mathf.Lerp(activeDiffuse, restingDiffuse, field);
            depthDriver.DecayTau = Mathf.Lerp(activeDecayTau, restingDecayTau, field);
        }

        if (simulationUpdater != null)
            simulationUpdater.StepsPerFrame = Mathf.RoundToInt(Mathf.Lerp(activeSteps, restingSteps, t));
    }

    /// <summary>
    /// Hand the material back as it was found.
    ///
    /// Without this, stopping play leaves the asset wherever the fade had reached - which for a
    /// shared material is permanent, and is how the surface ended up with its bloom and relief
    /// stuck at zero.
    /// </summary>
    private void OnDisable()
    {
        Apply(0f, 0f, 0f, 0f);

        if (depthDriver != null)
        {
            depthDriver.DiffusePerSecond = activeDiffuse;
            depthDriver.DecayTau = activeDecayTau;
        }

        if (simulationUpdater != null)
            simulationUpdater.StepsPerFrame = activeSteps;

        if (audioEngine != null)
            audioEngine.SetRoomBreath(0f);
    }

    /// <summary>Put the room straight back to active. For the operator between visitors.</summary>
    public void Wake()
    {
        raw = 0f;
        fieldRest = 0f;
        Recession = 0f;
        absentFor = 0f;
        Apply(0f, 0f, 0f, 0f);
    }
}
