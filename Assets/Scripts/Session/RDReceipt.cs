using System;
using System.IO;
using UnityEngine;
using Process = System.Diagnostics.Process;

/// <summary>
/// What the visitor takes away: a 57 mm receipt with a piece of the surface they were looking at
/// and how long they stayed.
///
/// The clip is a random crop of the live simulation, not a render of the whole sphere. That is
/// the point: a receipt is an extract, evidence that a transaction occurred, and nobody gets the
/// whole picture. It is also honest about what the machine kept - a patch of texture and a
/// duration, which is very nearly all this piece actually retains about a person.
///
/// 384 pixels wide because a 58 mm thermal printer rasterises at 203 dpi, which gives 384 dots
/// across the printable 48 mm of a 57 mm roll. Printing wider does not widen the paper; it
/// crops.
///
/// Printing itself is deliberately a shell-out. Thermal printers are either an installed Windows
/// printer or a raw ESC/POS device depending on the model, and guessing wrong means a receipt
/// that silently never prints. Leave Print Command empty and the PNG is simply written to disk,
/// which is also how you check the composition before any paper is involved.
/// </summary>
public class RDReceipt : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Raised when a visit ends. The receipt is printed on that signal.")]
    [SerializeField] private RDPresenceTracker presenceTracker;

    [Tooltip("The live simulation buffer. The clip is cropped out of this.")]
    [SerializeField] private Texture sourceTexture;

    [Tooltip("Whose tracking health marks the end of a visit. Normally the RDInputSourceRouter " +
             "or the gaze input itself. Found automatically when left empty.")]
    [SerializeField] private MonoBehaviour trackingSourceBehaviour;

    [Tooltip("Seconds of lost tracking that mean a DIFFERENT person is about to put the headset " +
             "on. Below this it is the same visitor blinking or being recalibrated; above it, " +
             "the receipt re-arms and the clock starts again.")]
    [SerializeField] private float newVisitorGap = 8f;

    [Tooltip("The headset gate. When assigned, the receipt prints the moment the headset comes " +
             "off its proximity sensor, which is when the visitor is still standing there to " +
             "take it. Without it the receipt waits for the session to end, 25 seconds of " +
             "absence later, by which time they have walked away.")]
    [SerializeField] private RDHeadsetSimulator headset;

    [Header("Paper")]
    [Tooltip("Printable width in dots. 384 is 48 mm at 203 dpi, the printable area of a 57 mm roll.")]
    [SerializeField] private int receiptWidth = 384;

    [Tooltip("Size of the crop taken out of the 1024 simulation, in source pixels. Pick a value " +
             "that divides the printable width exactly - 176 scales by 2 to fill 352 dots. A " +
             "value that does not divide evenly leaves white down one side, because the clip is " +
             "magnified by whole pixels only and never resampled.")]
    [SerializeField] private Vector2Int clipPixels = new Vector2Int(176, 176);

    [Tooltip("Margin around everything, in dots.")]
    [SerializeField] private int margin = 16;

    [Header("Threshold")]
    [Tooltip("A thermal printer has one ink and no greys. Dithering is left off on purpose - a " +
             "hard threshold keeps the ink blobs as shapes, which is what the pattern is.")]
    [Range(0f, 1f)] [SerializeField] private float inkThreshold = 0.55f;

    [Tooltip("How many crops to try before settling. Each one is a texture read, so this is not " +
             "free, but a black receipt is worth far less than a few milliseconds.")]
    [Range(1, 16)] [SerializeField] private int clipAttempts = 6;

    [Tooltip("Spread of light to dark that counts as a usable crop. Reached early, the search " +
             "stops; never reached, the best of the attempts is printed anyway.")]
    [Range(0.05f, 1f)] [SerializeField] private float acceptableContrast = 0.35f;

    [Tooltip("Invert if your prints come out mostly black.")]
    [SerializeField] private bool invert;

    [Header("Output")]
    [Tooltip("Folder for the PNGs. Empty means <persistentDataPath>/Receipts.")]
    [SerializeField] private string outputFolder = "";

    [Tooltip("Executable to print with. Empty means save the file and print nothing.")]
    [SerializeField] private string printCommand = "";

    [Tooltip("Arguments. {file} is replaced with the full path of the PNG.")]
    [SerializeField] private string printArguments = "\"{file}\"";

    [Tooltip("Shortest visit worth a receipt. Someone who glanced in and left does not get one.")]
    [SerializeField] private float minimumSeconds = 20f;

    // The whole character set a receipt needs: a duration. Three by five, drawn by hand, because
    // a bitmap this small prints cleanly at any scale and needs no font asset on the machine.
    private static readonly string[] Glyphs =
    {
        "###", "# #", "# #", "# #", "###",   // 0
        "  #", "  #", "  #", "  #", "  #",   // 1
        "###", "  #", "###", "#  ", "###",   // 2
        "###", "  #", "###", "  #", "###",   // 3
        "# #", "# #", "###", "  #", "  #",   // 4
        "###", "#  ", "###", "  #", "###",   // 5
        "###", "#  ", "###", "# #", "###",   // 6
        "###", "  #", "  #", "  #", "  #",   // 7
        "###", "# #", "###", "# #", "###",   // 8
        "###", "# #", "###", "  #", "###",   // 9
        "   ", " # ", "   ", " # ", "   "    // :
    };

    private void OnEnable()
    {
        if (presenceTracker == null)
            presenceTracker = FindAnyObjectByType<RDPresenceTracker>();

        if (headset == null)
            headset = GetComponent<RDHeadsetSimulator>();

        if (headset == null)
            headset = FindAnyObjectByType<RDHeadsetSimulator>();

        if (sourceTexture == null)
            sourceTexture = FindSurfaceTexture();

        if (trackingSourceBehaviour == null)
            trackingSourceBehaviour = FindAnyObjectByType<RDInputSourceRouter>();

        trackingSource = trackingSourceBehaviour as IInteractionHealthSource;

        if (trackingSource == null)
            trackingSource = headset as IInteractionHealthSource;

        if (trackingSource == null)
            Debug.LogError("[RDReceipt] No tracking health source; nothing will mark the end " +
                           "of a visit, so only the session-end fallback can print.");

        if (presenceTracker == null)
        {
            Debug.LogError("[RDReceipt] No RDPresenceTracker; nothing will tell me a visit ended.");
            return;
        }

        presenceTracker.onSessionEnd.AddListener(Print);

        // One visitor, one receipt. The flag is cleared when a NEW session opens - not when
        // tracking recovers, which was the bug: every dropout and auto-recalibration during a
        // single visit re-armed the trigger, and a long visit printed one receipt per dropout.
        presenceTracker.onSessionStart.AddListener(OnSessionStarted);
    }

    private void OnDisable()
    {
        if (presenceTracker != null)
            presenceTracker.onSessionEnd.RemoveListener(Print);

        if (presenceTracker != null)
            presenceTracker.onSessionStart.RemoveListener(OnSessionStarted);
    }

    /// <summary>
    /// The texture the surface is actually displaying.
    ///
    /// Taken from the renderer rather than from a buffer named in the Inspector, so the clip is
    /// by construction a piece of what the visitor was looking at. A hand-assigned field is one
    /// more thing that can be left empty - which is exactly how this failed - or, worse, point
    /// at a buffer the surface stopped using three display changes ago.
    /// </summary>
    private Texture FindSurfaceTexture()
    {
        Renderer renderer = GetComponent<Renderer>();

        if (renderer == null)
        {
            RDController controller = FindAnyObjectByType<RDController>();

            if (controller != null)
                renderer = controller.GetComponent<Renderer>();
        }

        Material material = renderer != null ? renderer.sharedMaterial : null;

        if (material != null && material.HasProperty("_MainTex"))
            return material.GetTexture("_MainTex");

        return null;
    }

    private IInteractionHealthSource trackingSource;
    private bool wasTracking;
    private bool hasEverTracked;
    private float lostFor;
    private float visitSeconds;
    private bool printedThisVisit;

    /// <summary>
    /// The visit ends when the gaze does.
    ///
    /// Printing on the falling edge of tracking health rather than on the proximity sensor,
    /// because losing the gaze IS the end of the encounter as this piece experiences it: the
    /// headset comes off, Varjo drops the calibration, and the status goes to "not calibrated -
    /// run calibration for this visitor". That moment is also when the next person is about to
    /// be handed the headset, so the paper and the queue line up.
    ///
    /// The hasEverTracked guard is what stops the first run printing. On launch nothing is
    /// calibrated yet, so tracking is already false and stays false until somebody puts the
    /// headset on - without the guard, the very first calibration would look like the end of a
    /// visit that never happened and spit out a receipt for an empty room.
    /// </summary>
    private void OnSessionStarted() => printedThisVisit = false;

    private void Update()
    {
        if (trackingSource == null) return;

        float dt = Time.deltaTime;
        bool tracking = trackingSource.IsTracking;

        if (tracking)
        {
            // A gap long enough to get a headset off one head and onto another re-arms the
            // receipt. Waiting for the session to close instead meant the next visitor got
            // nothing whenever they arrived inside RDPresenceTracker's 25 second window.
            // Only a gap AFTER a receipt means a new person.
            //
            // A gap before one means the same visitor lost calibration and got it back, which
            // happens repeatedly on a headset that keeps dropping. Resetting their clock there
            // was why no receipt ever appeared: every recalibration put the timer back to zero,
            // so it never reached Minimum Seconds and the visit was never long enough to print.
            if (!wasTracking && lostFor >= newVisitorGap && printedThisVisit)
            {
                printedThisVisit = false;
                visitSeconds = 0f;
            }

            hasEverTracked = true;
            lostFor = 0f;

            // The receipt keeps its own clock.
            //
            // Attended seconds undercounted badly: it only advanced while gaze was landing on
            // the surface, so looking around the room, holding still, or any dropout inside the
            // grace all vanished from the total. Elapsed session time overcounted just as badly,
            // because the session spans every visitor until 25 seconds of silence. Time spent
            // with the headset on and tracking is the thing a visitor would call "how long I
            // was in there", and it is bounded by exactly the two edges this component watches.
            visitSeconds += dt;
        }
        else
        {
            lostFor += dt;
        }

        if (!tracking && wasTracking && hasEverTracked && !printedThisVisit)
        {
            printedThisVisit = true;

            Debug.Log("[RDReceipt] Tracking lost after " + visitSeconds.ToString("0") +
                      "s of visit; printing.");

            // Attended seconds, not elapsed. The session clock runs from the moment it opened
            // and keeps running through blinks, headset adjustments, recalibrations and plain
            // standing still, because it only closes after 25 seconds of absence. Printing that
            // number tells a visitor how long they were in the room, which they already know.
            // This one tells them how long they actually looked, which they do not.
            PrintFor(visitSeconds);
        }

        wasTracking = tracking;
    }


    /// <summary>
    /// The session-end path. Still wired, because a sensor that never fires - a headset left on
    /// a table face down - would otherwise mean no receipt at all.
    /// </summary>
    public void Print()
    {
        if (printedThisVisit) return;

        printedThisVisit = true;

        PrintFor(visitSeconds);
    }

    /// <summary>
    /// Prints whatever is on the surface right now, however short the visit.
    ///
    /// For testing the whole chain - Unity to PNG to the printer - without needing a visitor
    /// and without waiting out the minimum.
    /// </summary>
    public void PrintNow() => PrintFor(Mathf.Max(visitSeconds, 1f), true);

    private void PrintFor(float seconds, bool ignoreMinimum = false)
    {
        if (!ignoreMinimum && seconds < minimumSeconds)
        {
            Debug.Log("[RDReceipt] Visit was " + seconds.ToString("0") + "s; below the " +
                      minimumSeconds + "s minimum, so no receipt.");
            return;
        }

        if (sourceTexture == null)
        {
            Debug.LogError("[RDReceipt] No source texture and none found on the surface " +
                           "renderer. Assign Source Texture to Assets/RDSystem/RDSystemBuffer, " +
                           "or put this component on the object that has the display material.");
            return;
        }

        try
        {
            Texture2D receipt = Compose(seconds);
            string path = Write(receipt);

            Destroy(receipt);

            Debug.Log("[RDReceipt] " + FormatDuration(seconds) + " receipt written to " + path);

            SendToPrinter(path);
        }
        catch (Exception e)
        {
            // A failed receipt must never take the room down with it.
            Debug.LogError("[RDReceipt] Receipt failed: " + e.Message);
        }
    }

    // ------------------------------------------------------------ composition

    private Texture2D Compose(float seconds)
    {
        Texture2D clip = GrabRandomClip();

        int inner = receiptWidth - margin * 2;
        int scale = Mathf.Max(1, inner / clip.width);
        int clipSize = clip.width * scale;

        const int digitScale = 6;
        int digitHeight = 5 * digitScale;

        int height = margin + clipSize + margin + digitHeight + margin;

        Texture2D page = new Texture2D(receiptWidth, height, TextureFormat.RGB24, false);
        Fill(page, Color.white);

        // Threshold against the crop's own range rather than a fixed level. The field drifts
        // light and dark over a session, so one absolute number prints a clean pattern early and
        // a solid rectangle later. Taken relative to this crop, there is always ink and always
        // paper; inkThreshold now biases where between its darkest and lightest the cut falls.
        float threshold = ClipThreshold(clip);

        // Nearest neighbour on purpose. The clip is already a few hundred pixels of a continuous
        // field; smoothing it before a one-bit threshold just rounds the edges of the blobs.
        for (int y = 0; y < clipSize; y++)
        {
            for (int x = 0; x < clipSize; x++)
            {
                float value = SampleSmoothed(clip, x / scale, y / scale);
                bool ink = invert ? value > threshold : value < threshold;

                page.SetPixel(margin + x, margin + digitHeight + margin + y,
                              ink ? Color.black : Color.white);
            }
        }

        DrawDuration(page, FormatDuration(seconds), margin, margin, digitScale);

        page.Apply();
        Destroy(clip);

        return page;
    }

    private Texture2D GrabRandomClip()
    {
        // A reaction-diffusion field is not uniformly interesting. Large stretches of it are
        // solid - all substrate or all blob - and a crop that lands in one prints as a black
        // rectangle or an empty one. Neither is a souvenir. So sample a few places and keep the
        // one with the most contrast rather than trusting a single throw of the dice.
        int w = Mathf.Clamp(clipPixels.x, 8, sourceTexture.width);
        int h = Mathf.Clamp(clipPixels.y, 8, sourceTexture.height);

        Texture2D best = null;
        float bestContrast = -1f;

        for (int attempt = 0; attempt < clipAttempts; attempt++)
        {
            Texture2D candidate = ReadClip(w, h);
            float contrast = Contrast(candidate);

            if (contrast > bestContrast)
            {
                if (best != null) Destroy(best);

                best = candidate;
                bestContrast = contrast;
            }
            else
            {
                Destroy(candidate);
            }

            // Good enough: stop paying for reads once the crop clearly has a pattern in it.
            if (bestContrast >= acceptableContrast) break;
        }

        return best;
    }

    /// <summary>
    /// A 2x2 box average of the source, not a point sample.
    ///
    /// The simulation carries a one-texel oscillation - adjacent rows sitting either side of
    /// the value the blob should hold. On the sphere it is invisible, one texel across on a ten
    /// metre surface. Magnified two to one onto paper and then forced to one bit, it became a
    /// hard white line through every black shape: the print read as a halftone rather than as
    /// the pattern, and it halved the ink.
    ///
    /// Averaging first removes the oscillation and leaves the blob edges, which is the whole of
    /// what a one-bit print can carry anyway.
    /// </summary>
    private static float SampleSmoothed(Texture2D clip, int x, int y)
    {
        int x1 = Mathf.Min(x + 1, clip.width - 1);
        int y1 = Mathf.Min(y + 1, clip.height - 1);

        return (clip.GetPixel(x, y).g + clip.GetPixel(x1, y).g +
                clip.GetPixel(x, y1).g + clip.GetPixel(x1, y1).g) * 0.25f;
    }

    private float ClipThreshold(Texture2D clip)
    {
        Color[] pixels = clip.GetPixels();

        float min = 1f;
        float max = 0f;

        for (int i = 0; i < pixels.Length; i++)
        {
            float v = pixels[i].g;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        return Mathf.Lerp(min, max, Mathf.Clamp01(inkThreshold));
    }

    /// <summary>Spread of the display channel across the crop. Flat region, flat number.</summary>
    private static float Contrast(Texture2D clip)
    {
        Color[] pixels = clip.GetPixels();

        float min = 1f;
        float max = 0f;

        for (int i = 0; i < pixels.Length; i++)
        {
            float v = pixels[i].g;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        return max - min;
    }

    private Texture2D ReadClip(int w, int h)
    {
        int x = UnityEngine.Random.Range(0, Mathf.Max(1, sourceTexture.width - w));
        int y = UnityEngine.Random.Range(0, Mathf.Max(1, sourceTexture.height - h));

        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = sourceTexture as RenderTexture;

        Texture2D clip = new Texture2D(w, h, TextureFormat.RGBA32, false);
        clip.ReadPixels(new Rect(x, y, w, h), 0, 0);
        clip.Apply();

        RenderTexture.active = previous;

        return clip;
    }

    private static void Fill(Texture2D texture, Color color)
    {
        Color[] pixels = new Color[texture.width * texture.height];

        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = color;

        texture.SetPixels(pixels);
    }

    private static void DrawDuration(Texture2D page, string text, int x, int y, int scale)
    {
        int cursor = x;

        foreach (char c in text)
        {
            int index = c == ':' ? 10 : c - '0';

            if (index < 0 || index > 10)
            {
                cursor += 4 * scale;
                continue;
            }

            for (int row = 0; row < 5; row++)
            {
                string line = Glyphs[index * 5 + row];

                for (int col = 0; col < 3; col++)
                {
                    if (line[col] != '#') continue;

                    for (int dy = 0; dy < scale; dy++)
                        for (int dx = 0; dx < scale; dx++)
                            page.SetPixel(cursor + col * scale + dx,
                                          y + (4 - row) * scale + dy,
                                          Color.black);
                }
            }

            cursor += 4 * scale;
        }
    }

    private static string FormatDuration(float seconds)
    {
        int whole = Mathf.Max(0, Mathf.RoundToInt(seconds));
        return (whole / 60).ToString("00") + ":" + (whole % 60).ToString("00");
    }

    // ------------------------------------------------------------ output

    private string Write(Texture2D page)
    {
        string folder = string.IsNullOrEmpty(outputFolder)
            ? Path.Combine(Application.persistentDataPath, "Receipts")
            : outputFolder;

        Directory.CreateDirectory(folder);

        string path = Path.Combine(folder,
            "receipt_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");

        File.WriteAllBytes(path, page.EncodeToPNG());

        return path;
    }

    private void SendToPrinter(string path)
    {
        if (string.IsNullOrEmpty(printCommand)) return;

        try
        {
            Process.Start(printCommand, printArguments.Replace("{file}", path));
        }
        catch (Exception e)
        {
            Debug.LogError("[RDReceipt] Print command failed: " + e.Message +
                           " (the PNG is still at " + path + ")");
        }
    }
}
