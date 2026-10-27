using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Minimal on-screen readout for whoever is guarding the show: is it tracking, is anyone present,
/// how much gaze has accumulated, what the surface is currently tuned to. Hidden by default and
/// toggled with a key, so it never appears in front of a visitor.
/// </summary>
public class RDOperatorOverlay : MonoBehaviour
{
    [SerializeField] private RDPresenceTracker presenceTracker;
    [SerializeField] private RDInputSourceRouter router;
    [SerializeField] private LinearBehaviorMapper mapper;
    [SerializeField] private RDGreedyDirector director;

    [Tooltip("Also list every strategy's learned value. Useful for the daily log.")]
    [SerializeField] private bool showStrategyTable = true;

    [SerializeField] private Key toggleKey = Key.F1;

    [Tooltip("Starts eye-tracking calibration for the visitor wearing the headset. Needed once " +
             "per visitor: gaze reports nothing until it has been fitted to their eyes.")]
    [SerializeField] private Key calibrateKey = Key.F2;
    [SerializeField] private bool visibleOnStart = false;
    [SerializeField] private int fontSize = 18;

    private bool visible;
    private GUIStyle style;

    private void Awake() => visible = visibleOnStart;

    private void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) return;

        if (keyboard[toggleKey].wasPressedThisFrame)
            visible = !visible;

        // F2 always says something. A key that silently does nothing is indistinguishable from
        // a key that is not wired, and the operator is standing next to a visitor waiting.
        if (keyboard[calibrateKey].wasPressedThisFrame)
        {
            if (router == null)
                Debug.LogError("[RDOperatorOverlay] No router assigned, so F2 cannot reach the " +
                               "gaze input.");
            else if (!router.CanCalibrate)
                Debug.LogError("[RDOperatorOverlay] The preferred input source cannot " +
                               "calibrate. Check that the router's Preferred Source is the " +
                               "VarjoGazeInput.");
            else
                router.RequestCalibration();
        }
    }

    private void OnGUI()
    {
        if (!visible) return;

        if (style == null)
        {
            style = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                wordWrap = false
            };
            style.normal.textColor = Color.white;
        }

        string text = "RD exhibition status\n";

        // Why, not just what: "input: MouseInput" on show night means the headset lost gaze, and
        // the operator needs the reason - uncalibrated, not allowed, not worn - at the machine.
        string trackingLine = "";

        if (router != null)
        {
            text += "input: " + router.ActiveSourceName + "\n";

            trackingLine = router.TrackingStatus;

            if (!string.IsNullOrEmpty(trackingLine))
                text += "tracking: " + trackingLine + "\n";
        }

        if (presenceTracker != null)
            text += presenceTracker.StatusLine + "\n";

        if (mapper != null)
            text += "pearson class: " + mapper.CurrentClassLabel + "\n";

        if (director != null && director.HasCatalogue)
        {
            text += string.Format("strategy: {0}  |  epsilon {1:0.000}  |  impressions {2}  |  last reward {3:0.00}\n",
                director.CurrentLabel, director.Epsilon, director.TotalImpressions, director.LastReward);

            if (showStrategyTable)
                text += director.DescribeValues() + "\n";
        }

        text += "fps: " + (1f / Mathf.Max(0.0001f, Time.smoothDeltaTime)).ToString("0") + "\n";
        text += "[" + toggleKey + "] hide";

        if (router != null && router.CanCalibrate)
            text += "   [" + calibrateKey + "] calibrate visitor";

        float height = 152f + (director != null && showStrategyTable ? 190f : 30f)
                       + (string.IsNullOrEmpty(trackingLine) ? 0f : 18f);

        GUI.Box(new Rect(12, 12, 640, height), GUIContent.none);
        GUI.Label(new Rect(22, 18, 620, height - 10f), text, style);
    }
}
