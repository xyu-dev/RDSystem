using UnityEngine;

/// <summary>
/// The set of strategies the machine chooses between. Editing this asset changes what the room is
/// able to become; it does not change what it learns to prefer.
///
/// In its own file, and that is not a formality. Unity only creates a MonoScript for the type
/// whose name matches the file name, so while this class lived in RDStrategy.cs beside the
/// struct it is named after, no MonoScript existed for it - and an asset with no MonoScript is
/// written with "m_Script: {fileID: 0}" and will never load as its own class again.
///
/// That is the whole of the bug that kept returning as "No strategy catalogue assigned": every
/// repair wrote a reference that could not resolve, and every recreate produced another asset
/// with the same empty field. Renaming the file is the fix; nothing in the asset was ever wrong.
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
