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
    [Tooltip("Fast is the two-dot routine: about five seconds, which is as long as a stranger " +
             "will stand still. OneDot is quicker and coarser.")]
    [SerializeField] private VarjoEyeTracking.GazeCalibrationMode calibrationMode =
        VarjoEyeTracking.GazeCalibrationMode.Fast;

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

            bool requested = VarjoEyeTracking.RequestGazeCalibration(calibrationMode);

            Debug.Log(requested
                ? "[VarjoGazeInput] Calibration requested (" + calibrationMode + "). Follow the dots."
                : "[VarjoGazeInput] Varjo refused the calibration request.");

            return requested;
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
            LoseGaze();
            return;
        }

        bool gazeReady = VarjoEyeTracking.IsGazeAllowed() && VarjoEyeTracking.IsGazeCalibrated();
        if (!gazeReady)
        {
            LoseGaze();
            return;
        }

        if (!TryBuildGazeRay(out rayOrigin, out rayDirection))
        {
            LoseGaze();
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
            LoseGaze();
            return;
        }

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