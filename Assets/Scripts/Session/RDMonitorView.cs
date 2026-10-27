using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// The second screen: the room seen from outside, slowly turning, for the people who are not
/// wearing the headset.
///
/// It renders a SECOND sphere, not the one the visitor is inside. That sounds wasteful and is
/// the only thing that works: the visitor's sphere is 10 m across with the camera at its centre,
/// so no camera outside it can frame it without either flying through the surface or needing a
/// 40 m room. This one is a small copy a couple of metres across, on its own layer, with its own
/// camera pointed at it - and because it shares the simulation texture, it is not a copy of the
/// pattern but the same pattern.
///
/// The headset never sees it: the HMD camera's culling mask excludes the layer, and this camera
/// renders nothing else.
/// </summary>
[RequireComponent(typeof(Camera))]
public class RDMonitorView : MonoBehaviour
{
    [Header("Subject")]
    [Tooltip("The small sphere this camera looks at. Rotates; the visitor's own sphere does not.")]
    [SerializeField] private Transform ball;

    [Tooltip("Seconds for one revolution. 60 reads as drift rather than as animation, while " +
             "still bringing the far side round inside the time someone stands and watches.")]
    [SerializeField] private float secondsPerRevolution = 60f;

    [Tooltip("Axis of rotation, in the ball's own space. Vertical, so the pattern travels " +
             "sideways and the poles stay where they are.")]
    [SerializeField] private Vector3 spinAxis = Vector3.up;

    /// <summary>The sphere the monitor camera looks at. The receipt frames it from its own angle.</summary>
    public Transform Ball => ball;

    [Header("Display")]
    [Tooltip("Which physical display. 1 is the second monitor; the headset takes display 0.")]
    [Range(0, 7)] [SerializeField] private int targetDisplay = 1;

    [Tooltip("Activate the second display on start. Unity leaves extra displays off until asked.")]
    [SerializeField] private bool activateDisplay = true;

    [Tooltip("In a BUILD, give the ball the main window and send the room to the headset only. " +
             "On a one screen machine this is the whole layout: the visitor sees the room " +
             "through the lenses, everyone else sees the ball on the monitor, and neither is a " +
             "mirror of the other. Unity would otherwise fill the window with a copy of what " +
             "the headset is showing, which is the least interesting of the two views and the " +
             "one the public cannot make sense of. No effect in the Editor, where the Game " +
             "view belongs to whoever is working.")]
    [SerializeField] private bool takeOverMainWindow = true;

    [Header("Cost")]
    [Tooltip("Render every Nth frame. An observer view does not need 90 Hz, and the headset's " +
             "frame budget is the one that cannot be compromised. 3 gives about 30 fps at 90.")]
    [Range(1, 6)] [SerializeField] private int renderEveryNthFrame = 3;

#if UNITY_EDITOR
    [Header("Editor Preview")]
    [Tooltip("Render the monitor view into a texture the RD Monitor window can draw. The Editor " +
             "reports exactly one display however many screens are attached, so Display 2 is not " +
             "reachable from here at all - this is the only way to see this camera while making " +
             "the piece. Has no effect in a build.")]
    [SerializeField] private bool previewInEditorWindow = true;

    [SerializeField] private Vector2Int previewSize = new Vector2Int(1280, 720);

    private RenderTexture previewTexture;

    /// <summary>The live monitor image. Null outside Play mode. Read by RDMonitorWindow.</summary>
    public static RenderTexture EditorPreview { get; private set; }
#endif

    private Camera monitorCamera;
    private int frameCounter;
    private float angle;
    private bool available;

    private void Awake()
    {
        monitorCamera = GetComponent<Camera>();

        // Mono, and not an XR camera. Without this the second camera is drawn into the headset
        // as a third eye, which looks exactly as bad as it sounds.
        //
        // Camera.stereoTargetEye is the built-in renderer's control and URP warns the moment you
        // touch it, so under a scriptable pipeline the same thing is said through the camera's
        // URP data instead. Both paths are kept: the warning was noise, but the intent behind it
        // is not optional.
        if (GraphicsSettings.currentRenderPipeline == null)
        {
            monitorCamera.stereoTargetEye = StereoTargetEyeMask.None;
        }
        else
        {
            UniversalAdditionalCameraData data = monitorCamera.GetUniversalAdditionalCameraData();
            if (data != null) data.allowXRRendering = false;
        }

        monitorCamera.targetDisplay = targetDisplay;

        // Off until Update turns it on for the frames it wants.
        //
        // Throttling is done by toggling `enabled`, NOT by calling Camera.Render(). That call is
        // the built-in pipeline's API: URP renders its cameras inside its own render-graph pass,
        // so invoking Render() from Update opens a second pass in the middle of the first and
        // the frame comes apart - "NextSubPass: Not inside a Renderpass", and an attachment
        // mismatch when the blit to the back buffer finally runs. Toggling enabled asks URP to
        // skip the camera, which is the same saving with none of that.
        monitorCamera.enabled = false;
    }

    private void Start()
    {
#if UNITY_EDITOR
        // Display.displays.Length is ALWAYS 1 in the Editor, however many screens are plugged in
        // - extra displays only resolve in a built player. Taking the branch below here would
        // therefore switch the monitor view off every single time the piece is run from the
        // Editor, which is when it most needs looking at, and the warning would blame the cable.
        //
        // So render into a texture instead, and let RDMonitorWindow draw it: RD > Monitor Window.
        available = true;

        if (previewInEditorWindow)
        {
            previewTexture = new RenderTexture(previewSize.x, previewSize.y, 24)
            {
                name = "RD Monitor Preview"
            };

            previewTexture.Create();

            // targetTexture rather than a manual Render() call: a camera with a target texture
            // is rendered by URP inside its own loop, at its own time, which is the whole point
            // of the throttle below. Calling Render() by hand is what tears the frame apart.
            monitorCamera.targetTexture = previewTexture;
            EditorPreview = previewTexture;
        }

        return;
#else
        if (takeOverMainWindow)
        {
            // The headset mirror is what normally owns the player window. Switching it off frees
            // display 0 for a camera of our own - this one, which already has allowXRRendering
            // false and so is not drawn into the headset.
            UnityEngine.XR.XRSettings.showDeviceView = false;

            targetDisplay = 0;
            monitorCamera.targetDisplay = 0;

            // It owns the window now, so it has to draw every frame. Skipping two in three was
            // a saving worth making for a secondary screen; on the main one it leaves the
            // window holding a stale or empty back buffer.
            renderEveryNthFrame = 1;

            available = true;
            return;
        }

        if (targetDisplay > 0 && Display.displays.Length <= targetDisplay)
        {
            // Rendering to a display that is not there is the other half of the attachment
            // mismatch: there is no back buffer the right size to blit into. Better to sit out
            // than to spray errors for the length of the show.
            Debug.LogWarning("[RDMonitorView] Display " + targetDisplay + " is not connected, so " +
                             "the monitor view is off. Plug the second screen in before launching, " +
                             "or set targetDisplay to 0 to put it on the main window for testing.");

            available = false;
            monitorCamera.enabled = false;
            return;
        }

        available = true;

        if (activateDisplay && targetDisplay > 0)
            Display.displays[targetDisplay].Activate();
#endif
    }

    private void Update()
    {
#if UNITY_EDITOR
        // A lost surface comes back empty rather than throwing, so the window would show "no
        // preview" for the rest of the session. Recreating costs one check a frame.
        if (previewTexture != null && !previewTexture.IsCreated())
        {
            previewTexture.Create();
            monitorCamera.targetTexture = previewTexture;
            EditorPreview = previewTexture;
        }
#endif

        if (ball != null && secondsPerRevolution > 0.01f)
        {
            angle += 360f / secondsPerRevolution * Time.deltaTime;
            if (angle > 360f) angle -= 360f;

            Vector3 axis = spinAxis.sqrMagnitude > 0.0001f ? spinAxis.normalized : Vector3.up;
            ball.localRotation = Quaternion.AngleAxis(angle, axis);
        }

        if (!available) return;

        // One frame in N. URP picks up enabled cameras in its own loop, so this is the whole
        // throttle - no manual render call, nothing that can interrupt the pass already running.
        frameCounter++;

        if (frameCounter >= Mathf.Max(1, renderEveryNthFrame))
            frameCounter = 0;

        monitorCamera.enabled = frameCounter == 0;
    }

#if UNITY_EDITOR
    private void OnDestroy()
    {
        if (previewTexture == null) return;

        // Released by hand: a RenderTexture created in code is not garbage collected while the
        // camera still points at it, and leaving one per play session behind is how an Editor
        // ends up out of video memory by the afternoon.
        if (monitorCamera != null)
            monitorCamera.targetTexture = null;

        EditorPreview = null;

        previewTexture.Release();
        DestroyImmediate(previewTexture);
        previewTexture = null;
    }
#endif
}
