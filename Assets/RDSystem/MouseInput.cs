using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Detects pointer/eye-gaze interaction on this GameObject.
/// Tracks interaction state and duration, then broadcasts events.
/// Does NOT touch any material or RD parameters.
/// </summary>
public class MouseInput : MonoBehaviour, 
    IPointerDownHandler, IDragHandler, 
    IPointerUpHandler, IPointerExitHandler
{
    // --- Events ---
    public event System.Action<Vector2> OnInteractionStart;   // UV position
    public event System.Action<Vector2> OnInteractionUpdate;  // UV position (per frame)
    public event System.Action<float>   OnInteractionEnd;     // total duration
    public event System.Action<float>   OnDurationTick;       // duration every frame while active

    // --- State (read-only access for external scripts) ---
    [SerializeField] public bool    IsInteracting     { get; private set; }
    [SerializeField] public float   InteractionTime   { get; private set; }  // seconds since last press
    [SerializeField] public Vector2 CurrentUV         { get; private set; }

    // -------------------------------------------------------

    public void OnPointerDown(PointerEventData eventData)
    {
        IsInteracting   = true;
        InteractionTime = 0f;

        Vector2 uv = RDInteractionUtils.GetUVFromPointer(eventData, gameObject);
        CurrentUV = uv;
        OnInteractionStart?.Invoke(uv);
    }

    public void OnDrag(PointerEventData eventData)
    {
        Vector2 uv = RDInteractionUtils.GetUVFromPointer(eventData, gameObject);
        CurrentUV = uv;
    }

    public void OnPointerUp(PointerEventData eventData)   => EndInteraction();
    public void OnPointerExit(PointerEventData eventData) => EndInteraction();

    void Update()
    {
        if (!IsInteracting) return;

        //Debug.Log($"Interacting at UV {CurrentUV} for {InteractionTime:F2} seconds");

        InteractionTime += Time.deltaTime;
        OnDurationTick?.Invoke(InteractionTime);
        OnInteractionUpdate?.Invoke(CurrentUV);
    }

    private void EndInteraction()
    {
        if (!IsInteracting) return;
        IsInteracting = false;
        OnInteractionEnd?.Invoke(InteractionTime);
        //Debug.Log($"Interaction ended after {InteractionTime:F2} seconds");
        InteractionTime = 0f;
    }
}