using UnityEngine;

[CreateAssetMenu(menuName = "RD/Music Config", fileName = "RDMusicConfig")]
public class RDMusicConfig : ScriptableObject
{
    [Header("Tuning")]
    [Tooltip("Root frequency of the whole piece, in Hz. 73.42 is D2, low enough for the gong to " +
             "sit under everything.")]
    public float rootHz = 73.42f;

    [Tooltip("Transpose the root, in semitones, without editing rootHz.")]
    [Range(-12f, 12f)] public float transpose = 0f;

    [Header("Pulse")]
    [Tooltip("Seconds per step when the room is calm and nobody is holding a gaze.")]
    public float slowStepSeconds = 1.35f;

    [Tooltip("Seconds per step at full attention and agitation.")]
    public float fastStepSeconds = 0.20f;

    [Tooltip("Steps in one colotomic cycle. The gong falls on step 0.")]
    [Range(4, 32)] public int cycleSteps = 16;

    [Header("Drive")]
    [Tooltip("How much sustained attention speeds the pulse.")]
    [Range(0f, 1f)] public float attentionDrive = 0.65f;

    [Tooltip("How much restless looking speeds the pulse.")]
    [Range(0f, 1f)] public float agitationDrive = 0.45f;

    [Tooltip("How much recent gaze switching speeds the pulse.")]
    [Range(0f, 1f)] public float switchDrive = 0.35f;

    [Header("Kendang")]
    [Tooltip("Chance of a drum stroke per step when nobody is looking.")]
    [Range(0f, 1f)] public float idleDrumChance = 0.04f;

    [Tooltip("Chance of a drum stroke per step at full drive.")]
    [Range(0f, 1f)] public float activeDrumChance = 0.78f;

    public float kendangLowHz = 96f;
    public float kendangHighHz = 210f;
    public float kendangDecay = 0.42f;

    [Header("Gong")]
    [Tooltip("Cycles between gong strokes while a visitor is present.")]
    [Range(1, 8)] public int gongEveryCycles = 1;

    public float gongDecay = 7.5f;
    public float gongBendSemitones = 0.7f;

    [Header("Temple Bell")]
    [Tooltip("Seconds between tolls when the room is empty. The machine waiting.")]
    public Vector2 idleBellInterval = new Vector2(11f, 26f);

    [Tooltip("Cycles between tolls while a visitor is present.")]
    [Range(1, 8)] public int bellEveryCycles = 2;

    public float templeBellDecay = 9f;
    public float rinDecay = 6f;

    [Header("Pipa")]
    [Tooltip("Attention above which the pipa enters. Below it, only percussion and bells.")]
    [Range(0f, 1f)] public float pipaEntryAttention = 0.35f;

    [Tooltip("Chance of a pipa note per step once it has entered.")]
    [Range(0f, 1f)] public float pipaDensity = 0.55f;

    public float pipaDecay = 2.6f;

    [Tooltip("Octaves above the root that the pipa line sits in.")]
    [Range(1, 4)] public int pipaOctave = 3;

    [Header("The Empty Room (a machine room)")]
    [Tooltip("With nobody looking the room is a data centre: fan fundamentals beating against " +
             "each other, mains hum, air in the ducts. Something very large that does not need " +
             "you. 0 disables the bed entirely.")]
    [Range(0f, 1f)] public float signalNoiseLevel = 0.62f;

    [Tooltip("How hard the room is working. Lifts the whirr and the air without touching the " +
             "rumble, so a busier room gets brighter rather than louder.")]
    [Range(0f, 1f)] public float signalUnrest = 0.4f;

    [Tooltip("Seconds of emptiness before the machine room has fully arrived. Long on purpose: " +
             "the room going quiet is something the visitor should be able to watch happen.")]
    public float signalRampSeconds = 90f;

    [Header("Signal Tones")]
    [Tooltip("Seconds between bursts while the room is empty. Machines signalling to other " +
             "machines - telemetry, a handshake, a scan - not calling for anyone.")]
    public Vector2 signalInterval = new Vector2(9f, 34f);

    [Range(0f, 1f)] public float signalLevel = 0.30f;

    [Tooltip("Tones in a burst. One reads as a ping, two or three as a handshake.")]
    public Vector2Int signalBurst = new Vector2Int(1, 3);

    [Tooltip("Seconds between the tones of a burst.")]
    public Vector2 signalGap = new Vector2(0.09f, 0.22f);

    [Tooltip("Chance a tone chirps - a quick sweep over about 50 ms, as though something is " +
             "syncing rather than just reporting.")]
    [Range(0f, 1f)] public float signalChirpChance = 0.35f;

    [Tooltip("Seconds between electronic artefacts while the room is empty. 0 level by default: " +
             "a machine room does not glitch, and the call carries this now.")]
    public Vector2 glitchInterval = new Vector2(6f, 20f);

    [Tooltip("Scale on the waiting temple bell while the room is empty. 0, and it should stay 0: " +
             "a bell is a ritual addressed to someone, and the empty room is machines addressing " +
             "machines. The bell belongs to the part of the piece that has a visitor in it.")]
    [Range(0f, 1f)] public float bellThroughStatic = 0f;

    [Header("Levels")]
    [Range(0f, 1f)] public float templeBellLevel = 0.85f;
    [Range(0f, 1f)] public float rinLevel = 0.5f;
    [Range(0f, 1f)] public float kendangLevel = 0.62f;
    [Range(0f, 1f)] public float gongLevel = 0.9f;
    [Range(0f, 1f)] public float pipaLevel = 0.44f;
    [Range(0f, 1f)] public float glitchLevel = 0f;

    [Header("Spatialisation")]
    [Tooltip("How far the sound follows the ring around the sphere. 0 = always centred.")]
    [Range(0f, 1f)] public float panFollowsRing = 0.8f;

    public float RootFrequency => rootHz * Mathf.Pow(2f, transpose / 12f);
}
