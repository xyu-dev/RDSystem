using UnityEngine;

/// <summary>
/// One strategy the room can adopt: a complete aesthetic position, visual and sonic at once.
///
/// In recommender terms this is an <i>arm</i> - one of the options the bandit chooses between and
/// learns the value of. It is deliberately a whole coherent look-and-sound rather than a single
/// slider, because a platform does not optimise "saturation": it optimises which <i>kind of thing</i>
/// to show you next.
/// </summary>
[System.Serializable]
public struct RDStrategy
{
    public string label;

    [Header("Visual")]
    [Tooltip("Which Pearson reaction-diffusion class this strategy pulls the pattern toward.")]
    [Range(0, 6)] public int favouredClass;

    [Tooltip("0 = a tight ring right where you look. 1 = a wide bloom across the sphere.")]
    [Range(0f, 1f)] public float ringWidth;

    [Tooltip("How readily the ring flees the gaze. 0 = never evades, 1 = evades constantly.")]
    [Range(0f, 1f)] public float evasion;

    [Tooltip("Feed bias. Positive grows the pattern faster, negative holds it back.")]
    [Range(-1f, 1f)] public float growth;

    [Header("Sonic")]
    [Tooltip("Pace bias. 0 = slower than the gaze suggests, 1 = faster.")]
    [Range(0f, 1f)] public float tempo;

    [Tooltip("Weight of the kendang.")]
    [Range(0f, 1f)] public float percussion;

    [Tooltip("Weight of the bells and gong.")]
    [Range(0f, 1f)] public float resonance;

    [Tooltip("Weight of the pipa.")]
    [Range(0f, 1f)] public float melody;

    [Tooltip("Timbre. 0 = dark and padded, 1 = hard and bright.")]
    [Range(0f, 1f)] public float brightness;
}
