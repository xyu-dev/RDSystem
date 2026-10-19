using UnityEngine;

/// <summary>
/// Feeds the gaze into the depth field and keeps its rates frame-rate independent.
///
/// The field accumulates under the gaze and relaxes with a time constant, so space the viewer
/// stops attending to closes back up. That decay is doing two jobs: it keeps the surface from
/// saturating into a uniformly inflated sphere - which would be invisible again, for exactly the
/// reason uniform expansion was - and it turns the mechanic from acquisition into maintenance.
/// You cannot bank the space you opened.
/// </summary>
public class RDGazeDepthDriver : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private CustomRenderTexture depthTexture;
    [SerializeField] private Material depthFieldMaterial;
    [SerializeField] private Material displayMaterial;
    [SerializeField] private RDController controller;

    [Header("Rates")]
    [Tooltip("How fast the dent deepens while the viewer holds a gaze, in field units per second.")]
    [Range(0.01f, 4f)] [SerializeField] private float paintPerSecond = 0.55f;

    [Tooltip("How fast the dent spreads and smooths. Low values give a narrow pit, high values a " +
             "wide shallow bowl.")]
    [Range(0f, 20f)] [SerializeField] private float diffusePerSecond = 3.5f;

    [Tooltip("Relaxation time constant in seconds. At 10 the dent is about 95% closed half a " +
             "minute after the viewer looks away.")]
    [Range(0.5f, 120f)] [SerializeField] private float decayTau = 10f;

    [Header("Shape")]
    [Tooltip("Angular radius of the painted area, in radians.")]
    [Range(0.01f, 1.5f)] [SerializeField] private float paintRadius = 0.26f;

    [Tooltip("Softness of the painted edge, in radians. Wide and soft gives a bowl; narrow gives " +
             "a crater with a rim, which reads as damage rather than as space.")]
    [Range(0.001f, 0.8f)] [SerializeField] private float paintSoftness = 0.22f;

    [Header("Read-only")]
    [SerializeField] private bool debugGazing;
    [SerializeField] private float debugDeltaTime;

    private static readonly int GazeDirId = Shader.PropertyToID("_GazeDir");
    private static readonly int GazeStateId = Shader.PropertyToID("_GazeState");
    private static readonly int DeltaTimeId = Shader.PropertyToID("_DeltaTime");
    private static readonly int PaintPerSecondId = Shader.PropertyToID("_PaintPerSecond");
    private static readonly int DiffusePerSecondId = Shader.PropertyToID("_DiffusePerSecond");
    private static readonly int DecayTauId = Shader.PropertyToID("_DecayTau");
    private static readonly int PaintRadiusId = Shader.PropertyToID("_PaintRadius");
    private static readonly int PaintSoftnessId = Shader.PropertyToID("_PaintSoftness");
    private static readonly int DepthTexId = Shader.PropertyToID("_DepthTex");

    /// <summary>
    /// Relaxation time constant, in seconds. RDRecession lengthens this when the room empties so
    /// the dent contracts over a minute or more instead of falling away.
    /// </summary>
    public float DecayTau
    {
        get => decayTau;
        set => decayTau = Mathf.Clamp(value, 0.5f, 240f);
    }

    /// <summary>
    /// How fast the dent spreads and smooths, per second.
    ///
    /// This is the one that caused the dent to vanish the instant the headset came off. At 3.5 it
    /// blurs the field towards its own neighbourhood average about three and a half times a
    /// second, which flattens a bump in a couple of seconds no matter what the decay is set to -
    /// so the slow fade downstream never got anything to fade. RDRecession drops it almost to
    /// zero when the room empties, which freezes the SHAPE and lets the decay do the contracting.
    /// </summary>
    public float DiffusePerSecond
    {
        get => diffusePerSecond;
        set => diffusePerSecond = Mathf.Clamp(value, 0f, 20f);
    }

    /// <summary>How fast the dent deepens under a held gaze, per second.</summary>
    public float PaintPerSecond
    {
        get => paintPerSecond;
        set => paintPerSecond = Mathf.Clamp(value, 0f, 4f);
    }

    private void Awake()
    {
        if (controller == null) controller = GetComponent<RDController>();

        if (depthFieldMaterial == null)
            Debug.LogError("[RDGazeDepthDriver] No depth field material; nothing will deform.");

        if (displayMaterial != null && depthTexture != null)
            displayMaterial.SetTexture(DepthTexId, depthTexture);
    }

    private void Update()
    {
        if (depthFieldMaterial == null) return;

        bool gazing = controller != null && controller.CurrentState.isGazing;
        Vector3 dir = controller != null ? controller.RingDirection : Vector3.up;

        if (dir.sqrMagnitude < 0.000001f) dir = Vector3.up;

        // Clamped, because a hitch would otherwise paint or decay a whole second in one step and
        // the dent would visibly jump.
        float dt = Mathf.Min(Time.deltaTime, 0.1f);

        depthFieldMaterial.SetVector(GazeDirId, new Vector4(dir.x, dir.y, dir.z, 0f));
        depthFieldMaterial.SetFloat(GazeStateId, gazing ? 1f : 0f);
        depthFieldMaterial.SetFloat(DeltaTimeId, dt);

        depthFieldMaterial.SetFloat(PaintPerSecondId, paintPerSecond);
        depthFieldMaterial.SetFloat(DiffusePerSecondId, diffusePerSecond);
        depthFieldMaterial.SetFloat(DecayTauId, decayTau);
        depthFieldMaterial.SetFloat(PaintRadiusId, paintRadius);
        depthFieldMaterial.SetFloat(PaintSoftnessId, paintSoftness);

        debugGazing = gazing;
        debugDeltaTime = dt;
    }

    /// <summary>Flatten the space back out. For the operator between days.</summary>
    public void ResetDepth()
    {
        if (depthTexture != null)
            depthTexture.Initialize();
    }
}
