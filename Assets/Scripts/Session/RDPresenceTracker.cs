using UnityEngine;

/// <summary>
/// Decides when one visitor's encounter ends and the next begins, and keeps the piece alive
/// when the tracker is not.
///
/// Fixes the gallery failure mode where a headset is handed to a new visitor, eye tracking is
/// uncalibrated, VarjoGazeInput silently reports nothing, and the room stays frozen with no
/// indication to the person guarding the show.
/// </summary>
public class RDPresenceTracker : MonoBehaviour
{
    public enum PresenceState
    {
        Empty,
        Present,
        Untracked
    }

    [Header("References")]
    [SerializeField] private MonoBehaviour signalSourceBehaviour;
    [SerializeField] private RDBehaviorStateLayer stateLayer;
    [SerializeField] private RDLongTermMemory longTermMemory;

    [Header("Session Boundaries")]
    [Tooltip("Seconds of no interaction before the current visitor is considered gone.")]
    [SerializeField] private float absenceBeforeSessionEnd = 25f;

    [Tooltip("Seconds of interaction before an encounter counts as a real visitor.")]
    [SerializeField] private float presenceBeforeSessionStart = 1.5f;

    [Header("On Session End")]
    [Tooltip("Wipe the short-term reading of the visitor (attention, agitation, switching).")]
    [SerializeField] private bool resetShortTermState = true;

    [Header("Tracking Health")]
    [Tooltip("Seconds the source may report no tracking before the piece flags a problem.")]
    [SerializeField] private float untrackedGraceTime = 8f;

    [Header("Events")]
    public UnityEngine.Events.UnityEvent onSessionStart;
    public UnityEngine.Events.UnityEvent onSessionEnd;
    public UnityEngine.Events.UnityEvent onTrackingLost;
    public UnityEngine.Events.UnityEvent onTrackingRestored;

    [Header("Read-only")]
    [SerializeField] private PresenceState debugState = PresenceState.Empty;
    [SerializeField] private float debugSessionLength;

    [Tooltip("Seconds of this session actually spent looking, as opposed to elapsed. This is the " +
             "number that means anything about a visit.")]
    [SerializeField] private float debugAttendedSeconds;

    public PresenceState State => debugState;
    public float CurrentSessionLength => debugSessionLength;

    /// <summary>
    /// Seconds of the current session spent in contact, not elapsed.
    ///
    /// CurrentSessionLength is wall clock from the moment the session opened, and the session
    /// stays open through 25 seconds of absence - so it keeps counting while the visitor is
    /// blinking, adjusting the headset, being recalibrated or standing still doing nothing. For
    /// "how long did they look at it", that number is always too large and sometimes wildly so.
    /// </summary>
    public float AttendedSeconds => debugAttendedSeconds;

    /// <summary>Attended seconds of the session that just ended.</summary>
    public float LastAttendedSeconds { get; private set; }

    /// <summary>
    /// How long the session that just ended ran, in seconds.
    ///
    /// Needed because CloseSession zeroes the running length BEFORE it raises onSessionEnd, so
    /// anything listening for the end of a visit - the receipt, a log - reads zero if it asks
    /// for the current one.
    /// </summary>
    public float LastSessionLength { get; private set; }
    public string StatusLine { get; private set; } = "";

    private IInteractionSignalSource signalSource;
    private IInteractionHealthSource healthSource;

    private bool sessionOpen;
    private float contactTimer;
    private float absenceTimer;
    private float untrackedTimer;
    private bool trackingFlagged;

    private void Awake()
    {
        if (stateLayer == null)
            stateLayer = GetComponent<RDBehaviorStateLayer>();

        signalSource = signalSourceBehaviour as IInteractionSignalSource;
        healthSource = signalSourceBehaviour as IInteractionHealthSource;

        if (signalSource == null)
            Debug.LogError("[RDPresenceTracker] signalSourceBehaviour must implement IInteractionSignalSource.");
    }

    private void Update()
    {
        if (signalSource == null) return;

        float dt = Time.deltaTime;

        UpdateTrackingHealth(dt);

        bool inContact = signalSource.IsGazing;

        if (inContact)
        {
            contactTimer += dt;
            absenceTimer = 0f;

            if (sessionOpen) debugAttendedSeconds += dt;

            if (longTermMemory != null)
                longTermMemory.AccumulateGaze(dt);

            if (!sessionOpen && contactTimer >= presenceBeforeSessionStart)
                OpenSession();
        }
        else
        {
            contactTimer = 0f;
            absenceTimer += dt;

            if (longTermMemory != null)
                longTermMemory.Rest(dt);

            if (sessionOpen && absenceTimer >= absenceBeforeSessionEnd)
                CloseSession();
        }

        if (sessionOpen)
            debugSessionLength += dt;

        debugState = trackingFlagged
            ? PresenceState.Untracked
            : (sessionOpen ? PresenceState.Present : PresenceState.Empty);

        BuildStatusLine();
    }

    private void UpdateTrackingHealth(float dt)
    {
        if (healthSource == null)
        {
            trackingFlagged = false;
            return;
        }

        if (healthSource.IsTracking)
        {
            untrackedTimer = 0f;

            if (trackingFlagged)
            {
                trackingFlagged = false;
                onTrackingRestored?.Invoke();
            }

            return;
        }

        untrackedTimer += dt;

        if (!trackingFlagged && untrackedTimer >= untrackedGraceTime)
        {
            trackingFlagged = true;
            onTrackingLost?.Invoke();
            Debug.LogWarning("[RDPresenceTracker] Input source not tracking: " + healthSource.TrackingStatus);
        }
    }

    private void OpenSession()
    {
        sessionOpen = true;
        debugSessionLength = contactTimer;

        // The contact that opened the session was real looking, so it counts.
        debugAttendedSeconds = contactTimer;

        if (longTermMemory != null)
            longTermMemory.RegisterVisitor();

        onSessionStart?.Invoke();
    }

    private void CloseSession()
    {
        LastSessionLength = debugSessionLength;
        LastAttendedSeconds = debugAttendedSeconds;

        sessionOpen = false;
        debugSessionLength = 0f;
        debugAttendedSeconds = 0f;

        if (resetShortTermState && stateLayer != null)
            stateLayer.ResetShortTerm();

        onSessionEnd?.Invoke();
    }

    private void BuildStatusLine()
    {
        if (trackingFlagged && healthSource != null)
        {
            StatusLine = "NOT TRACKING - " + healthSource.TrackingStatus;
            return;
        }

        string memory = longTermMemory != null
            ? string.Format(" | visitors {0} | familiarity {1:0.00} | fatigue {2:0.00}",
                longTermMemory.VisitorCount, longTermMemory.Familiarity, longTermMemory.Fatigue)
            : "";

        StatusLine = string.Format("{0}{1}", sessionOpen ? "visitor present" : "empty", memory);
    }
}
