using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.XR;
using Varjo.XR;

public class VarjoGazeInput : MonoBehaviour, IInteractionSignalSource, IInteractionUVSource
{
    public event System.Action<Vector2> OnInteractionStart;
    public event System.Action<Vector2> OnInteractionUpdate;
    public event System.Action<float> OnInteractionEnd;
    public event System.Action<float> OnDurationTick;

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
    }

    private void Update()
    {
        if (xrCamera == null)
        {
            EndInteractionIfNeeded();
            TickNoGaze();
            return;
        }

        bool gazeReady = VarjoEyeTracking.IsGazeAllowed() && VarjoEyeTracking.IsGazeCalibrated();
        if (!gazeReady)
        {
            EndInteractionIfNeeded();
            TickNoGaze();
            return;
        }

        if (!TryBuildGazeRay(out rayOrigin, out rayDirection))
        {
            EndInteractionIfNeeded();
            TickNoGaze();
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
            EndInteractionIfNeeded();
            TickNoGaze();
            wasInteractingLastFrame = false;
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
}