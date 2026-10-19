using UnityEngine;

/// <summary>
/// The instrument families the engine synthesises. Nothing is sampled: every voice is built from
/// a physical or spectral model, so the reaction-diffusion parameters can move continuously
/// through the timbre instead of triggering fixed recordings.
/// </summary>
public enum RDVoiceKind
{
    /// <summary>Bonsho. Large bronze temple bell: low hum tone, inharmonic partials, long decay, audible throb.</summary>
    TempleBell,

    /// <summary>Rin. Small bowl bell struck on the rim: high, nearly pure, very long sustain and beating.</summary>
    Rin,

    /// <summary>Kendang. Indonesian two-headed hand drum: noise slap over tuned membrane modes.</summary>
    Kendang,

    /// <summary>Gong ageng / kempul. Low bronze gong: slow bloom, shimmering upper partials.</summary>
    Gong,

    /// <summary>Pipa. Plucked four-string lute, modelled additively, with tremolo and slides.</summary>
    Pipa,

    /// <summary>
    /// Not an instrument. Short electronic artefacts - blips, stutters, decimated squares - for
    /// the empty room, where the piece has nobody to transmit to.
    /// </summary>
    Glitch,

    /// <summary>
    /// A machine signalling - telemetry, a handshake, a scan. What the empty room emits when
    /// nobody is looking: equipment addressing other equipment, not anyone.
    /// </summary>
    Signal
}

/// <summary>
/// One note, handed from the composer on the main thread to the synthesiser on the audio thread.
/// A struct, so queueing it allocates nothing.
/// </summary>
public struct RDNoteEvent
{
    public RDVoiceKind kind;

    /// <summary>Hertz.</summary>
    public float frequency;

    /// <summary>Linear gain, 0..1.</summary>
    public float amplitude;

    /// <summary>Stereo position, -1 left to +1 right.</summary>
    public float pan;

    /// <summary>Decay time in seconds to roughly -60 dB.</summary>
    public float decay;

    /// <summary>0 = dark and soft, 1 = bright and hard. Maps to mallet hardness / plectrum attack.</summary>
    public float brightness;

    /// <summary>
    /// Modulation depth, 0..1. The pipa reads it as tremolo depth (lunzhi), the glitch voice as
    /// stutter rate. Ignored by the other voices.
    /// </summary>
    public float tremolo;

    /// <summary>Pitch glide in semitones applied over the first part of the note. Pipa and gong.</summary>
    public float bend;

    /// <summary>Glitch bit-crush depth, 0..1. Ignored by the instrument voices.</summary>
    public float harmonic;
}

/// <summary>
/// A tuning, stored as semitone offsets from the root so non-equal-tempered systems survive.
/// Slendro and pelog are deliberately not rounded to the piano: their stretched, uneven steps are
/// most of what makes gamelan sound like gamelan.
/// </summary>
[System.Serializable]
public struct RDMode
{
    public string name;
    public float[] semitones;

    public RDMode(string name, params float[] semitones)
    {
        this.name = name;
        this.semitones = semitones;
    }

    public int Count => semitones != null ? semitones.Length : 0;

    /// <summary>
    /// Frequency of the given scale degree, wrapping into octaves above and below the root.
    /// </summary>
    public float Frequency(float rootHz, int degree)
    {
        if (semitones == null || semitones.Length == 0)
            return rootHz;

        int count = semitones.Length;

        int octave = Mathf.FloorToInt(degree / (float)count);
        int index = degree - octave * count;

        if (index < 0) index += count;
        if (index >= count) index -= count;

        float offset = semitones[index] + octave * 12f;
        return rootHz * Mathf.Pow(2f, offset / 12f);
    }
}

/// <summary>
/// The seven tunings, one per Pearson class. The pattern the visitor's gaze grows on the sphere
/// chooses which of these the room is currently singing in, so the reaction-diffusion class is
/// audible as a change of mode rather than just a change of texture.
/// </summary>
public static class RDModes
{
    // Slendro and pelog offsets follow commonly cited cent measurements of Javanese gamelan
    // tunings; every set is an approximation, because no two gamelan are tuned alike.
    public static readonly RDMode Slendro = new RDMode("slendro", 0f, 2.31f, 4.74f, 7.17f, 9.55f);
    public static readonly RDMode PelogBem = new RDMode("pelog bem", 0f, 1.20f, 2.58f, 6.75f, 7.85f);
    public static readonly RDMode PelogBarang = new RDMode("pelog barang", 0f, 2.58f, 5.39f, 6.75f, 9.42f);

    public static readonly RDMode Hirajoshi = new RDMode("hirajoshi", 0f, 2f, 3f, 7f, 8f);
    public static readonly RDMode InSen = new RDMode("in sen", 0f, 1f, 5f, 7f, 10f);
    public static readonly RDMode Yo = new RDMode("yo", 0f, 2f, 5f, 7f, 9f);
    public static readonly RDMode Zhi = new RDMode("zhi", 0f, 2f, 5f, 7f, 10f);

    /// <summary>
    /// Ordered to match LinearBehaviorMapper's class list: epsilon, zeta, lambda, delta, theta,
    /// kappa, pi. The order matters musically: the low-feed classes get the calm pentatonics and
    /// the high-feed classes get the more unstable pelog tunings.
    /// </summary>
    public static readonly RDMode[] ByPearsonClass =
    {
        Hirajoshi,    // epsilon
        Slendro,      // zeta
        Yo,           // lambda
        InSen,        // delta
        Zhi,          // theta
        PelogBem,     // kappa
        PelogBarang   // pi
    };

    public static RDMode ForClass(int index)
    {
        if (ByPearsonClass.Length == 0) return Slendro;

        int i = index % ByPearsonClass.Length;
        if (i < 0) i += ByPearsonClass.Length;

        return ByPearsonClass[i];
    }
}
