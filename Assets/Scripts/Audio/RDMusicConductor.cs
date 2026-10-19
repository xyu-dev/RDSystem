using UnityEngine;

/// <summary>
/// The composer. Reads the same state that drives the reaction-diffusion surface and decides what
/// the room plays, then hands notes to RDAudioEngine.
///
/// The mapping is the artistic argument of the piece, so it is worth stating plainly:
///
///   nobody looking      -> the channel goes dead: static, electronic artefacts, and the waiting
///                          bell still tolling underneath but no longer getting through
///   gaze arrives        -> the cycle starts; kendang appears, gong marks each cycle
///   sustained attention -> pulse accelerates, pipa enters and climbs
///   restless looking    -> drum density and pipa tremolo rise, strokes get brighter
///   a saccade           -> an accented rin, off the grid
///   Pearson class       -> the mode the whole room is tuned to (seven classes, seven tunings)
///   chase mode          -> the pulse drops out and answers from the opposite side of the sphere
///   accumulated fatigue -> everything slows and darkens, all week long
///
/// Attach next to RDController. The engine can live on the same object or the audio listener.
/// </summary>
public class RDMusicConductor : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private RDAudioEngine engine;
    [SerializeField] private RDController controller;
    [SerializeField] private LinearBehaviorMapper mapper;
    [SerializeField] private RDMusicConfig config;

    [Tooltip("Optional. When assigned, the bandit's current strategy sets the pace and the " +
             "instrument balance, so a change of strategy is heard as well as seen.")]
    [SerializeField] private RDGreedyDirector director;

    [Header("Strategy Response")]
    [Tooltip("Step-time multiplier at strategy tempo = 0 and = 1. Below 1 is faster.")]
    [SerializeField] private Vector2 strategyTempoScale = new Vector2(1.7f, 0.5f);

    [Tooltip("Floor on instrument weights, so no strategy silences a voice entirely.")]
    [Range(0f, 1f)] [SerializeField] private float instrumentFloor = 0.2f;

    [Header("Behaviour")]
    [Tooltip("Silence everything. For setting up the room without the sound running.")]
    [SerializeField] private bool muted = false;

    [Tooltip("Let the piece play its waiting bell when no one is present.")]
    [SerializeField] private bool playWhenEmpty = true;

    [Header("Read-only")]
    [SerializeField] private int debugStep;
    [SerializeField] private int debugCycle;
    [SerializeField] private string debugMode = "";
    [SerializeField] private float debugStepSeconds;

    private float stepTimer;
    private int step;
    private int cycle;

    private float idleBellTimer;
    private float nextIdleBellAt;

    private bool lastEyeJump;
    private bool lastChaseActive;

    // The pipa walks the scale rather than jumping randomly, so it reads as a line.
    private int pipaDegree;
    private int pipaDirection = 1;

    private float emptyDuration;
    private float glitchTimer;
    private float signalTimer;
    private float nextSignalAt;

    // Preallocated: the burst queue must not allocate while the room is running for a week.
    private const int PendingSignalCapacity = 8;
    private readonly float[] signalDelay = new float[PendingSignalCapacity];
    private readonly float[] signalFrequency = new float[PendingSignalCapacity];
    private readonly float[] signalAmplitude = new float[PendingSignalCapacity];
    private readonly float[] signalPan = new float[PendingSignalCapacity];
    private readonly float[] signalChirp = new float[PendingSignalCapacity];
    private int pendingSignals;
    private float nextGlitchAt;

    private void Awake()
    {
        if (controller == null) controller = GetComponent<RDController>();
        if (mapper == null) mapper = GetComponent<LinearBehaviorMapper>();
        if (engine == null) engine = GetComponent<RDAudioEngine>();

        if (engine == null)
            Debug.LogError("[RDMusicConductor] No RDAudioEngine assigned; the piece will be silent.");

        if (config == null)
            Debug.LogError("[RDMusicConductor] No RDMusicConfig assigned.");

        // The room starts empty. Without this the machine room would ramp up over its full
        // signalRampSeconds from launch, as though someone had only just walked out.
        emptyDuration = 600f;

        ScheduleNextIdleBell();
        ScheduleNextGlitch();
        ScheduleNextSignal();
    }

    private void Update()
    {
        if (muted || engine == null || controller == null || config == null) return;

        RDBehaviorState state = controller.CurrentState;
        RDTarget target = controller.CurrentTarget;

        RDMode mode = CurrentMode();
        debugMode = mode.name;

        float drive = ComputeDrive(state);
        float pan = ComputePan();

        bool present = state.isGazing || state.noGazeDuration < 2.5f;

        // Both of these strike a rin, and both used to run BEFORE this check - so an instrument
        // was structurally reachable with nobody in the room. They should never have been
        // outside it: a saccade and a chase are things a visitor does, and there is no visitor.
        if (present)
        {
            HandleSaccade(state, mode, pan);
            HandleChaseTransition(state, mode, pan);
        }
        else
        {
            // Cleared so the first saccade of the next visit reads as an event rather than as
            // the tail of the last one.
            lastEyeJump = false;
            lastChaseActive = false;
        }

        if (!present)
        {
            TickEmptyRoom(mode, pan, state);
            return;
        }

        idleBellTimer = 0f;

        // Someone is looking: the channel has signal again. The bed fades out on its own.
        emptyDuration = 0f;
        engine.SetSignalNoise(0f, config.signalUnrest);

        float stepSeconds = Mathf.Lerp(config.slowStepSeconds, config.fastStepSeconds, drive);

        // A fatigued machine plays slower. This accumulates across the whole exhibition.
        stepSeconds *= 1f + state.fatigue * 0.6f;

        // The greedy algorithm's pace bias, on top of what the gaze asked for.
        stepSeconds *= StrategyTempoScale();

        debugStepSeconds = stepSeconds;

        stepTimer += Time.deltaTime;

        // A single step per frame at most: better to drift than to fire a burst after a hitch.
        if (stepTimer >= stepSeconds)
        {
            stepTimer -= stepSeconds;
            AdvanceStep(state, target, mode, drive, pan);
        }
    }

    // ------------------------------------------------------------------ pulse

    private void AdvanceStep(RDBehaviorState state, RDTarget target, RDMode mode, float drive, float pan)
    {
        int cycleSteps = Mathf.Max(4, config.cycleSteps);

        if (step == 0)
        {
            // The gong ends and begins the cycle, as in a gamelan gongan.
            if (cycle % Mathf.Max(1, config.gongEveryCycles) == 0)
                StrikeGong(mode, pan, state);

            if (cycle % Mathf.Max(1, config.bellEveryCycles) == 0)
                StrikeTempleBell(mode, pan, state, 0.75f);
        }

        // During chase the PULSE withholds itself; the answer already sounded on the far side.
        //
        // Only the drum. Suppressing the pipa here as well was over-reach on my part: with chase
        // on and the bandit pushing evasion, the melody went silent for most of a visit, and the
        // chase read as a dropout rather than as a move. The drum stopping while the line carries
        // on is also the better musical event - it leaves the chase a texture instead of a hole.
        if (!controller.ChaseActive)
            MaybeStrikeKendang(state, mode, drive, pan, step, cycleSteps);

        MaybePlayPipa(state, mode, drive, pan, step, cycleSteps);

        step++;

        if (step >= cycleSteps)
        {
            step = 0;
            cycle++;
        }

        debugStep = step;
        debugCycle = cycle;
    }

    private void MaybeStrikeKendang(RDBehaviorState state, RDMode mode, float drive, float pan,
        int currentStep, int cycleSteps)
    {
        float chance = Mathf.Lerp(config.idleDrumChance, config.activeDrumChance, drive);

        // Strong beats are more likely, so a pattern emerges instead of an even rain of hits.
        bool onBeat = currentStep % 4 == 0;
        bool offBeat = currentStep % 2 == 1;

        if (onBeat) chance = Mathf.Clamp01(chance * 1.6f);
        if (offBeat) chance *= 0.65f;

        chance *= StrategyPercussion;

        if (Random.value > chance) return;

        // Low thump on the beat, high slap off it: the interlocking that drives gamelan.
        bool high = !onBeat || Random.value < 0.3f;

        float frequency = high ? config.kendangHighHz : config.kendangLowHz;
        float brightness = high
            ? Mathf.Lerp(0.55f, 0.95f, state.agitation)
            : Mathf.Lerp(0.10f, 0.35f, state.agitation);

        float amplitude = config.kendangLevel *
            Mathf.Lerp(0.45f, 1f, drive) *
            (onBeat ? 1f : 0.7f) *
            (1f - state.fatigue * 0.3f);

        engine.Play(new RDNoteEvent
        {
            kind = RDVoiceKind.Kendang,
            frequency = frequency * Random.Range(0.97f, 1.03f),
            amplitude = amplitude,
            pan = Mathf.Clamp(pan * 0.6f + Random.Range(-0.2f, 0.2f), -1f, 1f),
            decay = config.kendangDecay * Random.Range(0.8f, 1.2f),
            brightness = StrategyBrightness(brightness)
        });
    }

    private void MaybePlayPipa(RDBehaviorState state, RDMode mode, float drive, float pan,
        int currentStep, int cycleSteps)
    {
        if (state.attention < config.pipaEntryAttention) return;

        // Floored at 0.35 rather than starting from 0. The chance below is a product of three
        // fractions - density, this, and the strategy weight - and starting this one at zero made
        // the product collapse: at attention 0.45 it worked out to one note every seventy seconds,
        // which is not a sparse melody, it is an absent one. Crossing the entry threshold now
        // gets you a third of full density immediately, and attention takes it the rest of the way.
        float entered = Mathf.Lerp(0.35f, 1f,
            Mathf.InverseLerp(config.pipaEntryAttention, 1f, state.attention));

        float chance = config.pipaDensity * entered * StrategyMelody;

        if (Random.value > chance) return;

        // Walk the mode, turning back at the edges of the register.
        pipaDegree += pipaDirection * Random.Range(1, 3);

        int span = Mathf.Max(3, mode.Count * 2);

        if (pipaDegree > span) { pipaDegree = span; pipaDirection = -1; }
        if (pipaDegree < 0) { pipaDegree = 0; pipaDirection = 1; }

        if (Random.value < 0.25f) pipaDirection *= -1;

        float root = config.RootFrequency * Mathf.Pow(2f, config.pipaOctave);
        float frequency = mode.Frequency(root, pipaDegree);

        // Tremolo and slides come out under restless looking, which is where the instrument's
        // own vocabulary happens to live.
        float tremolo = Mathf.Clamp01(state.agitation * 0.9f + state.novelty * 0.3f);
        float bend = Random.value < 0.3f ? Random.Range(-2f, 2f) * state.agitation : 0f;

        engine.Play(new RDNoteEvent
        {
            kind = RDVoiceKind.Pipa,
            frequency = frequency,
            amplitude = config.pipaLevel * Mathf.Lerp(0.5f, 1f, entered) * (1f - state.fatigue * 0.35f) * StrategyMelody,
            pan = Mathf.Clamp(pan * 0.8f, -1f, 1f),
            decay = config.pipaDecay * Random.Range(0.75f, 1.25f),
            brightness = StrategyBrightness(Mathf.Lerp(0.35f, 0.9f, state.agitation)),
            tremolo = tremolo,
            bend = bend
        });
    }

    // ------------------------------------------------------------------ single events

    private void StrikeGong(RDMode mode, float pan, RDBehaviorState state)
    {
        engine.Play(new RDNoteEvent
        {
            kind = RDVoiceKind.Gong,
            frequency = config.RootFrequency,
            amplitude = config.gongLevel * (1f - state.fatigue * 0.25f) * StrategyResonance,
            pan = pan * 0.3f,
            decay = config.gongDecay,
            brightness = StrategyBrightness(Mathf.Lerp(0.3f, 0.8f, state.attention)),
            bend = config.gongBendSemitones
        });
    }

    private void StrikeTempleBell(RDMode mode, float pan, RDBehaviorState state, float amplitudeScale)
    {
        // The bell takes its pitch from the mode, an octave above the gong.
        int degree = Random.Range(0, Mathf.Max(1, mode.Count));
        float frequency = mode.Frequency(config.RootFrequency * 2f, degree);

        engine.Play(new RDNoteEvent
        {
            kind = RDVoiceKind.TempleBell,
            frequency = frequency,
            amplitude = config.templeBellLevel * amplitudeScale * (1f - state.fatigue * 0.2f) * StrategyResonance,
            pan = pan * 0.5f,
            decay = config.templeBellDecay * Random.Range(0.85f, 1.15f),
            brightness = StrategyBrightness(Mathf.Lerp(0.25f, 0.7f, state.attention))
        });
    }

    private void StrikeRin(RDMode mode, float pan, float amplitudeScale, float brightness)
    {
        int degree = Random.Range(2, Mathf.Max(3, mode.Count * 2));
        float frequency = mode.Frequency(config.RootFrequency * 4f, degree);

        engine.Play(new RDNoteEvent
        {
            kind = RDVoiceKind.Rin,
            frequency = frequency,
            amplitude = config.rinLevel * amplitudeScale * StrategyResonance,
            pan = pan,
            decay = config.rinDecay * Random.Range(0.8f, 1.2f),
            brightness = brightness
        });
    }

    /// <summary>A saccade gets its own accent, deliberately off the grid.</summary>
    private void HandleSaccade(RDBehaviorState state, RDMode mode, float pan)
    {
        bool jump = state.eyeJump;

        if (jump && !lastEyeJump)
        {
            // Familiarity blunts this: a machine that has seen thousands of eyes stops flinching.
            float startle = 1f - state.familiarity * 0.5f;
            StrikeRin(mode, pan, 0.8f * startle, 0.85f);
        }

        lastEyeJump = jump;
    }

    /// <summary>
    /// When the ring flees, the sound goes with it: an answer from the far side, then the pulse
    /// holds its breath until the ring comes back.
    /// </summary>
    private void HandleChaseTransition(RDBehaviorState state, RDMode mode, float pan)
    {
        bool chasing = controller.ChaseActive;

        if (chasing && !lastChaseActive)
            StrikeRin(mode, -pan, 0.65f, 0.95f);

        lastChaseActive = chasing;
    }

    // ------------------------------------------------------------------ empty room

    /// <summary>
    /// The empty room is a dead channel. The piece treats attention as signal, so with nobody
    /// looking there is nothing on the line: the static comes up, electronic artefacts punctuate
    /// it, and the waiting bell is still tolling underneath but no longer getting through.
    /// </summary>
    private void TickEmptyRoom(RDMode mode, float pan, RDBehaviorState state)
    {
        step = 0;
        stepTimer = 0f;

        if (!playWhenEmpty)
        {
            emptyDuration = 0f;
            engine.SetSignalNoise(0f, 0f);
            return;
        }

        emptyDuration += Time.deltaTime;

        // Crossfade in over the ramp, so a visitor stepping away for a moment does not trip it.
        float ramp = Mathf.Max(0.1f, config.signalRampSeconds);
        float arrived = Mathf.Clamp01(emptyDuration / ramp);

        engine.SetSignalNoise(config.signalNoiseLevel * arrived, config.signalUnrest);

        // ---- signalling ----

        signalTimer += Time.deltaTime;

        if (signalTimer >= nextSignalAt)
        {
            signalTimer = 0f;
            ScheduleNextSignal();

            if (config.signalLevel > 0.001f)
                PlaySignalBurst(arrived);
        }

        TickSignalQueue();

        // ---- electronic artefacts, off by default now ----

        glitchTimer += Time.deltaTime;

        if (glitchTimer >= nextGlitchAt)
        {
            glitchTimer = 0f;
            ScheduleNextGlitch();

            if (config.glitchLevel > 0.001f)
                PlayGlitch(arrived);
        }

        // ---- the bell, still going, no longer getting through ----

        idleBellTimer += Time.deltaTime;

        if (idleBellTimer >= nextIdleBellAt)
        {
            idleBellTimer = 0f;
            ScheduleNextIdleBell();

            // No bell. It used to toll here, scaled by Lerp(1, bellThroughStatic, arrived) - and
            // that lerp was the bug: taking the headset off resets emptyDuration, so arrived is
            // 0, so the scale evaluated to 1 and the bell rang at FULL strength exactly when the
            // room was supposed to be emptying. Setting bellThroughStatic to 0 changed nothing
            // because 0 was the far end of the ramp, not the near end.
            //
            // It is gone rather than quietened. An empty room is fans breathing and machines
            // calculating; a bell is addressed to someone, and there is no one here.
        }
    }

    // Machine pitches: a plain equal-tempered ladder, deliberately OUTSIDE the room's tuning.
    //
    // This is the opposite decision from the one the call made. A call belonged in the mode,
    // because the room calling in its own tuning read as the room looking for someone. Signal
    // tones must not belong: equipment reporting to equipment has nothing to do with the scale
    // the piece sings in, and putting it in the mode would make the machines sound like they
    // were grieving.
    //
    // Lowered and roughened from the first set, which ran up to 2637 Hz. Pure tones that high,
    // with a soft attack, are a glockenspiel - they read as a BELL, which is the one thing the
    // empty room must not contain. Down here, with the square edge the voice puts on them, they
    // read as a modem or a bus: data rather than music.
    private static readonly float[] SignalPitches =
        { 392f, 466.2f, 523.3f, 622.3f, 698.5f, 880f, 1046.5f };

    /// <summary>
    /// One burst of signalling, queued across the next fraction of a second.
    ///
    /// A burst rather than a single tone, and SEQUENTIAL rather than simultaneous: tones at once
    /// make a chord, which is music, while tones one after another make a handshake, which is
    /// traffic. The queue is what buys the gap between them - a few milliseconds is the whole
    /// difference between the two readings.
    /// </summary>
    private void PlaySignalBurst(float arrived)
    {
        int count = Random.Range(
            Mathf.Max(1, config.signalBurst.x),
            Mathf.Max(1, config.signalBurst.y) + 1);

        float delay = 0f;

        for (int i = 0; i < count && pendingSignals < PendingSignalCapacity; i++)
        {
            float pitch = SignalPitches[Random.Range(0, SignalPitches.Length)];

            signalDelay[pendingSignals] = delay;
            signalFrequency[pendingSignals] = pitch;
            signalAmplitude[pendingSignals] = config.signalLevel * arrived * Random.Range(0.6f, 1f);

            // Narrow pan: a rack is one place in the room, not spread across it.
            signalPan[pendingSignals] = Random.Range(-0.35f, 0.35f);
            signalChirp[pendingSignals] = Random.value < config.signalChirpChance
                ? Random.Range(-7f, 7f)
                : 0f;

            pendingSignals++;

            delay += Random.Range(
                Mathf.Min(config.signalGap.x, config.signalGap.y),
                Mathf.Max(config.signalGap.x, config.signalGap.y));
        }
    }

    /// <summary>Releases queued tones as their delays come due.</summary>
    private void TickSignalQueue()
    {
        int kept = 0;

        for (int i = 0; i < pendingSignals; i++)
        {
            signalDelay[i] -= Time.deltaTime;

            if (signalDelay[i] > 0f)
            {
                signalDelay[kept] = signalDelay[i];
                signalFrequency[kept] = signalFrequency[i];
                signalAmplitude[kept] = signalAmplitude[i];
                signalPan[kept] = signalPan[i];
                signalChirp[kept] = signalChirp[i];
                kept++;
                continue;
            }

            engine.Play(new RDNoteEvent
            {
                kind = RDVoiceKind.Signal,
                frequency = signalFrequency[i],
                amplitude = signalAmplitude[i],
                pan = signalPan[i],
                decay = Random.Range(0.05f, 0.26f),
                brightness = Random.Range(0.2f, 0.9f),
                bend = signalChirp[i]
            });
        }

        pendingSignals = kept;
    }

    /// <summary>
    /// One artefact. Pitches are off the mode on purpose: this is not the room singing, it is the
    /// machine making a noise at nobody, and it should not sound like it belongs.
    /// </summary>
    private void PlayGlitch(float arrived)
    {
        // A short ladder of machine-ish pitches, unrelated to the tuning the room is in.
        float[] steps = { 440f, 523.25f, 659.25f, 880f, 1174.66f, 1760f };
        float frequency = steps[Random.Range(0, steps.Length)] * Random.Range(0.98f, 1.02f);

        bool stutter = Random.value < 0.45f;

        engine.Play(new RDNoteEvent
        {
            kind = RDVoiceKind.Glitch,
            frequency = frequency,
            amplitude = config.glitchLevel * arrived * Random.Range(0.5f, 1f),
            pan = Random.Range(-0.9f, 0.9f),
            decay = stutter ? Random.Range(0.12f, 0.45f) : Random.Range(0.02f, 0.12f),
            brightness = Random.Range(0.15f, 1f),
            tremolo = stutter ? Random.Range(0.4f, 1f) : 0f,
            bend = Random.value < 0.4f ? Random.Range(-12f, 12f) : 0f,
            harmonic = Random.Range(0.2f, 1f)
        });
    }

    private void ScheduleNextIdleBell()
    {
        Vector2 range = config != null ? config.idleBellInterval : new Vector2(11f, 26f);
        nextIdleBellAt = Random.Range(Mathf.Min(range.x, range.y), Mathf.Max(range.x, range.y));
    }

    private void ScheduleNextSignal()
    {
        Vector2 range = config != null ? config.signalInterval : new Vector2(9f, 34f);
        nextSignalAt = Random.Range(Mathf.Min(range.x, range.y), Mathf.Max(range.x, range.y));
    }

    private void ScheduleNextGlitch()
    {
        Vector2 range = config != null ? config.glitchInterval : new Vector2(1.6f, 7f);
        nextGlitchAt = Random.Range(Mathf.Min(range.x, range.y), Mathf.Max(range.x, range.y));
    }

    // ------------------------------------------------------------------ helpers

    private float ComputeDrive(RDBehaviorState state)
    {
        float drive =
            state.attention * config.attentionDrive +
            state.agitation * config.agitationDrive +
            state.switchActivity * config.switchDrive;

        float weightSum = config.attentionDrive + config.agitationDrive + config.switchDrive;

        if (weightSum > 0.0001f)
            drive /= weightSum;

        return Mathf.Clamp01(drive);
    }

    /// <summary>
    /// Stereo position from the ring's azimuth, so a visitor hears the pattern answer from the
    /// direction they are looking, and hears it move when the ring evades them.
    /// </summary>
    private float ComputePan()
    {
        Vector3 dir = controller.RingDirection;

        if (dir.sqrMagnitude < 0.000001f)
            return 0f;

        // Azimuth in the shader's sphere space, mapped to -1..1.
        float azimuth = Mathf.Atan2(dir.z, dir.x);
        float pan = Mathf.Sin(azimuth);

        return pan * config.panFollowsRing;
    }

    private RDMode CurrentMode()
    {
        int index = mapper != null ? mapper.CurrentClassIndex : 0;
        return RDModes.ForClass(index);
    }

    // ------------------------------------------------------------------ strategy

    private bool HasStrategy => director != null && director.HasCatalogue;

    private float StrategyTempoScale()
    {
        if (!HasStrategy) return 1f;
        return Mathf.Lerp(strategyTempoScale.x, strategyTempoScale.y, Mathf.Clamp01(director.Current.tempo));
    }

    /// <summary>
    /// Instrument weight for the current strategy, floored so a strategy re-balances the ensemble
    /// rather than deleting parts of it. A room that drops to one voice reads as broken, not as
    /// a decision.
    /// </summary>
    private float Weight(float strategyValue) =>
        HasStrategy ? Mathf.Lerp(instrumentFloor, 1f, Mathf.Clamp01(strategyValue)) : 1f;

    private float StrategyPercussion => Weight(HasStrategy ? director.Current.percussion : 1f);
    private float StrategyResonance => Weight(HasStrategy ? director.Current.resonance : 1f);
    private float StrategyMelody => Weight(HasStrategy ? director.Current.melody : 1f);

    /// <summary>Blends the strategy's timbre toward the value the behaviour already asked for.</summary>
    private float StrategyBrightness(float behaviourBrightness) =>
        HasStrategy ? Mathf.Lerp(behaviourBrightness, director.Current.brightness, 0.6f) : behaviourBrightness;
}
