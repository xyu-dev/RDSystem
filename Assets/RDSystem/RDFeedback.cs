using UnityEngine;

/// <summary>
/// Subscribes to MouseInput events and drives RD material parameters.
/// Feed/Kill writes are virtual so subclasses can override them.
/// </summary>
public class RDFeedback : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MouseInput          mouseInput;
    [SerializeField] private CustomRenderTexture rdTexture;

    [Header("Interaction Settings")]
    [SerializeField] private float baseRadius  = 0.05f;
    [SerializeField] private float maxRadius   = 0.15f;
    [SerializeField] private float baseFeed    = 0.055f;
    [SerializeField] private float maxFeed     = 0.08f;

    [Header("Random Speed Settings")]
    [SerializeField] private float minRadiusSpeed = 0.02f;
    [SerializeField] private float maxRadiusSpeed = 0.15f;
    [SerializeField] private float minFeedSpeed   = 0.005f;
    [SerializeField] private float maxFeedSpeed   = 0.03f;

    // --- Runtime state ---
    protected Material rdMaterial;
    private float      currentRadius;
    private float      currentFeed;

    // -------------------------------------------------------

    void Start()
    {
        if (rdTexture != null) rdMaterial = rdTexture.material;

        currentRadius = baseRadius;
        currentFeed   = baseFeed;

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
        //Debug.Log($"[RDFeedback] Interaction ended after {totalDuration:F2}s");
    }

    private void HandleDurationTick(float duration)
    {
        if (rdMaterial == null) return;

        (currentRadius, currentFeed) = RDInteractionUtils.StepParameters(
            currentRadius, currentFeed,
            minRadiusSpeed, maxRadiusSpeed,
            minFeedSpeed,   maxFeedSpeed,
            baseRadius,  maxRadius,
            baseFeed,    maxFeed,
            grow: true
        );

        rdMaterial.SetFloat("_ClickRadius", currentRadius);
        WriteFeed(currentFeed);
    }

    void Update()
    {
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
        WriteFeed(currentFeed);
    }

    // --- Virtual write methods ---

    protected virtual void WriteFeed(float feed)
    {
        rdMaterial?.SetFloat("_Feed", feed);
    }

    protected virtual void WriteKill(float kill)
    {
        rdMaterial?.SetFloat("_Kill", kill);
    }
}