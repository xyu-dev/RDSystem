using UnityEngine;

/// <summary>
/// One scene, two ways in.
///
/// The exhibition runs on the Varjo headset; development right now runs on a mouse. Instead of
/// maintaining two scenes that drift apart, everything downstream (controller, state layer,
/// mapper, conductor) talks to this router, and the router picks whichever source is actually
/// alive. If the headset is absent, uncalibrated, or drops out mid-show, the piece falls back to
/// the pointer instead of freezing.
/// </summary>
public class RDInputSourceRouter : MonoBehaviour,
    IInteractionSignalSource, IInteractionUVSource, IInteractionHealthSource
{
    public enum SourceSelection
    {
        /// <summary>Use the preferred source when it is tracking, otherwise the fallback.</summary>
        Automatic,
        ForcePreferred,
        ForceFallback
    }

    [Header("Sources")]
    [Tooltip("Normally VarjoGazeInput.")]
    [SerializeField] private MonoBehaviour preferredSource;

    [Tooltip("Normally MouseInput.")]
    [SerializeField] private MonoBehaviour fallbackSource;

    [Header("Selection")]
    [SerializeField] private SourceSelection selection = SourceSelection.Automatic;

    [Tooltip("Seconds the preferred source must stay healthy before the router switches back to " +
             "it. Stops the piece flapping between sources while a headset is being fitted.")]
    [SerializeField] private float switchBackDelay = 2.5f;

    [Tooltip("Disable the inactive source's component so it stops raycasting.")]
    [SerializeField] private bool disableInactiveSource = true;

    [Header("Read-only")]
    [SerializeField] private string debugActiveSource = "";

    public string ActiveSourceName => debugActiveSource;

    private IInteractionSignalSource preferredSignal;
    private IInteractionUVSource preferredUV;
    private IInteractionHealthSource preferredHealth;

    private IInteractionSignalSource fallbackSignal;
    private IInteractionUVSource fallbackUV;
    private IInteractionHealthSource fallbackHealth;

    private bool usingPreferred;
    private float preferredHealthyFor;

    // Switching source must not look like a gaze event to the state layer, so the router keeps
    // its own switch counter and only ever advances it.
    private int routedSwitchCount;
    private int lastSourceSwitchCount;
    private bool switchBaselineSet;
    private IInteractionSignalSource baselineSource;

    private void Awake()
    {
        preferredSignal = preferredSource as IInteractionSignalSource;
        preferredUV = preferredSource as IInteractionUVSource;
        preferredHealth = preferredSource as IInteractionHealthSource;

        fallbackSignal = fallbackSource as IInteractionSignalSource;
        fallbackUV = fallbackSource as IInteractionUVSource;
        fallbackHealth = fallbackSource as IInteractionHealthSource;

        if (preferredSignal == null && fallbackSignal == null)
            Debug.LogError("[RDInputSourceRouter] No usable input source assigned.");

        usingPreferred = preferredSignal != null && selection != SourceSelection.ForceFallback;
        ApplyEnabledState();
    }

    private void Update()
    {
        ChooseSource();
        ApplyEnabledState();
        AdvanceSwitchCount();

        debugActiveSource = usingPreferred
            ? (preferredSource != null ? preferredSource.GetType().Name : "none")
            : (fallbackSource != null ? fallbackSource.GetType().Name : "none");
    }

    private void ChooseSource()
    {
        if (selection == SourceSelection.ForcePreferred)
        {
            usingPreferred = preferredSignal != null;
            return;
        }

        if (selection == SourceSelection.ForceFallback)
        {
            usingPreferred = false;
            return;
        }

        if (preferredSignal == null)
        {
            usingPreferred = false;
            return;
        }

        bool preferredOk = preferredHealth == null || preferredHealth.IsTracking;

        if (preferredOk)
            preferredHealthyFor += Time.deltaTime;
        else
            preferredHealthyFor = 0f;

        if (usingPreferred)
        {
            // Leave immediately when the headset stops delivering: a dead room is the worst state.
            if (!preferredOk && fallbackSignal != null)
                usingPreferred = false;
        }
        else
        {
            if (preferredOk && preferredHealthyFor >= switchBackDelay)
                usingPreferred = true;
        }
    }

    private void ApplyEnabledState()
    {
        if (!disableInactiveSource) return;

        if (preferredSource != null)
            preferredSource.enabled = usingPreferred;

        if (fallbackSource != null)
            fallbackSource.enabled = !usingPreferred;
    }

    private void AdvanceSwitchCount()
    {
        IInteractionSignalSource active = ActiveSignal;
        if (active == null) return;

        int sourceCount = active.SwitchCount;

        // Each source keeps its own lifetime counter, and the two are unrelated numbers. Comparing
        // across a source change produces a meaningless delta in EITHER direction: handing over
        // from a headset on 3 to a mouse on 50 would inject 47 phantom saccades into the state
        // layer, which is exactly the burst that makes the room look agitated for no reason.
        // So rebase on the new source and count nothing for the frame of the handover.
        if (!switchBaselineSet || !ReferenceEquals(active, baselineSource))
        {
            baselineSource = active;
            lastSourceSwitchCount = sourceCount;
            switchBaselineSet = true;
            return;
        }

        int delta = sourceCount - lastSourceSwitchCount;
        lastSourceSwitchCount = sourceCount;

        if (delta > 0)
            routedSwitchCount += delta;
    }

    private IInteractionSignalSource ActiveSignal => usingPreferred ? preferredSignal : fallbackSignal;
    private IInteractionUVSource ActiveUV => usingPreferred ? preferredUV : fallbackUV;
    private IInteractionHealthSource ActiveHealth => usingPreferred ? preferredHealth : fallbackHealth;

    // ------------------------------------------------------------ forwarded interface

    public bool IsGazing => ActiveSignal != null && ActiveSignal.IsGazing;
    public float GazeDuration => ActiveSignal != null ? ActiveSignal.GazeDuration : 0f;
    public float NoGazeDuration => ActiveSignal != null ? ActiveSignal.NoGazeDuration : 0f;
    public int SwitchCount => routedSwitchCount;
    public float HeadMotion => ActiveSignal != null ? ActiveSignal.HeadMotion : 0f;
    public bool EyeJump => ActiveSignal != null && ActiveSignal.EyeJump;
    public Vector3 InteractionDirection => ActiveSignal != null ? ActiveSignal.InteractionDirection : Vector3.forward;

    public Vector2 CurrentUV => ActiveUV != null ? ActiveUV.CurrentUV : Vector2.zero;

    public bool IsTracking => ActiveHealth == null || ActiveHealth.IsTracking;

    public string TrackingStatus
    {
        get
        {
            if (ActiveHealth == null) return "";

            string status = ActiveHealth.TrackingStatus;

            if (!usingPreferred && preferredHealth != null && !preferredHealth.IsTracking)
            {
                string reason = preferredHealth.TrackingStatus;
                return string.IsNullOrEmpty(reason)
                    ? "running on fallback input"
                    : "running on fallback input (" + reason + ")";
            }

            return status;
        }
    }
}
