using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Readout for the two progression test scenes: which level, how much of the sphere is resolved,
/// how far the shell stack has grown. Shows whichever of the two is present.
///
/// F3 hides it. R resets whichever mechanic is in the scene, which is what you want when testing
/// a loop that otherwise takes minutes to come back round.
/// </summary>
public class RDProgressionHUD : MonoBehaviour
{
    [SerializeField] private RDResolveProgression resolve;
    [SerializeField] private RDShellStack shells;

    [SerializeField] private Key toggleKey = Key.F3;
    [SerializeField] private Key resetKey = Key.R;
    [SerializeField] private bool visibleOnStart = true;
    [SerializeField] private int fontSize = 16;

    private bool visible;
    private GUIStyle style;

    private void Awake()
    {
        visible = visibleOnStart;

        if (resolve == null) resolve = FindFirstObjectByType<RDResolveProgression>();
        if (shells == null) shells = FindFirstObjectByType<RDShellStack>();
    }

    private void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) return;

        if (keyboard[toggleKey].wasPressedThisFrame)
            visible = !visible;

        if (keyboard[resetKey].wasPressedThisFrame)
        {
            if (resolve != null) resolve.ResetProgression();
            if (shells != null) shells.ResetStack();
        }
    }

    private void OnGUI()
    {
        if (!visible) return;

        if (style == null)
        {
            style = new GUIStyle(GUI.skin.label) { fontSize = fontSize, wordWrap = false };
            style.normal.textColor = Color.white;
        }

        string text = "";

        if (resolve != null)
        {
            text += string.Format("level {0} of {1}   cells/face {2}   ({3} around)\n",
                resolve.Level + 1, resolve.LevelCount, resolve.CellsPerFace, resolve.CellsPerFace * 4);

            text += string.Format("resolved {0:0.0}%{1}\n",
                resolve.Coverage * 100f, resolve.Completing ? "   -- complete, holding --" : "");

            if (resolve.AtFinestLevel)
                text += "finest level the simulation buffer can feed\n";
        }

        if (shells != null)
        {
            text += string.Format("shells {0}   newest radius {1:0.0} m   born {2}\n",
                shells.ShellCount, shells.NewestRadius, shells.TotalBorn);
        }

        text += string.Format("fps {0:0}\n", 1f / Mathf.Max(0.0001f, Time.smoothDeltaTime));
        text += "[" + toggleKey + "] hide    [" + resetKey + "] reset";

        float height = 48f + (resolve != null ? 64f : 0f) + (shells != null ? 22f : 0f);

        GUI.Box(new Rect(12, 12, 540, height), GUIContent.none);
        GUI.Label(new Rect(22, 18, 520, height - 10f), text, style);
    }
}
