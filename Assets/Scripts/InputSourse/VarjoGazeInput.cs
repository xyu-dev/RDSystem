using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.XR;
using Varjo.XR;

public class VarjoGazeInput : MonoBehaviour, IInteractionSignalSource, IInteractionUVSource,
    IInteractionHealthSource, IInteractionCalibrationSource
{
    public event System.Action<Vector2> OnInteractionStart;
    public event System.Action<Vector2> OnInteractionUpdate;
    public event System.Action<float> OnInteractionEnd;
    public event System.Action<float> OnDurationTick;

    [Header("Calibration")]
    [Tooltip("OneDot: a single point, a couple of seconds, coarser. Fast is the two-dot routine " +
             "and is the better calibration, but it asks a stranger to hold still through a " +
             "longer sequence, and anyone who gives up halfway leaves the headset uncalibrated " +
             "for the next person in the queue. Deliberately a NEW field name: the old one " +
             "was already serialised as Fast in the scene, so changing its default would have " +
             "changed nothing. A renamed field falls back to the value written here.")]
    [SerializeField] private VarjoEyeTracking.GazeCalibrationMode gazeCalibrationMode =
        VarjoEyeTracking.GazeCalibrationMode.OneDot;

    [Tooltip("Print to the console whenever the tracking verdict changes. The cheapest way to " +
             "see which gate is refusing when the router will not hand over.")]
    [SerializeField] private bool logStatusChanges = true;

    [Tooltip("How long gaze may read Invalid before the source counts as lost. Must comfortably " +
             "exceed a blink, or the router never finishes its switch-back delay; must stay well " +
             "under it, or a headset taken off keeps the room running on nothing.")]
    [SerializeField] private float gazeDropoutGrace = 0.6f;

    [Tooltip("Gaze sample rate requested at startup. MaximumSupported is what the headset can do.")]
    [SerializeField] private VarjoEyeTracking.GazeOutputFrequency outputFrequency =
        VarjoEyeTracking.GazeOutputFrequency.MaximumSupported;

    [Header("Gaze Source")]
    [SerializeField] private GazeDataSource gazeDataSource = GazeDataSource.GazeAPI;
    [SerializeField] private Camera xrCamera;

    [Header("Raycast")]
    [SerializeField] private LayerMask interactionMask = ~0;
    [SerializeField] private float gazeRadius = 0.01f;
    [SerializeField] private float maxDistance = 20f;
    [SerializeField] private bool requireThisObject = true;

    [Header("Recovery")]
    [Tooltip("OFF by default, and that is deliberate. Varjo's calibration routine takes over " +
             "the display, so firing it automatically can seize the headset in the middle of a " +
             "visit - which is what a visitor closing their eyes for a few seconds was doing. " +
             "F2 is the operator path and a person can see whether now is a sensible moment.")]
    [SerializeField] private bool autoRecalibrateWhenLost = false;

    [Tooltip("Seconds continuously uncalibrated before asking. Long: a real calibration loss " +
             "persists, while eyes shut, a hand over the visor or a headset pushed up on the " +
             "forehead all clear on their own within a few seconds.")]
    [SerializeField] private float recalibrateAfterLostSeconds = 15f;

    [Tooltip("Seconds between requests. The routine itself takes several seconds and reports " +
             "uncalibrated while it runs, so asking again on top of it restarts it in a loop.")]
    [SerializeField] private float recalibrateCooldownSeconds = 60f;

    [Header("Behavior Tuning")]
    [SerializeField] private float eyeJumpDistanceThreshold = 0.18f;
    [SerializeField] private float headMotionScale = 1.0f;

    [field: SerializeField] public bool IsInteracting { get; private set; }
    [field: SerializeField] public float InteractionTime { get; private set; }
    [field: SerializeField] public Vector2 CurrentUV { get; private set; }
    [field: SerializeField] public Vector3 CurrentDir { get; private set; }

    [field: SerializeField] public float NoGazeDuration { get; private set; }
    [field: SerializeField] public int SwitchCount { get; private set; }
    [field: SerializeField] public float HeadMotion { get; private set; }
    [field: SerializeField] public bool EyeJump { get; private set; }

    public bool IsGazing => IsInteracting;
    public float GazeDuration => InteractionTime;
    public Vector3 InteractionDirection => CurrentDir;

    // ------------------------------------------------------------ tracking health
    //
    // Evaluated on demand rather than cached in Update, so the router can poll it even while
    // this component is disabled and decide whether to hand control back to the headset.

    public bool IsTracking => EvaluateTracking(out _);

    public string TrackingStatus
    {
        get
        {
            EvaluateTracking(out string status);
            return status;
        }
    }

    private string lastLoggedStatus;
    private float lastValidGazeTime = -999f;

    /// <summary>
    /// Wraps the real check so the reason reaches the console on its own.
    ///
    /// It is logged from here rather than from Update because the router disables this component
    /// the instant it falls back to the mouse - Update stops running, and the one fact anyone
    /// needs (which gate is failing) stops being reported at exactly the moment it matters. The
    /// router polls this every frame either way, so the transition is always caught.
    /// </summary>
    private bool EvaluateTracking(out string status)
    {
        bool ok = EvaluateTrackingCore(out status);

        if (logStatusChanges)
        {
            string line = ok ? "gaze healthy" : status;

            if (line != lastLoggedStatus)
            {
                lastLoggedStatus = line;
                Debug.Log("[VarjoGazeInput] " + line);
            }
        }

        return ok;
    }

    private bool EvaluateTrackingCore(out string status)
    {
        if (xrCamera == null && Camera.main == null)
        {
            status = "no XR camera";
            return false;
        }

        try
        {
            if (!VarjoEyeTracking.IsGazeAvailable())
            {
                status = "no eye tracker on this headset";
                return false;
            }

            if (VarjoEyeTracking.IsGazeCalibrating())
            {
                status = "calibrating - hold still";
                return false;
            }

            if (!VarjoEyeTracking.IsGazeAllowed())
            {
                status = "gaze not allowed (enable eye tracking in Varjo Base)";
                return false;
            }

            if (!VarjoEyeTracking.IsGazeCalibrated())
            {
                status = "eye tracking not calibrated - run calibration for this visitor";
                return false;
            }

            // A blink must not count as "lost".
            //
            // The router only hands the room back after switchBackDelay seconds of CONTINUOUS
            // health, and it resets that counter on any unhealthy frame. Gaze goes Invalid every
            // time the visitor blinks - roughly every three seconds, for a couple of hundred
            // milliseconds. Failing instantly on Invalid therefore resets the router's counter
            // faster than it can ever fill, and the hand-over never happens no matter how well
            // the eye tracker is working. That is the whole bug: the gaze was fine, the health
            // check was not.
            //
            // Update() already forgives the same dropouts for the same reason; see LoseGaze.
            if (VarjoEyeTracking.GetGaze().status == VarjoEyeTracking.GazeStatus.Invalid)
            {
                if (Time.unscaledTime - lastValidGazeTime > gazeDropoutGrace)
                {
                    status = "gaze invalid (headset not worn or eyes not found)";
                    return false;
                }
            }
            else
            {
                lastValidGazeTime = Time.unscaledTime;
            }
        }
        catch (System.Exception e)
        {
            // Thrown when the Varjo runtime or XR loader is not present at all, e.g. on a
            // desktop machine with no headset attached.
            status = "Varjo runtime unavailable (" + e.GetType().Name + ")";
            return false;
        }

        status = "";
        return true;
    }

    /// <summary>
    /// Everything Varjo will tell us about the gaze, in one line.
    ///
    /// A refusal on its own is not actionable: available, allowed, calibrating and calibrated
    /// fail for completely different reasons and only one of them has anything to do with this
    /// project. Printing all of them together turns a dead end into a decision.
    /// </summary>
    public string DescribeGazeState()
    {
        try
        {
            return "available " + VarjoEyeTracking.IsGazeAvailable() +
                   " | allowed " + VarjoEyeTracking.IsGazeAllowed() +
                   " | calibrating " + VarjoEyeTracking.IsGazeCalibrating() +
                   " | calibrated " + VarjoEyeTracking.IsGazeCalibrated() +
                   " | status " + VarjoEyeTracking.GetGaze().status +
                   " | worn " + HeadsetWorn;
        }
        catch (System.Exception e)
        {
            return "Varjo runtime unavailable (" + e.GetType().Name + ")";
        }
    }

    // ------------------------------------------------------------ calibration

    public bool CanCalibrate => true;

    /// <summary>
    /// Starts Varjo's own calibration routine for whoever is wearing the headset now.
    ///
    /// Nothing in the piece ever called this, which is why gaze can look permanently dead:
    /// IsGazeCalibrated() stays false until a calibration has actually run, the router sees an
    /// unhealthy preferred source, and it hands the room to the mouse - silently, forever.
    /// </summary>
    public bool RequestCalibration()
    {
        try
        {
            if (!VarjoEyeTracking.IsGazeAvailable())
            {
                Debug.LogWarning("[VarjoGazeInput] No eye tracker available; nothing to calibrate.");
                return false;
            }

            if (!VarjoEyeTracking.IsGazeAllowed())
            {
                Debug.LogWarning("[VarjoGazeInput] Eye tracking is not allowed. Enable it in " +
                                 "Varjo Base, then request calibration again.");
                return false;
            }

            if (VarjoEyeTracking.IsGazeCalibrating())
            {
                Debug.Log("[VarjoGazeInput] Already calibrating; letting it finish.");
                return false;
            }

            if (VarjoEyeTracking.RequestGazeCalibration(gazeCalibrationMode))
            {
                Debug.Log("[VarjoGazeInput] Calibration requested (" + gazeCalibrationMode +
                          "). Follow the dots.");
                return true;
            }

            // Fall back rather than fail.
            //
            // OneDot is not offered by every Varjo Base and headset combination, and a refused
            // request looks exactly like calibration being broken: nothing happens, no dots, no
            // error the operator can act on. Fast is the mode every version has, so asking for
            // it second means the worst case is a longer routine rather than no routine.
            if (gazeCalibrationMode != VarjoEyeTracking.GazeCalibrationMode.Fast)
            {
                Debug.LogWarning("[VarjoGazeInput] Varjo refused " + gazeCalibrationMode +
                                 " calibration; falling back to Fast. Set Gaze Calibration Mode " +
                                 "to Fast if this Base version never offers the single dot.");

                if (VarjoEyeTracking.RequestGazeCalibration(
                        VarjoEyeTracking.GazeCalibrationMode.Fast))
                {
                    Debug.Log("[VarjoGazeInput] Calibration requested (Fast). Follow the dots.");
                    return true;
                }
            }

            // Throttled. Eight identical errors in two seconds says nothing the first one did not,
            // and it buries the state dump below that actually explains the refusal.
            if (Time.unscaledTime - lastRefusalLog > 3f)
            {
                lastRefusalLog = Time.unscaledTime;

                Debug.LogError("[VarjoGazeInput] Varjo refused the calibration request." +
                               System.Environment.NewLine + DescribeGazeState());
            }

            return false;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[VarjoGazeInput] Calibration unavailable (" + e.GetType().Name +
                             "). Is the headset connected and the Varjo loader active?");
            return false;
        }
    }

    private readonly List<InputDevice> devices = new List<InputDevice>();
    private InputDevice device;
    private Eyes eyes;
    private VarjoEyeTracking.GazeData gazeData;

    private Vector3 fixationPoint;
    private Vector3 rayOrigin;
    private Vector3 rayDirection;

    private float dropoutFor;
    private float uncalibratedFor;
    private float lastCalibrationRequest = -999f;
    private float lastRefusalLog = -999f;
    private Vector3 lastDir = Vector3.forward;
    private bool hasLastDir = false;
    private bool wasInteractingLastFrame = false;

    private void OnEnable()
    {
        if (!device.isValid)
            GetDevice();
    }

    private void Start()
    {
        if (xrCamera == null)
            xrCamera = Camera.main;

        // Asking for the fastest the headset will give: the signal this piece reads - dwell,
        // saccades, returns - is made of small fast movements, and at 100 Hz the short ones are
        // simply not in the data. Wrapped because this throws outright when no Varjo runtime is
        // loaded, which is every time anyone opens the scene on a desk.
        try
        {
            VarjoEyeTracking.SetGazeOutputFrequency(outputFrequency);
        }
        catch (System.Exception e)
        {
            Debug.Log("[VarjoGazeInput] Gaze output frequency not set (" + e.GetType().Name +
                      "). Normal with no headset attached.");
        }
    }

    private void Update()
    {
        if (xrCamera == null)
        {
            HoldOrLoseGaze();
            return;
        }

        TryAutoRecalibrate();

        bool gazeReady = VarjoEyeTracking.IsGazeAllowed() && VarjoEyeTracking.IsGazeCalibrated();
        if (!gazeReady)
        {
            HoldOrLoseGaze();
            return;
        }

        if (!TryBuildGazeRay(out rayOrigin, out rayDirection))
        {
            HoldOrLoseGaze();
            return;
        }

        bool hasHit = Physics.SphereCast(
            rayOrigin,
            gazeRadius,
            rayDirection,
            out RaycastHit hit,
            maxDistance,
            interactionMask,
            QueryTriggerInteraction.Ignore
        );

        bool validHit = hasHit && (!requireThisObject || hit.collider.gameObject == gameObject);

        if (!validHit)
        {
            HoldOrLoseGaze();
            return;
        }

        dropoutFor = 0f;

        Vector2 uv = hit.textureCoord;
        Vector3 localPoint = transform.InverseTransformPoint(hit.point);
        Vector3 localDir = localPoint.sqrMagnitude > 0.000001f ? localPoint.normalized : Vector3.forward;

        CurrentUV = uv;
        CurrentDir = localDir;

        UpdateMotionSignals(localDir);

        if (!wasInteractingLastFrame)
        {
            IsInteracting = true;
            InteractionTime = 0f;
            NoGazeDuration = 0f;
            SwitchCount++;
            OnInteractionStart?.Invoke(CurrentUV);
        }
        else
        {
            IsInteracting = true;
            InteractionTime += Time.deltaTime;
            NoGazeDuration = 0f;
            OnDurationTick?.Invoke(InteractionTime);
            OnInteractionUpdate?.Invoke(CurrentUV);
        }

        wasInteractingLastFrame = true;
    }

    private void GetDevice()
    {
        InputDevices.GetDevicesAtXRNode(XRNode.CenterEye, devices);
        device = devices.FirstOrDefault();
    }

    private bool TryBuildGazeRay(out Vector3 origin, out Vector3 dir)
    {
        origin = Vector3.zero;
        dir = Vector3.forward;

        if (gazeDataSource == GazeDataSource.InputSubsystem)
        {
            if (!device.isValid)
                GetDevice();

            if (!device.isValid)
                return false;

            if (!device.TryGetFeatureValue(CommonUsages.eyesData, out eyes))
                return false;

            if (!eyes.TryGetFixationPoint(out fixationPoint))
                return false;

            origin = xrCamera.transform.position;
            dir = (fixationPoint - xrCamera.transform.position).normalized;
            return dir.sqrMagnitude > 0.000001f;
        }
        else
        {
            gazeData = VarjoEyeTracking.GetGaze();

            if (gazeData.status == VarjoEyeTracking.GazeStatus.Invalid)
                return false;

            origin = xrCamera.transform.TransformPoint(gazeData.gaze.origin);
            dir = xrCamera.transform.TransformDirection(gazeData.gaze.forward).normalized;
            return dir.sqrMagnitude > 0.000001f;
        }
    }

    private void UpdateMotionSignals(Vector3 localDir)
    {
        if (!hasLastDir)
        {
            lastDir = localDir;
            hasLastDir = true;
        }

        HeadMotion = Vector3.Distance(lastDir, localDir) * headMotionScale;
        EyeJump = HeadMotion >= eyeJumpDistanceThreshold;

        if (EyeJump)
            SwitchCount++;

        lastDir = localDir;
    }

    private void EndInteractionIfNeeded()
    {
        if (!IsInteracting)
            return;

        IsInteracting = false;
        OnInteractionEnd?.Invoke(InteractionTime);
        InteractionTime = 0f;
        HeadMotion = 0f;
        EyeJump = false;
    }

    private void TickNoGaze()
    {
        NoGazeDuration += Time.deltaTime;
        HeadMotion = 0f;
        EyeJump = false;
    }

    /// <summary>
    /// Puts the gaze back together without waiting for a human.
    ///
    /// Varjo can report the gaze uncalibrated again part way through a visit - a brow pushing
    /// the headset, the proximity sensor deciding it came off, a dropped session. The visitor
    /// has not moved and has no idea anything is wrong; from inside, the room simply stops
    /// answering, and then recedes, which is how a perfectly healthy headset ends up looking
    /// like a blank sphere that needs an operator.
    ///
    /// Cooldown rather than a retry counter: the calibration routine itself takes several
    /// seconds and reports uncalibrated while it runs, so asking again on top of it would
    /// restart it in a loop and the visitor would chase dots forever.
    /// </summary>
    /// <summary>
    /// Whether the headset is on a head right now, from its own proximity sensor.
    ///
    /// Fully qualified rather than imported: this file already uses UnityEngine.XR.InputDevice,
    /// and a using for the Input System would make that name ambiguous.
    /// </summary>
    private static bool HeadsetWorn
    {
        get
        {
            Varjo.XR.Input.VarjoHMD hmd =
                UnityEngine.InputSystem.InputSystem.GetDevice<Varjo.XR.Input.VarjoHMD>();

            return hmd == null || hmd.userPresence == null || hmd.userPresence.isPressed;
        }
    }

    private void TryAutoRecalibrate()
    {
        if (!autoRecalibrateWhenLost) return;

        try
        {
            if (!VarjoEyeTracking.IsGazeAvailable() || !VarjoEyeTracking.IsGazeAllowed())
                return;

            if (VarjoEyeTracking.IsGazeCalibrating() || VarjoEyeTracking.IsGazeCalibrated())
            {
                uncalibratedFor = 0f;
                return;
            }

            uncalibratedFor += Time.deltaTime;

            if (uncalibratedFor < recalibrateAfterLostSeconds) return;
            if (Time.unscaledTime - lastCalibrationRequest < recalibrateCooldownSeconds) return;

            // Never while the headset is off a head. A unit on the table reports no gaze and no
            // calibration forever, and calibrating it is both pointless and the thing that
            // hijacks the display the moment the next visitor picks it up.
            if (!HeadsetWorn) return;

            lastCalibrationRequest = Time.unscaledTime;
            uncalibratedFor = 0f;

            Debug.Log("[VarjoGazeInput] Calibration lost with the headset still on - asking for " +
                      "it again rather than waiting for the operator.");

            RequestCalibration();
        }
        catch (System.Exception)
        {
            // No runtime, nothing to recalibrate. EvaluateTracking already says so.
        }
    }

    /// <summary>
    /// A blink is not a visitor looking away.
    ///
    /// Varjo reports gaze Invalid for a couple of hundred milliseconds every time someone blinks,
    /// which is roughly every three seconds, all day. Ending the interaction there does far more
    /// than flicker a marker: re-acquiring on the next valid frame takes the start branch and
    /// increments SwitchCount, the state layer reads that as a saccade, and so every blink
    /// arrives downstream as attention switching that never happened. Taking the headset off and
    /// putting it back on delivers a burst of them at once, which drives switchActivity and
    /// agitation up together and leaves the surface cycling - bright, churning, never settling,
    /// because the blinks keep arriving and keep restarting it.
    ///
    /// So hold through a dropout shorter than the grace, and only let go when it lasts long
    /// enough to mean something. Same grace the health check uses, for the same reason.
    /// </summary>
    private void HoldOrLoseGaze()
    {
        dropoutFor += Time.deltaTime;

        if (dropoutFor <= gazeDropoutGrace && wasInteractingLastFrame)
        {
            // Report "not gazing" at once - closed eyes are not looking, and the surface has to
            // stop responding the moment they shut, not two thirds of a second later.
            //
            // But do NOT clear wasInteractingLastFrame. That flag is what makes the next valid
            // frame a continuation rather than a fresh acquisition, so the blink still does not
            // arrive downstream as a saccade. Stopping the signal and counting a switch are two
            // different things, and only the second one was ever the bug.
            if (IsInteracting)
            {
                IsInteracting = false;
                OnInteractionEnd?.Invoke(InteractionTime);
            }

            TickNoGaze();
            return;
        }

        LoseGaze();
    }

    /// <summary>
    /// Every way of losing the gaze this frame, handled identically.
    ///
    /// Clearing wasInteractingLastFrame is the point: without it, a momentary dropout - a blink,
    /// a headset adjustment, one invalid frame during calibration - ends the interaction but
    /// leaves the flag set, so when gaze returns the re-acquisition branch is skipped. No
    /// SwitchCount increment, no OnInteractionStart. The visitor looks away and back and the
    /// machine records nothing. In a gallery, that is most of the dropouts.
    /// </summary>
    private void LoseGaze()
    {
        EndInteractionIfNeeded();
        TickNoGaze();
        wasInteractingLastFrame = false;
    }
}