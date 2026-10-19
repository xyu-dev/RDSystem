using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Scene 1's mechanic. The surface starts pixelated; looking resolves it to smooth, spreading
/// outward from the gaze; covering the whole sphere raises the level, which halves the cell size
/// and wipes the progress so it has to be resolved again on a finer grid.
///
/// The reward for finishing is that it gets harder. That is the argument, not a side effect.
///
/// Two things are worth knowing about how it is built:
///
/// The resolve state lives in its own CustomRenderTexture rather than being derived from a single
/// growing radius, because a buffer handles what a radius cannot: the gaze moving around, several
/// fronts merging, and progress persisting where the viewer has already been.
///
/// Completion is measured by reading that buffer back asynchronously a few times a second and
/// averaging it on the CPU. A frame or two of latency is irrelevant for a progress check, and a
/// full readback of a 512x256 single-channel texture is about 130 KB - cheap enough to do plainly
/// rather than depending on mip generation behaving on a double-buffered CRT.
/// </summary>
public class RDResolveProgression : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private CustomRenderTexture resolveTexture;
    [SerializeField] private Material resolveMaterial;
    [SerializeField] private Material displayMaterial;
    [SerializeField] private RDController controller;

    [Tooltip("The simulation buffer. Only read, to work out how fine the cells can usefully get.")]
    [SerializeField] private CustomRenderTexture simulationTexture;

    [Header("Levels")]
    [Tooltip("Cells along one cube face edge at level 0. 64 is 256 cells around the sphere.")]
    [SerializeField] private int baseCellsPerFace = 64;

    [Tooltip("Fraction of the sphere that must be resolved before the level advances.")]
    [Range(0.5f, 1f)] [SerializeField] private float completionThreshold = 0.9f;

    [Tooltip("Seconds to hold the completed image before wiping it for the next level, so the " +
             "viewer gets to see what they finished.")]
    [SerializeField] private float completionHold = 3.5f;

    [Header("Readback")]
    [Tooltip("Seconds between coverage checks.")]
    [SerializeField] private float readbackInterval = 0.4f;

    [Header("Read-only")]
    [SerializeField] private int debugLevel;
    [SerializeField] private float debugCoverage;
    [SerializeField] private int debugCellsPerFace;
    [SerializeField] private int debugMaxCellsPerFace;

    /// <summary>Current level, starting at 0.</summary>
    public int Level { get; private set; }

    /// <summary>Fraction of the sphere resolved, 0..1. Updated a few times a second.</summary>
    public float Coverage { get; private set; }

    /// <summary>True while the completed image is being held before the wipe.</summary>
    public bool Completing { get; private set; }

    /// <summary>True once the cells are as fine as the simulation buffer can feed.</summary>
    public bool AtFinestLevel => CellsPerFace >= MaxCellsPerFace;

    public int CellsPerFace { get; private set; }
    public int MaxCellsPerFace { get; private set; }

    private static readonly int GazeDirId = Shader.PropertyToID("_GazeDir");
    private static readonly int GazeStateId = Shader.PropertyToID("_GazeState");
    private static readonly int CellsPerFaceId = Shader.PropertyToID("_CellsPerFace");
    private static readonly int ResolveTexId = Shader.PropertyToID("_ResolveTex");

    private AsyncGPUReadbackRequest readback;
    private bool readbackPending;
    private float readbackTimer;

    private float holdTimer;

    private void Awake()
    {
        if (controller == null) controller = GetComponent<RDController>();

        if (resolveMaterial == null)
            Debug.LogError("[RDResolveProgression] No resolve material assigned; nothing will resolve.");

        if (displayMaterial == null)
            Debug.LogError("[RDResolveProgression] No display material assigned.");

        // A cell finer than one simulation texel samples the same value as its neighbour, so this
        // is where the level ladder has to stop. For an equirectangular buffer a texel subtends
        // 2*pi/W, and a cube face edge spans pi/2, which works out to exactly W/4 cells per face.
        int simWidth = simulationTexture != null ? simulationTexture.width : 1024;
        MaxCellsPerFace = Mathf.Max(baseCellsPerFace, simWidth / 4);

        Level = 0;
        ApplyLevel();

        if (displayMaterial != null && resolveTexture != null)
            displayMaterial.SetTexture(ResolveTexId, resolveTexture);

        debugMaxCellsPerFace = MaxCellsPerFace;

        Debug.Log("[RDResolveProgression] Level ladder: " + baseCellsPerFace + " -> " +
                  MaxCellsPerFace + " cells per face (" + (LevelCount) + " levels), set by the " +
                  simWidth + "-wide simulation buffer.");
    }

    /// <summary>How many levels the current simulation resolution can support.</summary>
    public int LevelCount
    {
        get
        {
            int levels = 1;
            int cells = Mathf.Max(2, baseCellsPerFace);

            while (cells * 2 <= MaxCellsPerFace)
            {
                cells *= 2;
                levels++;
            }

            return levels;
        }
    }

    private void Update()
    {
        if (resolveMaterial == null || displayMaterial == null) return;

        PushGaze();
        TickReadback();
        TickCompletion();

        debugLevel = Level;
        debugCoverage = Coverage;
        debugCellsPerFace = CellsPerFace;
    }

    /// <summary>
    /// The ring direction is used rather than the raw gaze, so that in chase mode the resolution
    /// evades the viewer along with the pattern. The test scene ships with chase off, because the
    /// mechanic needs to be legible before it is made difficult.
    /// </summary>
    private void PushGaze()
    {
        bool gazing = controller != null && controller.CurrentState.isGazing;
        Vector3 dir = controller != null ? controller.RingDirection : Vector3.up;

        if (dir.sqrMagnitude < 0.000001f) dir = Vector3.up;

        // Paused while the completed image is held, so the next level starts from a clean wipe
        // rather than from wherever the viewer happened to be looking.
        if (Completing) gazing = false;

        resolveMaterial.SetVector(GazeDirId, new Vector4(dir.x, dir.y, dir.z, 0f));
        resolveMaterial.SetFloat(GazeStateId, gazing ? 1f : 0f);
    }

    private void TickReadback()
    {
        if (resolveTexture == null) return;

        if (readbackPending)
        {
            if (!readback.done) return;

            readbackPending = false;

            if (!readback.hasError)
            {
                NativeArray<byte> data = readback.GetData<byte>();
                Coverage = Average(data);
            }

            return;
        }

        readbackTimer += Time.deltaTime;
        if (readbackTimer < readbackInterval) return;

        readbackTimer = 0f;

        // An explicit target format, so the stride does not depend on how the CRT was created.
        readback = AsyncGPUReadback.Request(resolveTexture, 0, TextureFormat.R8);
        readbackPending = true;
    }

    /// <summary>
    /// Strided, because 131072 bytes every 0.4 s is more precision than a progress bar needs and
    /// every fourth texel gives the same answer to three decimal places.
    /// </summary>
    private static float Average(NativeArray<byte> data)
    {
        if (data.Length == 0) return 0f;

        const int stride = 4;

        long sum = 0;
        int count = 0;

        for (int i = 0; i < data.Length; i += stride)
        {
            sum += data[i];
            count++;
        }

        if (count == 0) return 0f;

        return (float)sum / (count * 255f);
    }

    private void TickCompletion()
    {
        if (Completing)
        {
            holdTimer -= Time.deltaTime;

            if (holdTimer <= 0f)
            {
                Completing = false;
                AdvanceLevel();
            }

            return;
        }

        if (Coverage < completionThreshold) return;
        if (AtFinestLevel) return;          // the ladder is finished; hold the smooth image

        Completing = true;
        holdTimer = completionHold;
    }

    private void AdvanceLevel()
    {
        Level++;
        ApplyLevel();

        // Wipe the progress. The finer grid has to be resolved from nothing.
        if (resolveTexture != null)
            resolveTexture.Initialize();

        Coverage = 0f;

        Debug.Log("[RDResolveProgression] Level " + Level + ": " + CellsPerFace +
                  " cells per face (" + (CellsPerFace * 4) + " around the sphere).");
    }

    private void ApplyLevel()
    {
        int cells = Mathf.Max(2, baseCellsPerFace);

        for (int i = 0; i < Level; i++)
            cells *= 2;

        CellsPerFace = Mathf.Min(cells, MaxCellsPerFace);

        if (displayMaterial != null)
            displayMaterial.SetFloat(CellsPerFaceId, CellsPerFace);
    }

    /// <summary>Back to level 0 with an unresolved surface. For the operator between days.</summary>
    public void ResetProgression()
    {
        Level = 0;
        Completing = false;
        Coverage = 0f;

        ApplyLevel();

        if (resolveTexture != null)
            resolveTexture.Initialize();
    }

    private void OnDisable()
    {
        // Leaving a request in flight across a scene change is how you get a native-array error
        // on the next domain reload.
        if (readbackPending)
        {
            readback.WaitForCompletion();
            readbackPending = false;
        }
    }
}
