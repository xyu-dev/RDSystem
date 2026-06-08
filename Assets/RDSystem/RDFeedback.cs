using UnityEngine;

/// <summary>
/// Subscribes to MouseInput events and drives RD material parameters.
/// Calls RDInteractionUtils for all parameter logic.
/// </summary>
public class RDFeedback : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MouseInput       mouseInput;
    [SerializeField] private CustomRenderTexture rdTexture;

    [Header("Interaction Settings")]
    [SerializeField] private float baseRadius  = 0.01f;
    [SerializeField] private float maxRadius   = 0.10f;
    [SerializeField] private float baseFeed    = 0.055f;
    [SerializeField] private float maxFeed     = 0.08f;

    [Header("Random Speed Settings")]
    [SerializeField] private float minRadiusSpeed = 0.02f;
    [SerializeField] private float maxRadiusSpeed = 0.15f;
    [SerializeField] private float minFeedSpeed   = 0.005f;
    [SerializeField] private float maxFeedSpeed   = 0.03f;

    // --- Runtime state ---
    private Material rdMaterial;
    private float    currentRadius;
    private float    currentFeed;

    // -------------------------------------------------------

    void Start()
    {
        if (rdTexture != null) rdMaterial = rdTexture.material;

        currentRadius = baseRadius;
        currentFeed   = baseFeed;

        // Subscribe to gaze events
        mouseInput.OnInteractionStart  += HandleStart;
        mouseInput.OnInteractionUpdate += HandleUpdate;
        mouseInput.OnInteractionEnd    += HandleEnd;
        mouseInput.OnDurationTick      += HandleDurationTick;
    }

    void OnDestroy()
    {
        mouseInput.OnInteractionStart  -= HandleStart;
        mouseInput.OnInteractionUpdate -= HandleUpdate;
        mouseInput.OnInteractionEnd    -= HandleEnd;
        mouseInput.OnDurationTick      -= HandleDurationTick;
    }

    // --- Event handlers ---

    private void HandleStart(Vector2 uv)
    {
        rdMaterial?.SetFloat("_ClickState", 1f);
        rdMaterial?.SetVector("_ClickPos", uv);
    }

    private void HandleUpdate(Vector2 uv)
    {
        rdMaterial?.SetVector("_ClickPos", uv);
    }

    private void HandleEnd(float totalDuration)
    {
        rdMaterial?.SetFloat("_ClickState", 0f);
        Debug.Log($"[RDFeedback] Interaction ended after {totalDuration:F2}s");
    }

    private void HandleDurationTick(float duration)
    {
        if (rdMaterial == null) return;

        // Grow while interacting
        (currentRadius, currentFeed) = RDInteractionUtils.StepParameters(
            currentRadius, currentFeed,
            minRadiusSpeed, maxRadiusSpeed,
            minFeedSpeed,   maxFeedSpeed,
            baseRadius,  maxRadius,
            baseFeed,    maxFeed,
            grow: true
        );

        rdMaterial.SetFloat("_ClickRadius", currentRadius);
        rdMaterial.SetFloat("_Feed",        currentFeed);
    }

    void Update()
    {
        // Shrink while NOT interacting
        if (mouseInput.IsInteracting || rdMaterial == null) return;

        (currentRadius, currentFeed) = RDInteractionUtils.StepParameters(
            currentRadius, currentFeed,
            minRadiusSpeed, maxRadiusSpeed,
            minFeedSpeed,   maxFeedSpeed,
            baseRadius,  maxRadius,
            baseFeed,    maxFeed,
            grow: false
        );

        rdMaterial.SetFloat("_ClickRadius", currentRadius);
        rdMaterial.SetFloat("_Feed",        currentFeed);
    }
}