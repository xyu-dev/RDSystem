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

    [Tooltip("Print the room as an object - the small sphere the monitor camera sees, pattern " +
             "and tentacles and all - rather than a flat swatch of the texture. A swatch is " +
             "evidence that something was generated; the ball is a picture of the thing they " +
             "stood inside.")]
    [SerializeField] private bool printBallView = true;

    [Tooltip("The monitor camera. Found on the RDMonitorView when left empty.")]
    [SerializeField] private Camera ballCamera;

    [Tooltip("Seconds between snapshots taken during a visit. The receipt prints the most " +
             "complex of them, so this is how finely the visit is sampled - not a cost that " +
             "repeats per frame.")]
    [SerializeField] private float sampleInterval = 4f;

    [Tooltip("The gaze depth field - the record of where the visitor actually looked. The clip " +
             "is centred on its hottest region, so the paper carries the part of the pattern " +
             "they made. Found on the surface material when left empty; without it the crop " +
             "falls back to random.")]
    [SerializeField] private Texture gazeTexture;

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

    [Tooltip("The blob width the pattern usually has, in source pixels. The crop whose shapes " +
             "match this is the one that looks like the piece rather than like a smear or a " +
             "speckle. Doubled on paper by the magnification, so 10 here is a 20 dot blob.")]
    [Range(2f, 40f)] [SerializeField] private float targetBlobPixels = 10f;

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

    // The whole character set a receipt needs: a duration and a visitor number. Drawn by hand,
    // because a bitmap this small prints cleanly at any scale and needs no font asset on the
    // machine.
    //
    // Variable width, not a fixed three. A hash crammed into three columns is two verticals and
    // two horizontals with no gap between them, which prints as a solid square at 203 dpi - the
    // one glyph on the receipt that has to be unmistakable, reduced to a blob. At five columns
    // it has real counters and reads as a hash across the room.
    //
    // The 1 is five columns too, with a serif foot. A bare vertical bar beside a 0 and a 3 does
    // not read as a digit; it reads as a mark where a digit should be.
    private static readonly string[] Glyphs =
    {
        "###", "# #", "# #", "# #", "###",            // 0
        " # ", "## ", " # ", " # ", "###",            // 1
        "###", "  #", "###", "#  ", "###",            // 2
        "###", "  #", "###", "  #", "###",            // 3
        "# #", "# #", "###", "  #", "  #",            // 4
        "###", "#  ", "###", "  #", "###",            // 5
        "###", "#  ", "###", "# #", "###",            // 6
        "###", "  #", "  #", "  #", "  #",            // 7
        "###", "# #", "###", "# #", "###",            // 8
        "###", "# #", "###", "  #", "###",            // 9
        "   ", " # ", "   ", " # ", "   ",            // :
        " # #", "####", " # #", "####", " # #"        // #
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

        if (gazeTexture == null)
            gazeTexture = FindSurfaceTexture("_DepthTex");

        if (ballCamera == null)
        {
            RDMonitorView monitor = FindAnyObjectByType<RDMonitorView>();

            if (monitor != null) ballCamera = monitor.GetComponent<Camera>();
        }

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
        ReleaseBestFrame();

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
    private Texture FindSurfaceTexture(string property = "_MainTex")
    {
        Renderer renderer = GetComponent<Renderer>();

        if (renderer == null)
        {
            RDController controller = FindAnyObjectByType<RDController>();

            if (controller != null)
                renderer = controller.GetComponent<Renderer>();
        }

        Material material = renderer != null ? renderer.sharedMaterial : null;

        if (material != null && material.HasProperty(property))
            return material.GetTexture(property);

        return null;
    }

    private IInteractionHealthSource trackingSource;
    private bool wasTracking;
    private bool hasEverTracked;
    private bool wasWorn = true;
    private float lostFor;
    private float visitSeconds;
    private bool printedThisVisit;

    // The most complex frame seen during this visit, and what it scored.
    private Camera captureCamera;
    private Vector2 gazeUV;
    private bool hasGazeUV;
    private Texture2D bestFrame;
    private float bestFrameScore = -1f;
    private float sampleTimer;

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

                ReleaseBestFrame();
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

            SampleBestFrame(dt);
        }
        else
        {
            lostFor += dt;
        }

        // Taking the headset off, and nothing else.
        //
        // Losing the gaze used to trigger this too, which read as the same event and is not:
        // closing your eyes, resting them, a calibration dropping out mid-visit all look
        // identical to the gaze signal and none of them means the visit is over. A visitor who
        // blinked their way to three receipts had not finished looking.
        //
        // The proximity sensor says the one thing we actually want to know.
        bool worn = headset == null || headset.Worn;

        bool removed = !worn && wasWorn;

        if (removed && hasEverTracked && !printedThisVisit)
        {
            printedThisVisit = true;

            Debug.Log("[RDReceipt] Headset taken off after " + visitSeconds.ToString("0") +
                      "s of visit; printing.");

            // Attended seconds, not elapsed. The session clock runs from the moment it opened
            // and keeps running through blinks, headset adjustments, recalibrations and plain
            // standing still, because it only closes after 25 seconds of absence. Printing that
            // number tells a visitor how long they were in the room, which they already know.
            // This one tells them how long they actually looked, which they do not.
            PrintFor(visitSeconds);

            // Released only after the page has been composed from it.
            ReleaseBestFrame();
        }

        wasTracking = tracking;
        wasWorn = worn;
    }


    /// <summary>
    /// The session-end path. Still wired, because a sensor that never fires - a headset left on
    /// a table face down - would otherwise mean no receipt at all.
    /// </summary>
    public void Print()
    {
        if (printedThisVisit) return;

        // A session ends after 25 seconds without gaze, which a visitor can reach with their
        // eyes shut while still wearing the headset. The fallback exists for a sensor that
        // never releases - a unit left face down on the table - not for someone resting.
        if (headset != null && headset.Worn)
        {
            Debug.Log("[RDReceipt] Session ended but the headset is still on, so no receipt. " +
                      "It prints when the headset comes off.");
            return;
        }

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

            // Path on its own line so it can be selected and copied out of the console in one
            // go. Unity only makes a console link clickable for files inside the project, and
            // receipts deliberately live outside it, so RD > Open Receipts Folder is the link.
            Debug.Log("[RDReceipt] RECEIPT GENERATED - " + FormatDuration(seconds) +
                      " attended, " + new System.IO.FileInfo(path).Length / 1024 + " KB" + System.Environment.NewLine + path);

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
        int inner = receiptWidth - margin * 2;

        // The frame kept from mid-visit, not a fresh capture.
        //
        // By the time the headset is off, RDRecession has already taken the field to rest -
        // diffusion near zero, the dent contracting, the ink thinned - so capturing at the
        // moment of printing hands the visitor a picture of the room emptying. The receipt
        // should show what they made while they were in it.
        Texture2D clip = bestFrame != null
            ? bestFrame
            : (printBallView && ballCamera != null ? GrabBallView(inner) : GrabRandomClip());

        Debug.Log(bestFrame != null
            ? "[RDReceipt] Printing the best frame of the visit, legibility " +
              bestFrameScore.ToString("0.000") + "."
            : "[RDReceipt] No frame was kept during the visit; capturing one now.");

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
        float threshold = ClipThreshold(clip, printBallView && ballCamera != null);

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

        // The visitor number, right aligned on the same line. Two receipts can look alike - two
        // people can look at the same patch for the same length of time - but no two carry the
        // same number, so each one is a record of a particular visit rather than a pretty print.
        string visitor = NextSerial();

        if (!string.IsNullOrEmpty(visitor))
        {
            int width = MeasureText(visitor, digitScale);
            DrawDuration(page, visitor, receiptWidth - margin - width, margin, digitScale);
        }

        page.Apply();

        // The retained frame belongs to the visit and is released with it, not here.
        if (clip != bestFrame) Destroy(clip);

        return page;
    }

    /// <summary>
    /// Keeps the most complex frame of the visit, judged as it happens.
    ///
    /// A visit is not uniformly interesting. It opens on whatever the last visitor left, passes
    /// through whatever this one grows, and ends with the field already settling the moment
    /// they stop looking. Sampling through the visit and keeping the best frame means the
    /// paper carries the pattern at its richest rather than at its last gasp.
    ///
    /// Scored with the same Legibility measure the crop search uses, so "best" means the same
    /// thing in both places: balanced ink, and blobs at the width this field usually makes them.
    /// </summary>
    private void SampleBestFrame(float dt)
    {
        if (!printBallView || ballCamera == null) return;

        sampleTimer += dt;

        if (sampleTimer < Mathf.Max(0.5f, sampleInterval)) return;

        sampleTimer = 0f;

        Texture2D frame = GrabBallView(receiptWidth - margin * 2);
        float score = Legibility(frame);

        if (score <= bestFrameScore)
        {
            Destroy(frame);
            return;
        }

        if (bestFrame != null) Destroy(bestFrame);

        bestFrame = frame;
        bestFrameScore = score;
    }

    /// <summary>Lets go of the retained frame, at the end of a visit or the start of the next.</summary>
    private void ReleaseBestFrame()
    {
        if (bestFrame != null) Destroy(bestFrame);

        bestFrame = null;
        bestFrameScore = -1f;
        sampleTimer = 0f;
    }

    /// <summary>
    /// A camera of our own, aimed at the part of the sphere this visitor actually looked at.
    ///
    /// Two visitors standing in the same room get the same ball from the same angle, so framing
    /// every receipt from the monitor camera made them near enough identical. Orbiting to the
    /// gaze hotspot instead means each receipt frames that person's own patch of the sphere -
    /// unique because their looking was, not because a random number said so.
    ///
    /// Its own camera rather than borrowing the monitor one: moving that would swing the public
    /// screen every time a receipt printed.
    /// </summary>
    private Camera CaptureCamera()
    {
        if (captureCamera == null)
        {
            GameObject go = new GameObject("Receipt Capture Camera");
            go.transform.SetParent(transform, false);
            go.hideFlags = HideFlags.HideAndDontSave;

            captureCamera = go.AddComponent<Camera>();
            captureCamera.enabled = false;
        }

        // Copied each time, so retuning the monitor view retunes the receipt with it.
        captureCamera.CopyFrom(ballCamera);
        captureCamera.enabled = false;
        captureCamera.targetTexture = null;
        captureCamera.clearFlags = CameraClearFlags.SolidColor;
        captureCamera.backgroundColor = Color.white;

        Transform ball = BallTransform();

        if (ball != null)
        {
            float distance = Vector3.Distance(ballCamera.transform.position, ball.position);

            // Through the ball's own rotation, so the patch stays the one they looked at however
            // far round the sphere has turned since.
            Vector3 direction = hasGazeUV
                ? ball.rotation * DirectionFromUV(gazeUV)
                : ballCamera.transform.position - ball.position;

            captureCamera.transform.position = ball.position + direction.normalized * distance;
            captureCamera.transform.rotation =
                Quaternion.LookRotation(ball.position - captureCamera.transform.position, Vector3.up);
        }
        else
        {
            captureCamera.transform.SetPositionAndRotation(ballCamera.transform.position,
                                                           ballCamera.transform.rotation);
        }

        return captureCamera;
    }

    private Transform BallTransform()
    {
        RDMonitorView monitor = FindAnyObjectByType<RDMonitorView>();

        if (monitor == null) return null;

        return monitor.Ball;
    }

    /// <summary>
    /// Renders the monitor ball on demand, square, at the size the paper needs.
    ///
    /// SubmitRenderRequest rather than Camera.Render(): Render() is the built-in pipeline's API,
    /// and calling it mid-frame under URP opens a second pass inside the one already running -
    /// the frame comes apart with "NextSubPass: Not inside a Renderpass". The request API exists
    /// for exactly this, rendering one camera to one target outside the loop.
    ///
    /// The background is forced white for the capture. The monitor view clears to near-black,
    /// which a one-bit threshold turns into a solid black border around the ball: a great deal
    /// of ink for nothing, on paper that fades faster the more of it is burned. White surround
    /// means the ball sits on the paper rather than in a box.
    /// </summary>
    private Texture2D GrabBallView(int size)
    {
        RenderTexture target = RenderTexture.GetTemporary(size, size, 24);

        Camera camera = CaptureCamera();

        UnityEngine.Rendering.RenderPipeline.StandardRequest request =
            new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = target };

        // Static, and deliberately not guarded by IsRenderRequestSupported - that one is
        // protected, for pipelines to implement rather than for callers to ask. If a pipeline
        // cannot serve the request it says so itself; a failed capture must not cost the receipt.
        try
        {
            if (UnityEngine.Rendering.RenderPipelineManager.currentPipeline != null)
                UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera, request);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[RDReceipt] Could not render the ball view (" + e.GetType().Name +
                             "); the receipt will be blank. Untick Print Ball View to fall back " +
                             "to the texture crop.");
        }

        RenderTexture previousActive = RenderTexture.active;
        RenderTexture.active = target;

        Texture2D view = new Texture2D(size, size, TextureFormat.RGBA32, false);
        view.ReadPixels(new Rect(0, 0, size, size), 0, 0);
        view.Apply();

        RenderTexture.active = previousActive;
        RenderTexture.ReleaseTemporary(target);

        return view;
    }

    /// <summary>
    /// Picks the crop: where the visitor looked, and the most characteristic patch of it.
    ///
    /// A random crop was a souvenir of the room. This is a souvenir of their visit - the gaze
    /// depth field is a record of where attention actually landed, so centring the clip on its
    /// hottest region means the paper carries the part of the pattern they made rather than a
    /// patch they may never have turned towards.
    ///
    /// Legibility settles the rest. Several crops are tried around that region and the one that
    /// reads best as the pattern wins - balanced between ink and paper, with blobs at the width
    /// this field usually makes them. See Legibility for why that beats picking the busiest.
    /// </summary>
    private Texture2D GrabRandomClip()
    {
        int w = Mathf.Clamp(clipPixels.x, 8, sourceTexture.width);
        int h = Mathf.Clamp(clipPixels.y, 8, sourceTexture.height);

        Vector2Int hot = FindGazeHotspot(w, h);

        Texture2D best = null;
        float bestScore = -1f;

        for (int attempt = 0; attempt < clipAttempts; attempt++)
        {
            Vector2Int at = hot.x < 0 ? RandomPosition(w, h) : JitterAround(hot, w, h);

            Texture2D candidate = ReadClip(w, h, at);
            float score = Legibility(candidate);

            if (score > bestScore)
            {
                if (best != null) Destroy(best);

                best = candidate;
                bestScore = score;
            }
            else
            {
                Destroy(candidate);
            }
        }

        Debug.Log("[RDReceipt] Clip " + (hot.x < 0 ? "chosen at random" : "taken from the gaze") +
                  ", legibility " + bestScore.ToString("0.000") + ".");

        return best;
    }

    /// <summary>
    /// The coarse cell of the gaze depth field with the most accumulated looking, as the
    /// bottom-left corner of a clip centred on it.
    ///
    /// Scanned on a grid rather than per pixel: the field is a thousand pixels square, the clip
    /// is a couple of hundred, and resolving the hotspot finer than the clip itself would be
    /// precision with nowhere to go. Returns (-1, -1) when there is no gaze texture or nobody
    /// looked at anything, and the caller falls back to a random crop.
    /// </summary>
    private Vector2Int FindGazeHotspot(int w, int h)
    {
        if (gazeTexture == null) return new Vector2Int(-1, -1);

        RenderTexture source = gazeTexture as RenderTexture;
        if (source == null) return new Vector2Int(-1, -1);

        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = source;

        Texture2D field = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
        field.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
        field.Apply();

        RenderTexture.active = previous;

        Color[] pixels = field.GetPixels();
        Destroy(field);

        const int cells = 24;

        int cellW = source.width / cells;
        int cellH = source.height / cells;

        float bestSum = 0f;
        Vector2Int bestCell = new Vector2Int(-1, -1);

        for (int cy = 0; cy < cells; cy++)
        {
            for (int cx = 0; cx < cells; cx++)
            {
                float sum = 0f;

                // Every fourth pixel in each direction. The field is smooth, so a sixteenth of
                // the samples ranks the cells identically and costs a sixteenth of the time.
                for (int y = cy * cellH; y < (cy + 1) * cellH; y += 4)
                {
                    int row = y * source.width;

                    for (int x = cx * cellW; x < (cx + 1) * cellW; x += 4)
                        sum += pixels[row + x].r;
                }

                if (sum > bestSum)
                {
                    bestSum = sum;
                    bestCell = new Vector2Int(cx, cy);
                }
            }
        }

        if (bestCell.x < 0) return bestCell;

        int centreX = (int)((bestCell.x + 0.5f) * cellW);
        int centreY = (int)((bestCell.y + 0.5f) * cellH);

        // Kept in UV so the camera aiming can use the same answer without scanning again.
        gazeUV = new Vector2(centreX / (float)source.width, centreY / (float)source.height);
        hasGazeUV = true;

        return Clamped(centreX - w / 2, centreY - h / 2, w, h);
    }

    /// <summary>
    /// Equirectangular UV to a direction in the sphere's own space. Matches RDSphereMesh, which
    /// is the mapping the simulation is indexed by.
    /// </summary>
    private static Vector3 DirectionFromUV(Vector2 uv)
    {
        float theta = Mathf.Clamp01(uv.y) * Mathf.PI;
        float phi = uv.x * 2f * Mathf.PI;

        float sinTheta = Mathf.Sin(theta);

        return new Vector3(sinTheta * Mathf.Cos(phi), Mathf.Cos(theta), sinTheta * Mathf.Sin(phi));
    }

    private Vector2Int JitterAround(Vector2Int at, int w, int h)
    {
        int spread = Mathf.Max(8, w / 3);

        return Clamped(at.x + UnityEngine.Random.Range(-spread, spread),
                       at.y + UnityEngine.Random.Range(-spread, spread), w, h);
    }

    private Vector2Int RandomPosition(int w, int h)
    {
        return new Vector2Int(UnityEngine.Random.Range(0, Mathf.Max(1, sourceTexture.width - w)),
                              UnityEngine.Random.Range(0, Mathf.Max(1, sourceTexture.height - h)));
    }

    private Vector2Int Clamped(int x, int y, int w, int h)
    {
        return new Vector2Int(Mathf.Clamp(x, 0, Mathf.Max(0, sourceTexture.width - w)),
                              Mathf.Clamp(y, 0, Mathf.Max(0, sourceTexture.height - h)));
    }

    /// <summary>
    /// How well this crop will read as the pattern, rather than how busy it is.
    ///
    /// Busiest was the wrong target. Edge energy peaks on the noisiest crop available, and the
    /// noisiest part of a reaction-diffusion field is where it is breaking down into speckle -
    /// the least characteristic thing it does, and the worst thing to put on a 203 dpi head
    /// where anything finer than a few dots bleeds into grey.
    ///
    /// Two terms, multiplied so that failing either one disqualifies the crop:
    ///
    /// Coverage, peaking at half ink and half paper. A crop that is mostly one or the other has
    /// little shape in it whatever its edges are doing.
    ///
    /// Blob scale, from the mean run length between transitions. This is the term that finds the
    /// typical pattern: a Gray-Scott field at rest is spots and worms of a fairly consistent
    /// width, so a crop whose runs match that width is showing the pattern as it usually looks -
    /// while a slab runs long and speckle runs to a pixel or two.
    /// </summary>
    private float Legibility(Texture2D clip)
    {
        float threshold = ClipThreshold(clip, false);

        Color[] pixels = clip.GetPixels();

        int w = clip.width;
        int h = clip.height;

        int ink = 0;
        int transitions = 0;
        int samples = 0;

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            bool previous = false;

            for (int x = 0; x < w; x++)
            {
                bool isInk = pixels[row + x].g < threshold;

                if (isInk) ink++;
                if (x > 0 && isInk != previous) transitions++;

                previous = isInk;
                samples++;
            }
        }

        if (samples == 0 || transitions == 0) return 0f;

        float coverage = ink / (float)samples;
        float coverageScore = 1f - Mathf.Abs(coverage - 0.5f) * 2f;

        float run = samples / (float)transitions;
        float runScore = 1f - Mathf.Clamp01(Mathf.Abs(run - targetBlobPixels) / targetBlobPixels);

        return coverageScore * runScore;
    }

    /// <summary>
    /// A 2x2 box average of the source, not a point sample.
    ///
    /// The simulation carries a one-texel oscillation - adjacent rows sitting either side of the
    /// value the blob should hold. On the sphere it is invisible, one texel across on a ten metre
    /// surface. Magnified two to one onto paper and then forced to one bit, it became a hard
    /// white line through every black shape: the print read as a halftone rather than as the
    /// pattern, and it halved the ink.
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

    private float ClipThreshold(Texture2D clip, bool ignoreBackground)
    {
        Color[] pixels = clip.GetPixels();

        int[] histogram = new int[256];
        int counted = 0;

        // The ball view sits on white paper that is genuinely white, and counting it would hand
        // most of the ink budget to the surround - the percentile would then drive the threshold
        // deep into the ball and print it almost solid. Excluding it sets the cut from the
        // object's own tones, which is where the picture is. A texture swatch has no surround,
        // so it counts everything.
        for (int i = 0; i < pixels.Length; i++)
        {
            int bucket = Mathf.Clamp(Mathf.RoundToInt(pixels[i].g * 255f), 0, 255);

            if (ignoreBackground && bucket >= 243) continue;

            histogram[bucket]++;
            counted++;
        }

        if (counted == 0) return 0.5f;

        int target = Mathf.RoundToInt(counted * Mathf.Clamp01(inkThreshold));
        int running = 0;

        for (int bucket = 0; bucket < 256; bucket++)
        {
            running += histogram[bucket];

            if (running >= target)
                return (bucket + 0.5f) / 255f;
        }

        return 0.5f;
    }

    private Texture2D ReadClip(int w, int h, Vector2Int at)
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = sourceTexture as RenderTexture;

        Texture2D clip = new Texture2D(w, h, TextureFormat.RGBA32, false);
        clip.ReadPixels(new Rect(at.x, at.y, w, h), 0, 0);
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

    /// <summary>
    /// Width of a string in dots, needed because the glyphs are no longer all one width and
    /// the visitor number is right aligned against the edge of the paper.
    /// </summary>
    private static int MeasureText(string text, int scale)
    {
        int width = 0;

        foreach (char c in text)
        {
            int index = GlyphIndex(c);
            width += (index < 0 ? 4 : Glyphs[index * 5].Length + 1) * scale;
        }

        return width;
    }

    /// <summary>Digits, the colon and the hash. Anything else advances the cursor as a space.</summary>
    private static int GlyphIndex(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c == ':') return 10;
        if (c == '#') return 11;

        return -1;
    }

    private static void DrawDuration(Texture2D page, string text, int x, int y, int scale)
    {
        int cursor = x;

        foreach (char c in text)
        {
            int index = GlyphIndex(c);

            if (index < 0)
            {
                cursor += 4 * scale;
                continue;
            }

            // Each glyph carries its own width in the length of its rows, so the hash can be
            // wider than the digits without the rest of the line shifting.
            int glyphWidth = Glyphs[index * 5].Length;

            for (int row = 0; row < 5; row++)
            {
                string line = Glyphs[index * 5 + row];

                for (int col = 0; col < glyphWidth; col++)
                {
                    if (line[col] != '#') continue;

                    for (int dy = 0; dy < scale; dy++)
                        for (int dx = 0; dx < scale; dx++)
                            page.SetPixel(cursor + col * scale + dx,
                                          y + (4 - row) * scale + dy,
                                          Color.black);
                }
            }

            cursor += (glyphWidth + 1) * scale;
        }
    }

    /// <summary>
    /// The serial on this receipt, zero padded, and unique by construction.
    ///
    /// It used to read RDLongTermMemory.VisitorCount, and two receipts came out as #045. The two
    /// components count different things on different clocks: the memory increments when a
    /// SESSION opens, which needs 1.5 seconds of gaze and does not close until 25 seconds of
    /// absence, while a receipt prints whenever the headset comes off and re-arms after an 8
    /// second gap. Hand the headset to the next person inside that 25 second window and they are
    /// the same visitor as far as the memory is concerned - and they got the same number.
    ///
    /// So the receipt counts receipts. One number is burned per sheet of paper, nothing else can
    /// consume one, and the claim printed on the paper - that no two carry the same number - is
    /// true by construction rather than by coincidence of timing.
    ///
    /// Persisted, because the count has to survive the machine being switched off overnight in
    /// the middle of a run.
    /// </summary>
    private string NextSerial()
    {
        const string key = "RDReceiptSerial";

        int serial = PlayerPrefs.GetInt(key, 0) + 1;

        PlayerPrefs.SetInt(key, serial);
        PlayerPrefs.Save();

        return "#" + serial.ToString("000");
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
        if (string.IsNullOrEmpty(printCommand))
        {
            // Said out loud. Silence here is what made a missing Print Command look like the
            // whole receipt feature being broken, when the PNG was on disk the entire time.
            Debug.Log("[RDReceipt] No Print Command set, so it was saved but not printed. " +
                      "RD > Apply Show Config fills it in.");
            return;
        }


        try
        {
            string arguments = printArguments.Replace("{file}", path);

            // Launched headless. A default Process.Start opens a console window, which takes
            // focus - and the fullscreen monitor window is a drop-down, so losing focus used to
            // shut it. Printing a receipt closed the public screen every time.
            var start = new System.Diagnostics.ProcessStartInfo(printCommand, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };

            Process.Start(start);

            Debug.Log("[RDReceipt] Sent to the printer:" + System.Environment.NewLine + printCommand + " " + arguments);
        }
        catch (Exception e)
        {
            Debug.LogError("[RDReceipt] Print command failed: " + e.Message +
                           " (the PNG is still at " + path + ")");
        }
    }
}
