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

/// <summary>
/// The set of strategies the machine chooses between. Editing this asset changes what the room is
/// able to become; it does not change what it learns to prefer.
/// </summary>
[CreateAssetMenu(menuName = "RD/Strategy Catalogue", fileName = "RDStrategyCatalogue")]
public class RDStrategyCatalogue : ScriptableObject
{
    [SerializeField]
    private RDStrategy[] strategies = new RDStrategy[]
    {
        new RDStrategy { label = "hush",      favouredClass = 0, ringWidth = 0.15f, evasion = 0.05f, growth = -0.4f,
                         tempo = 0.10f, percussion = 0.10f, resonance = 0.95f, melody = 0.05f, brightness = 0.20f },

        new RDStrategy { label = "tide",      favouredClass = 1, ringWidth = 0.55f, evasion = 0.10f, growth = -0.1f,
                         tempo = 0.25f, percussion = 0.30f, resonance = 0.80f, melody = 0.20f, brightness = 0.30f },

        new RDStrategy { label = "procession",favouredClass = 2, ringWidth = 0.40f, evasion = 0.15f, growth = 0.0f,
                         tempo = 0.45f, percussion = 0.70f, resonance = 0.60f, melody = 0.30f, brightness = 0.45f },

        new RDStrategy { label = "lament",    favouredClass = 3, ringWidth = 0.30f, evasion = 0.30f, growth = -0.2f,
                         tempo = 0.30f, percussion = 0.25f, resonance = 0.70f, melody = 0.85f, brightness = 0.35f },

        new RDStrategy { label = "bloom",     favouredClass = 4, ringWidth = 0.80f, evasion = 0.10f, growth = 0.6f,
                         tempo = 0.55f, percussion = 0.45f, resonance = 0.55f, melody = 0.60f, brightness = 0.60f },

        new RDStrategy { label = "swarm",     favouredClass = 5, ringWidth = 0.65f, evasion = 0.45f, growth = 0.8f,
                         tempo = 0.80f, percussion = 0.90f, resonance = 0.35f, melody = 0.50f, brightness = 0.80f },

        new RDStrategy { label = "chase",     favouredClass = 6, ringWidth = 0.25f, evasion = 0.90f, growth = 0.5f,
                         tempo = 0.90f, percussion = 0.80f, resonance = 0.30f, melody = 0.70f, brightness = 0.90f },

        new RDStrategy { label = "refusal",   favouredClass = 0, ringWidth = 0.10f, evasion = 0.95f, growth = -0.7f,
                         tempo = 0.15f, percussion = 0.05f, resonance = 0.40f, melody = 0.05f, brightness = 0.15f },
    };

    public int Count => strategies != null ? strategies.Length : 0;

    public RDStrategy Get(int index)
    {
        if (strategies == null || strategies.Length == 0)
            return default;

        int i = index % strategies.Length;
        if (i < 0) i += strategies.Length;

        return strategies[i];
    }

    public string LabelOf(int index) => Count == 0 ? "" : Get(index).label;
}
