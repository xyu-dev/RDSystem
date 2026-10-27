using UnityEngine;
using Varjo.XR;

/// <summary>
/// Step outside the circle and the room goes away: the headset falls back to its own cameras and
/// the visitor sees the gallery they are actually standing in.
///
/// This is a safety boundary, so it is built to fail safe in both directions. Leaving shows the
/// real room, which is what someone about to walk into a wall needs. Losing the Varjo runtime, or
/// running on a desk with no headset at all, leaves the piece exactly as it was rather than
/// blanking it - an exception here must never take the work down.
///
/// Three things have to happen together for see-through to be visible, and missing any one of
/// them looks like the feature not working rather than like a mistake:
///
///   - the compositor must be told the submitted frame is no longer opaque, or it composites the
///     app over black and there is nothing to see through TO;
///   - the camera must clear to transparent, or the app paints over the video itself;
///   - the surface must be hidden, because the visitor is INSIDE a ten metre sphere and it would
///     otherwise still be wrapped around them, see-through or not.
///
/// Hysteresis rather than a bare radius: someone standing exactly on the line would otherwise
/// flip the room on and off with the sway of normal standing, which is both ugly and, for
/// something meant to warn, actively confusing.
/// </summary>
public class RDBoundary : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The headset. Normally the Main Camera.")]
    [SerializeField] private Transform head;

    [Tooltip("Centre of the safe area. Normally the camera rig, which is where the piece is " +
             "composed around - not the floor marker.")]
    [SerializeField] private Transform centre;

    [Tooltip("The room sphere. Hidden while outside, or the visitor sees the inside of it " +
             "instead of the gallery.")]
    [SerializeField] private Renderer roomSurface;

    [Tooltip("The headset camera, cleared to transparent while outside.")]
    [SerializeField] private Camera headsetCamera;

    [Header("Boundary")]
    [Tooltip("Metres from the centre before the room gives way.")]
    [SerializeField] private float radius = 1.1f;

    [Tooltip("How far back inside they must come before the room returns. Stops someone standing " +
             "on the line flickering between the two.")]
    [SerializeField] private float hysteresis = 0.25f;

    [Tooltip("Ignore height. A visitor crouching or on tiptoe has not left the safe area, and " +
             "counting it means tall people trip the boundary standing still.")]
    [SerializeField] private bool horizontalOnly = true;

    [Header("Warning")]
    [Tooltip("Shown in the headset while outside. English first, because the exhibition label " +
             "and the operator both work in English; German underneath at the same size, not " +
             "smaller - a safety message that ranks one language above the other is a safety " +
             "message half the visitors skim past.")]
    [SerializeField] private string warningEnglish = "Outside the tracking area";

    [SerializeField] private string warningGerman = "Au\u00DFerhalb des Tracking-Bereichs";

    [Tooltip("Second line, in both languages. Tells them what to do about it.")]
    [SerializeField] private string instructionEnglish = "Please step back";

    [SerializeField] private string instructionGerman = "Bitte zur\u00FCcktreten";

    [Tooltip("Metres in front of the eyes. Closer than the sphere, far enough to focus on.")]
    [SerializeField] private float warningDistance = 2f;

    [Tooltip("Glyph resolution. Low on purpose: 16px magnified with point filtering is what " +
             "makes this read as a system warning rather than as part of the work.")]
    [SerializeField] private int warningPixelSize = 16;

    [SerializeField] private float warningSize = 0.06f;

    [Tooltip("How many copies around the circle. Three means one is never more than a third of " +
             "a turn from wherever the visitor happens to be facing.")]
    [Range(1, 6)] [SerializeField] private int warningCopies = 3;

    [Tooltip("Degrees per second the ring turns. Fast enough to read as an alarm, slow enough " +
             "to read at all.")]
    [SerializeField] private float warningSpin = 28f;

    [SerializeField] private Color warningColor = new Color(1f, 0.72f, 0.40f, 1f);

    [Header("Stop")]
    [Tooltip("Components switched off while the visitor is outside, and switched back on when " +
             "they return. Left empty, the controller and the simulation updater are found.")]
    [SerializeField] private MonoBehaviour[] pauseWhileOutside;

    [Header("Read-only")]
    [SerializeField] private bool debugOutside;
    [SerializeField] private float debugDistance;

    public bool IsOutside => debugOutside;

    private bool outside;
    private bool applied;
    private Transform warning;
    private float warningAngle;

    // Probed once. IsMRReady logs an error every time it is called on a headset without
    // cameras, so calling it per boundary crossing fills the console with a hardware fact that
    // does not change between frames.
    private int mixedReality = -1;

    private bool MixedRealityAvailable
    {
        get
        {
            if (mixedReality < 0)
            {
                try
                {
                    mixedReality = VarjoMixedReality.IsMRAvailable() ? 1 : 0;
                }
                catch (System.Exception)
                {
                    mixedReality = 0;
                }

                if (mixedReality == 0)
                    Debug.Log("[RDBoundary] This headset has no passthrough cameras, so leaving " +
                              "the boundary hides the room rather than showing the gallery.");
            }

            return mixedReality == 1;
        }
    }
    private Color clearColour = Color.black;

    private void Awake()
    {
        if (head == null && Camera.main != null) head = Camera.main.transform;
        if (headsetCamera == null && Camera.main != null) headsetCamera = Camera.main;
        if (centre == null && head != null) centre = head.parent;

        if (headsetCamera != null) clearColour = headsetCamera.backgroundColor;

        if (pauseWhileOutside == null || pauseWhileOutside.Length == 0)
        {
            // The two that make the piece look alive: the controller, which paints, and the
            // updater, which steps the field. Stopping both freezes the room exactly as it was,
            // so stepping back in resumes rather than restarts.
            RDController controller = FindAnyObjectByType<RDController>();
            global::RDSystem.RDSystemUpdater updater =
                FindAnyObjectByType<global::RDSystem.RDSystemUpdater>();

            pauseWhileOutside = new MonoBehaviour[]
            {
                controller,
                updater
            };
        }
    }

    private void OnDisable()
    {
        // Never leave the headset in see-through because a component was switched off.
        if (applied) SetPassthrough(false);
    }

    private void Update()
    {
        if (head == null || centre == null) return;

        Vector3 offset = head.position - centre.position;

        if (horizontalOnly) offset.y = 0f;

        debugDistance = offset.magnitude;

        // Asymmetric thresholds: out at the radius, back in only once well inside it.
        if (!outside && debugDistance > radius)
            outside = true;
        else if (outside && debugDistance < radius - hysteresis)
            outside = false;

        debugOutside = outside;

        if (outside != applied) SetPassthrough(outside);

        if (outside) TickWarning();
    }

    /// <summary>
    /// The warning the visitor actually reads, in both languages at once.
    ///
    /// Orbiting the head rather than pinned in front of it. Someone who has walked out of the
    /// tracked area is by definition somewhere the piece cannot predict, so a sign hung in the
    /// room would be behind them half the time - but text welded to the face is also the one
    /// thing in VR people reliably try to look away from, and it follows. Circling them is the
    /// compromise that works: it sweeps through their view wherever they are facing, and the
    /// motion itself reads as an alarm rather than as part of the work.
    ///
    /// Copies spaced evenly around the circle so one is never more than half a turn away.
    ///
    /// Pixel type, point filtered: 16px glyphs magnified with no smoothing. The piece is all soft
    /// continuous fields, so hard pixel edges are the furthest thing from it available - nobody
    /// mistakes this for the artwork, which is the entire job of a safety message.
    ///
    /// No backdrop panel: the room surface is already hidden whenever this is visible, so the
    /// text sits on black, or on the gallery where the headset has cameras.
    /// </summary>
    private void BuildWarning()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        if (font == null)
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");

        if (font == null)
        {
            Debug.LogWarning("[RDBoundary] No builtin font; the boundary will stop the piece " +
                             "without explaining why.");
            return;
        }

        // At the scene root, not under the head: parented to the head it would inherit head
        // rotation, and the orbit would be cancelled out by the visitor simply turning round.
        GameObject root = new GameObject("Boundary Warning");
        root.layer = head.gameObject.layer;

        warning = root.transform;

        int count = Mathf.Max(1, warningCopies);

        for (int i = 0; i < count; i++)
        {
            GameObject arm = new GameObject("Copy " + i);
            arm.transform.SetParent(warning, false);
            arm.transform.localRotation = Quaternion.Euler(0f, i * (360f / count), 0f);
            arm.layer = root.layer;

            // English above, German below, same size. A safety message that ranks one language
            // above the other is a safety message half the visitors skim past.
            BuildLine(font, arm.transform, warningEnglish + "\n" + instructionEnglish, 0.22f);
            BuildLine(font, arm.transform, warningGerman + "\n" + instructionGerman, -0.22f);
        }

        ApplyPointFiltering(font);
        Font.textureRebuilt += OnFontTextureRebuilt;

        warning.gameObject.SetActive(false);
    }

    private void BuildLine(Font font, Transform parent, string text, float y)
    {
        GameObject go = new GameObject("Line");
        go.transform.SetParent(parent, false);

        // Pushed out along the arm's own forward, so the text faces back at the visitor: Unity's
        // TextMesh is read from its -Z side, which is the side now pointing at the centre.
        go.transform.localPosition = new Vector3(0f, y, warningDistance);
        go.layer = parent.gameObject.layer;

        TextMesh line = go.AddComponent<TextMesh>();
        line.font = font;
        line.text = text;
        line.fontSize = Mathf.Max(8, warningPixelSize);
        line.characterSize = warningSize;
        line.anchor = TextAnchor.MiddleCenter;
        line.alignment = TextAlignment.Center;
        line.color = warningColor;

        MeshRenderer renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = font.material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    private void OnFontTextureRebuilt(Font rebuilt) => ApplyPointFiltering(rebuilt);

    /// <summary>Point filtering is the pixel look; bilinear on a magnified atlas is just blur.</summary>
    private static void ApplyPointFiltering(Font font)
    {
        Texture atlas = font != null && font.material != null ? font.material.mainTexture : null;

        if (atlas != null) atlas.filterMode = FilterMode.Point;
    }

    private void ShowWarning(bool show)
    {
        if (show && warning == null && head != null) BuildWarning();

        if (warning != null) warning.gameObject.SetActive(show);
    }

    /// <summary>
    /// Keeps the ring centred on the visitor and turning.
    ///
    /// unscaledDeltaTime on purpose: stopping the piece is allowed to stop time, and a warning
    /// that freezes with everything else is a warning nobody reads.
    /// </summary>
    private void TickWarning()
    {
        if (warning == null || head == null) return;

        warningAngle = Mathf.Repeat(warningAngle + warningSpin * Time.unscaledDeltaTime, 360f);

        warning.position = head.position;
        warning.rotation = Quaternion.Euler(0f, warningAngle, 0f);
    }

    private void OnDestroy()
    {
        Font.textureRebuilt -= OnFontTextureRebuilt;

        if (warning != null) Destroy(warning.gameObject);
    }

    /// <summary>
    /// Stops the piece while the visitor is out of bounds, and resumes it when they return.
    ///
    /// Switching components off rather than touching Time.timeScale: the scale is global, and a
    /// frozen clock would also freeze the warning that is supposed to be telling them to move.
    /// Disabling leaves every value exactly where it was, so coming back resumes the same room
    /// instead of restarting it - which matters, because what the visitor walked away from is
    /// the thing they spent two minutes making.
    /// </summary>
    private void SetSystemRunning(bool running)
    {
        if (pauseWhileOutside == null) return;

        for (int i = 0; i < pauseWhileOutside.Length; i++)
        {
            if (pauseWhileOutside[i] != null)
                pauseWhileOutside[i].enabled = running;
        }
    }

    private void SetPassthrough(bool on)
    {
        applied = on;

        if (roomSurface != null) roomSurface.enabled = !on;

        ShowWarning(on);
        SetSystemRunning(!on);

        if (headsetCamera != null)
        {
            headsetCamera.clearFlags = CameraClearFlags.SolidColor;

            // Alpha zero is the part that matters; the compositor blends the video in behind it.
            headsetCamera.backgroundColor = on
                ? new Color(0f, 0f, 0f, 0f)
                : clearColour;
        }

        // Asked once, not per crossing: without cameras there is nothing to turn on, and the
        // hiding of the surface above is the whole of the warning this headset can give.
        if (!MixedRealityAvailable) return;

        try
        {
            if (on)
            {
                VarjoMixedReality.StartRender();
                VarjoRendering.SetOpaque(false);
            }
            else
            {
                VarjoRendering.SetOpaque(true);
                VarjoMixedReality.StopRender();
            }
        }
        catch (System.Exception e)
        {
            Debug.Log("[RDBoundary] See-through unavailable (" + e.GetType().Name + ").");
        }
    }
}
