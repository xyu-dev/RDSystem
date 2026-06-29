using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Detects pointer / gaze interaction on this GameObject.
/// Also serves as an IInteractionSignalSource.
/// </summary>
public class MouseInput : MonoBehaviour, IInteractionSignalSource,
    IPointerDownHandler, IDragHandler,
    IPointerUpHandler, IPointerExitHandler
{
    public event System.Action<Vector2> OnInteractionStart;
    public event System.Action<Vector2> OnInteractionUpdate;
    public event System.Action<float> OnInteractionEnd;
    public event System.Action<float> OnDurationTick;

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
    public Vector3 InteractionDirection => CurrentDir; // local-space direction

    private Vector3 lastDir = Vector3.forward;
    private bool hasLastDir = false;

    public void OnPointerDown(PointerEventData eventData)
    {
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
        CurrentUV = RDInteractionUtils.GetUVFromPointer(eventData, gameObject);
        CurrentDir = GetLocalDirectionFromPointer(eventData);

        HeadMotion = Vector3.Distance(lastDir, CurrentDir) * headMotionScale;
        EyeJump = HeadMotion >= eyeJumpDistanceThreshold;

        if (EyeJump)
            SwitchCount++;

        lastDir = CurrentDir;
    }

    public void OnPointerUp(PointerEventData eventData) => EndInteraction();
    public void OnPointerExit(PointerEventData eventData) => EndInteraction();

    private void Update()
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
            NoGazeDuration += Time.deltaTime;
            HeadMotion = 0f;
            EyeJump = false;
        }
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