using UnityEngine;

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

    [Header("Display")]
    [Tooltip("Which physical display. 1 is the second monitor; the headset takes display 0.")]
    [Range(0, 7)] [SerializeField] private int targetDisplay = 1;

    [Tooltip("Activate the second display on start. Unity leaves extra displays off until asked.")]
    [SerializeField] private bool activateDisplay = true;

    [Header("Cost")]
    [Tooltip("Render every Nth frame. An observer view does not need 90 Hz, and the headset's " +
             "frame budget is the one that cannot be compromised. 3 gives about 30 fps at 90.")]
    [Range(1, 6)] [SerializeField] private int renderEveryNthFrame = 3;

    private Camera monitorCamera;
    private int frameCounter;
    private float angle;
    private bool available;

    private void Awake()
    {
        monitorCamera = GetComponent<Camera>();

        // Mono, and not an XR camera. Without this the second camera is drawn into the headset
        // as a third eye, which looks exactly as bad as it sounds.
        monitorCamera.stereoTargetEye = StereoTargetEyeMask.None;
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
    }

    private void Update()
    {
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
}
