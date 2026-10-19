using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// Desktop stand-in for the eye tracker.
///
/// Two modes:
/// - Hover (default): the pointer behaves like a gaze ray. Resting the cursor on the sphere is
///   "gazing"; no click needed. This is what you want while developing without the headset,
///   because the dwell / switch / saccade signals then mean the same thing they will mean
///   under Varjo.
/// - Click: the original press-and-drag behaviour, kept for deliberate poking.
/// </summary>
public class MouseInput : MonoBehaviour, IInteractionSignalSource, IInteractionUVSource,
    IInteractionHealthSource,
    IPointerDownHandler, IDragHandler,
    IPointerUpHandler, IPointerExitHandler
{
    public enum PointerMode
    {
        HoverAsGaze,
        ClickAndDrag
    }

    public event System.Action<Vector2> OnInteractionStart;
    public event System.Action<Vector2> OnInteractionUpdate;
    public event System.Action<float> OnInteractionEnd;
    public event System.Action<float> OnDurationTick;

    [Header("Mode")]
    [SerializeField] private PointerMode mode = PointerMode.HoverAsGaze;

    [Header("Hover Raycast")]
    [SerializeField] private Camera sourceCamera;
    [SerializeField] private LayerMask interactionMask = ~0;
    [SerializeField] private float maxDistance = 50f;

    [Tooltip("When on, the ray comes from the screen centre instead of the cursor. Use with " +
             "CameraController to mimic a head-mounted display, where gaze starts at the centre.")]
    [SerializeField] private bool useScreenCentre = false;

    [Header("Behavior Tuning")]
    [SerializeField] private float eyeJumpDistanceThreshold = 0.18f;
    [SerializeField] private float headMotionScale = 1.0f;

    [Tooltip("Simulated tracker jitter, in local direction units. Keeps the mouse from being " +
             "unrealistically steadier than a real eye.")]
    [SerializeField] private float simulatedGazeNoise = 0.004f;

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
    public Vector3 InteractionDirection => CurrentDir; // local-space direction

    // A mouse is always "tracking". Reported so the router and the operator overlay can treat
    // both input sources through one interface.
    public bool IsTracking => true;
    public string TrackingStatus => "";

    private Vector3 lastDir = Vector3.forward;
    private bool hasLastDir;
    private bool wasHoveringLastFrame;

    private void Start()
    {
        if (sourceCamera == null)
            sourceCamera = Camera.main;
    }

    private void Update()
    {
        if (mode == PointerMode.HoverAsGaze)
            TickHover();
        else
            TickClick();
    }

    // ---------------------------------------------------------------- hover mode

    private void TickHover()
    {
        if (sourceCamera == null)
        {
            LoseHover();
            return;
        }

        if (!TryBuildPointerRay(out Ray ray))
        {
            LoseHover();
            return;
        }

        bool hasHit = Physics.Raycast(
            ray, out RaycastHit hit, maxDistance, interactionMask, QueryTriggerInteraction.Ignore);

        bool validHit = hasHit && hit.collider != null && hit.collider.gameObject == gameObject;

        if (!validHit)
        {
            LoseHover();
            return;
        }

        Vector3 localPoint = transform.InverseTransformPoint(hit.point);
        Vector3 localDir = localPoint.sqrMagnitude > 0.000001f ? localPoint.normalized : Vector3.forward;

        if (simulatedGazeNoise > 0f)
            localDir = (localDir + Random.insideUnitSphere * simulatedGazeNoise).normalized;

        CurrentUV = hit.textureCoord;
        CurrentDir = localDir;

        UpdateMotionSignals(localDir);

        if (!wasHoveringLastFrame)
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

        wasHoveringLastFrame = true;
    }

    private bool TryBuildPointerRay(out Ray ray)
    {
        ray = default;

        Vector2 screenPoint;

        if (useScreenCentre)
        {
            screenPoint = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        }
        else
        {
            if (Mouse.current == null) return false;
            screenPoint = Mouse.current.position.ReadValue();
        }

        ray = sourceCamera.ScreenPointToRay(screenPoint);
        return true;
    }

    // ---------------------------------------------------------------- click mode

    private void TickClick()
    {
        if (IsInteracting)
        {
            InteractionTime += Time.deltaTime;
            NoGazeDuration = 0f;

            OnDurationTick?.Invoke(InteractionTime);
            OnInteractionUpdate?.Invoke(CurrentUV);
        }
        else
        {
            TickNoGaze();
        }
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (mode != PointerMode.ClickAndDrag) return;

        IsInteracting = true;
        InteractionTime = 0f;
        NoGazeDuration = 0f;
        SwitchCount++;

        CurrentUV = RDInteractionUtils.GetUVFromPointer(eventData, gameObject);
        CurrentDir = GetLocalDirectionFromPointer(eventData);

        if (!hasLastDir)
        {
            lastDir = CurrentDir;
            hasLastDir = true;
        }

        HeadMotion = Vector3.Distance(lastDir, CurrentDir) * headMotionScale;
        EyeJump = HeadMotion >= eyeJumpDistanceThreshold;
        lastDir = CurrentDir;

        OnInteractionStart?.Invoke(CurrentUV);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (mode != PointerMode.ClickAndDrag) return;

        CurrentUV = RDInteractionUtils.GetUVFromPointer(eventData, gameObject);
        CurrentDir = GetLocalDirectionFromPointer(eventData);

        UpdateMotionSignals(CurrentDir);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        if (mode != PointerMode.ClickAndDrag) return;
        EndInteraction();
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (mode != PointerMode.ClickAndDrag) return;
        EndInteraction();
    }

    // ---------------------------------------------------------------- shared

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

    private void EndInteraction()
    {
        if (!IsInteracting) return;

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
    /// Every way of losing the hover this frame, handled identically - including clearing
    /// wasHoveringLastFrame, so the next arrival is counted as a new one. Mirrors
    /// VarjoGazeInput.LoseGaze, deliberately: the two sources have to behave the same way or
    /// tuning done on the mouse will not transfer to the headset.
    /// </summary>
    private void LoseHover()
    {
        EndInteraction();
        TickNoGaze();
        wasHoveringLastFrame = false;
    }

    private Vector3 GetLocalDirectionFromPointer(PointerEventData eventData)
    {
        if (RDInteractionUtils.TryGetPointerHit(eventData, gameObject, out RaycastHit hit))
        {
            Vector3 localPoint = transform.InverseTransformPoint(hit.point);
            Vector3 localDir = localPoint.normalized;

            if (localDir.sqrMagnitude < 0.000001f)
                return Vector3.forward;

            return localDir;
        }

        return Vector3.forward;
    }
}
