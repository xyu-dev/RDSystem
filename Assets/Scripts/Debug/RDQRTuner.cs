using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Play-mode sliders for the QR display shader, so the look can be found while the simulation
/// is actually running and reacting to gaze. A static inspector is no use here: the pattern only
/// shows what it is once it is moving.
///
/// It writes to sharedMaterial on purpose - the values stick to the material asset after you
/// leave Play mode, which is the whole reason to tune this way rather than guessing.
/// </summary>
public class RDQRTuner : MonoBehaviour
{
    [SerializeField] private Renderer target;

    [SerializeField] private Key toggleKey = Key.F2;
    [SerializeField] private bool visibleOnStart = true;

    private bool visible;
    private Material material;
    private GUIStyle labelStyle;

    private void Awake()
    {
        visible = visibleOnStart;

        if (target == null)
            target = GetComponent<Renderer>();

        if (target != null)
            material = target.sharedMaterial;

        if (material == null)
            Debug.LogWarning("[RDQRTuner] No material found; assign a Renderer that uses RDSystem/RDQRDisplay.");
    }

    private void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) return;

        if (keyboard[toggleKey].wasPressedThisFrame)
            visible = !visible;
    }

    private void OnGUI()
    {
        if (!visible || material == null) return;

        if (labelStyle == null)
        {
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = false };
            labelStyle.normal.textColor = Color.white;
        }

        const float w = 330f;
        const float h = 300f;

        GUI.Box(new Rect(Screen.width - w - 12f, 12f, w, h), GUIContent.none);

        GUILayout.BeginArea(new Rect(Screen.width - w - 2f, 20f, w - 20f, h - 16f));

        GUILayout.Label("QR display  [" + toggleKey + "] hide", labelStyle);

        Slider("Patches Around (U)", "_PatchesX", 1f, 24f, true);
        Slider("Patches Over (V)", "_PatchesY", 1f, 12f, true);
        Slider("Modules / Patch", "_ModulesPerPatch", 21f, 77f, true);
        Slider("Quiet Zone", "_QuietZone", 0f, 0.24f, false);
        Slider("Gain", "_Gain", 0f, 4f, false);
        Slider("Threshold", "_Threshold", 0f, 1f, false);
        Slider("Dither", "_Dither", 0f, 1f, false);
        Slider("Mask (8 = off)", "_MaskPattern", 0f, 8f, true);
        Slider("Module Gap", "_ModuleGap", 0f, 0.4f, false);

        GUILayout.Space(4f);

        if (material.HasProperty("_ShowStructureOnly"))
        {
            bool structure = material.GetFloat("_ShowStructureOnly") > 0.5f;
            bool next = GUILayout.Toggle(structure, "Show structure only");
            if (next != structure)
                material.SetFloat("_ShowStructureOnly", next ? 1f : 0f);
        }

        GUILayout.EndArea();
    }

    private void Slider(string label, string property, float min, float max, bool integer)
    {
        if (!material.HasProperty(property)) return;

        float value = material.GetFloat(property);

        GUILayout.BeginHorizontal();
        GUILayout.Label(label + "  " + (integer ? value.ToString("0") : value.ToString("0.00")),
            labelStyle, GUILayout.Width(150f));

        float next = GUILayout.HorizontalSlider(value, min, max);
        GUILayout.EndHorizontal();

        if (integer)
            next = Mathf.Round(next);

        if (!Mathf.Approximately(next, value))
            material.SetFloat(property, next);
    }
}
