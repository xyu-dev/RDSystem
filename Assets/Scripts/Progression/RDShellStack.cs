using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Scene 2's mechanic. Gaze makes the space grow: each translucent shell expands outward while
/// someone is looking, and because its cells are fixed in world units a growing shell becomes a
/// finer image. When the newest shell has travelled far enough, another is born close in at a
/// different hue, and the stack accumulates - the viewer ends up inside a set of nested fields
/// seen through each other's holes.
///
/// Two decisions worth stating:
///
/// The shells share ONE simulation buffer. Running a reaction-diffusion field per shell would cost
/// six CustomRenderTextures and buy very little, because what makes two shells read as different
/// is their cell density, their hue and their rotation - not whether the underlying pattern is a
/// separate solve. Each shell gets its own longitude rotation so they do not line up.
///
/// Render order is assigned explicitly. Every shell is centred on the viewer, so Unity's
/// transparent sorting - which orders by distance to the renderer's position - sees them all at
/// the same distance and the order is undefined, which shows up as flickering as the camera
/// moves. Largest radius gets the lowest queue, so the stack draws back to front.
/// </summary>
public class RDShellStack : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private CustomRenderTexture rdTexture;
    [SerializeField] private Material shellMaterialTemplate;
    [SerializeField] private RDController controller;

    [Header("Growth")]
    [Tooltip("Radius a shell is born at, in metres. With 12 cm cells a 3 m shell has 2.3 degree " +
             "cells - clearly coarse, still legible. Much smaller than this and a new shell " +
             "arrives as unreadable blocks filling the whole view.")]
    [SerializeField] private float birthRadius = 3f;

    [Tooltip("Radius at which a shell stops growing and is retired, in metres.")]
    [SerializeField] private float maxRadius = 24f;

    [Tooltip("Metres per second of expansion while someone is looking.")]
    [SerializeField] private float growthPerSecond = 0.9f;

    [Tooltip("How much sustained attention accelerates the growth, as a multiplier at attention 1.")]
    [SerializeField] private float attentionBoost = 2.2f;

    [Tooltip("Metres per second of expansion when nobody is looking. 0 freezes the stack.")]
    [SerializeField] private float idleGrowthPerSecond = 0f;

    [Header("Stack")]
    [Tooltip("Radius the newest shell must pass before the next one is born.")]
    [SerializeField] private float spawnSpacing = 6f;

    [Tooltip("Shells alive at once. The oldest is retired past this.")]
    [Range(1, 8)] [SerializeField] private int maxShells = 5;

    [Tooltip("Hue step between consecutive shells, 0..1 of the wheel.")]
    [Range(0f, 1f)] [SerializeField] private float huePerShell = 0.14f;

    [Tooltip("Metres over which a newly born shell fades in, and a retiring one fades out. " +
             "Without this they pop.")]
    [SerializeField] private float fadeDistance = 2.5f;

    [Header("Cells")]
    [Tooltip("Metres across, on the surface. 0.12 reproduces the 1.4 degree cells of the pixel " +
             "display at a 5 m radius, so the look is continuous with the other scenes.")]
    [SerializeField] private float cellWorldSize = 0.12f;

    [Header("Read-only")]
    [SerializeField] private int debugShellCount;
    [SerializeField] private float debugNewestRadius;
    [SerializeField] private int debugTotalBorn;

    private sealed class Shell
    {
        public GameObject go;
        public Renderer renderer;
        public Material material;
        public float radius;
        public int index;
    }

    private readonly List<Shell> shells = new List<Shell>();

    private static readonly int RadiusId = Shader.PropertyToID("_Radius");
    private static readonly int HueShiftId = Shader.PropertyToID("_HueShift");
    private static readonly int RotationId = Shader.PropertyToID("_UVRotation");
    private static readonly int FadeId = Shader.PropertyToID("_ShellFade");
    private static readonly int CellWorldSizeId = Shader.PropertyToID("_CellWorldSize");
    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");

    private int totalBorn;

    /// <summary>Radius of the newest, innermost shell.</summary>
    public float NewestRadius => shells.Count > 0 ? shells[shells.Count - 1].radius : 0f;

    public int ShellCount => shells.Count;
    public int TotalBorn => totalBorn;

    private void Awake()
    {
        if (controller == null) controller = GetComponent<RDController>();

        if (shellMaterialTemplate == null)
        {
            Debug.LogError("[RDShellStack] No shell material assigned; nothing will be drawn.");
            return;
        }

        SpawnShell();
    }

    private void Update()
    {
        if (shellMaterialTemplate == null) return;

        bool gazing = controller != null && controller.CurrentState.isGazing;
        float attention = controller != null ? controller.CurrentState.attention : 0f;

        float rate = gazing
            ? growthPerSecond * Mathf.Lerp(1f, attentionBoost, Mathf.Clamp01(attention))
            : idleGrowthPerSecond;

        float delta = rate * Time.deltaTime;

        for (int i = 0; i < shells.Count; i++)
        {
            Shell shell = shells[i];
            shell.radius = Mathf.Min(shell.radius + delta, maxRadius);
            ApplyShell(shell);
        }

        // Sort before the retirement checks, which index into the list expecting largest first.
        // Insertion order happens to agree today, but only because new shells are always the
        // smallest - that is a coincidence, not an invariant worth relying on.
        shells.Sort((a, b) => b.radius.CompareTo(a.radius));

        // Birth: once the newest has moved far enough out to leave room for another behind it.
        if (NewestRadius >= birthRadius + spawnSpacing)
            SpawnShell();

        // Retire from the outside in: index 0 is the largest.
        while (shells.Count > maxShells)
            RetireShell(0);

        if (shells.Count > 1 && shells[0].radius >= maxRadius - 0.001f)
            RetireShell(0);

        AssignRenderOrder();

        debugShellCount = shells.Count;
        debugNewestRadius = NewestRadius;
        debugTotalBorn = totalBorn;
    }

    private void SpawnShell()
    {
        if (shells.Count >= maxShells + 1) return;

        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "Shell " + totalBorn;

        // Visual only. The gaze raycasts against a separate fixed interaction sphere, so the
        // shells must not catch the ray or the hit UV would jump every time one is born.
        Collider collider = go.GetComponent<Collider>();
        if (collider != null) Destroy(collider);

        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.zero;

        Material material = new Material(shellMaterialTemplate);

        if (rdTexture != null)
            material.SetTexture(MainTexId, rdTexture);

        material.SetFloat(CellWorldSizeId, cellWorldSize);
        material.SetFloat(HueShiftId, Mathf.Repeat(totalBorn * huePerShell, 1f));

        // Golden-ratio rotation, so no two shells in the stack line up and the sequence never
        // repeats at any stack depth.
        material.SetFloat(RotationId, Mathf.Repeat(totalBorn * 0.6180339f, 1f));

        Renderer renderer = go.GetComponent<Renderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        Shell shell = new Shell
        {
            go = go,
            renderer = renderer,
            material = material,
            radius = birthRadius,
            index = totalBorn
        };

        shells.Add(shell);
        totalBorn++;

        ApplyShell(shell);
    }

    private void ApplyShell(Shell shell)
    {
        // The primitive sphere is diameter 1 at scale 1.
        shell.go.transform.localScale = Vector3.one * (shell.radius * 2f);

        shell.material.SetFloat(RadiusId, shell.radius);

        // Fade in off the birth radius and out towards the retirement radius, so neither event
        // is a pop.
        float fade = 1f;

        if (fadeDistance > 0.01f)
        {
            float fadeIn = Mathf.Clamp01((shell.radius - birthRadius) / fadeDistance);
            float fadeOut = Mathf.Clamp01((maxRadius - shell.radius) / fadeDistance);
            fade = Mathf.Min(fadeIn, fadeOut);
        }

        shell.material.SetFloat(FadeId, fade);
    }

    /// <summary>
    /// Largest radius first. Every shell shares the viewer's position, so without this the draw
    /// order is undefined and the stack flickers as the camera moves.
    /// </summary>
    private void AssignRenderOrder()
    {
        shells.Sort((a, b) => b.radius.CompareTo(a.radius));

        for (int i = 0; i < shells.Count; i++)
        {
            if (shells[i].material != null)
                shells[i].material.renderQueue = 3000 + i;
        }
    }

    private void RetireShell(int index)
    {
        if (index < 0 || index >= shells.Count) return;

        Shell shell = shells[index];
        shells.RemoveAt(index);

        if (shell.material != null) Destroy(shell.material);
        if (shell.go != null) Destroy(shell.go);
    }

    /// <summary>Clear the stack and start again from one shell. For the operator between days.</summary>
    public void ResetStack()
    {
        while (shells.Count > 0)
            RetireShell(0);

        totalBorn = 0;
        SpawnShell();
    }

    private void OnDestroy()
    {
        for (int i = 0; i < shells.Count; i++)
            if (shells[i].material != null) Destroy(shells[i].material);

        shells.Clear();
    }
}
