using UnityEngine;
using UnityEngine.InputSystem;
using Varjo.XR.Input;

/// <summary>
/// Simulates the headset being picked up and put down, so the whole session lifecycle can be
/// exercised without the Aero: a visitor arrives, the room comes alive, they leave, the channel
/// goes dead, the session ends.
///
/// It is a GATE, not a source. It sits between the router and everything downstream, wrapping
/// whatever the router chose rather than replacing it:
///
///     VarjoGazeInput / MouseInput  ->  RDInputSourceRouter  ->  RDHeadsetSimulator  ->  controller
///
/// That matters because the router owns the `enabled` flag on its two sources and rewrites it
/// every frame - anything that tried to simulate "not worn" by disabling MouseInput would be
/// overruled on the next Update. Gating downstream of the router leaves the real input path
/// completely untouched, which is also why the same scene can run the show: set `worn` true and
/// this forwards everything verbatim.
///
/// Press H to put the headset on and take it off. The piece sees exactly what it would see if
/// someone had lifted it off the hook.
/// </summary>
public class RDHeadsetSimulator : MonoBehaviour,
    IInteractionSignalSource, IInteractionUVSource, IInteractionHealthSource
{
    [Header("Source")]
    [Tooltip("Normally the RDInputSourceRouter on this object. Everything is forwarded from here " +
             "while the headset is worn.")]
    [SerializeField] private MonoBehaviour sourceBehaviour;

    [Header("Simulation")]
    [Tooltip("Is the headset on a head right now. Toggled by the key below, and settable from " +
             "the operator overlay or a real presence sensor later.")]
    [SerializeField] private bool worn = false;

    [SerializeField] private Key toggleKey = Key.H;

    [Tooltip("Leave this off for the real show: the simulation is then a no-op and the gate " +
             "forwards the router unchanged.")]
    [SerializeField] private bool simulate = true;

    [Tooltip("With simulation off, read the headset's own proximity sensor instead of assuming " +
             "it is always worn. This is what makes taking it off a real event rather than " +
             "something the piece only infers 25 seconds later from the absence of gaze.")]
    [SerializeField] private bool useHeadsetSensor = true;

    [Header("Read-only")]
    [SerializeField] private bool debugWorn;
    [SerializeField] private float debugNoGaze;

    private IInteractionSignalSource signal;
    private IInteractionUVSource uv;
    private IInteractionHealthSource health;

    // Kept here rather than read through, because while the headset is off the wrapped source is
    // still happily reporting a mouse hover and its own counters are meaningless to us.
    private float offGazeDuration;
    private Vector2 lastUV = new Vector2(0.5f, 0.5f);

    /// <summary>True while the headset is being worn. Set this from a presence sensor when there
    /// is one; until then the H key is the sensor.</summary>
    public bool Worn
    {
        get => simulate ? worn : (!useHeadsetSensor || sensorWorn);
        set => worn = value;
    }

    // Cached once a frame. Worn is read several times per frame by everything downstream, and a
    // device query per read is wasteful; it also keeps the value stable within a frame, so two
    // readers cannot disagree about whether the headset is on.
    private bool sensorWorn = true;

    /// <summary>
    /// The headset's own answer to whether someone is wearing it.
    ///
    /// Varjo publishes it as a button on its Input System layout, and the XR subsystem may also
    /// expose it as a feature usage; both are tried because which one is populated depends on
    /// the runtime version. When neither answers, the honest default is TRUE - a missing sensor
    /// must not read as an empty gallery and gate the whole piece off.
    /// </summary>
    private static bool ReadUserPresence()
    {
        VarjoHMD hmd = InputSystem.GetDevice<VarjoHMD>();

        if (hmd != null && hmd.userPresence != null)
            return hmd.userPresence.isPressed;

        UnityEngine.XR.InputDevice head =
            UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.Head);

        if (head.isValid &&
            head.TryGetFeatureValue(UnityEngine.XR.CommonUsages.userPresence, out bool present))
            return present;

        return true;
    }

    private void Awake()
    {
        if (sourceBehaviour == null)
            sourceBehaviour = GetComponent<RDInputSourceRouter>();

        signal = sourceBehaviour as IInteractionSignalSource;
        uv = sourceBehaviour as IInteractionUVSource;
        health = sourceBehaviour as IInteractionHealthSource;

        if (signal == null)
            Debug.LogError("[RDHeadsetSimulator] sourceBehaviour must implement " +
                           "IInteractionSignalSource - normally the RDInputSourceRouter.");

        // Launching with the headset on the hook is the same situation as someone having just
        // put it down, so the room has to be empty from the first frame. Starting this at zero
        // meant NoGazeDuration read as 0 for the first two and a half seconds, everything
        // downstream concluded a visitor was present, and the piece opened in the wrong state.
        if (!Worn) offGazeDuration = 600f;
    }

    private void Update()
    {
        if (!simulate && useHeadsetSensor)
            sensorWorn = ReadUserPresence();

        if (simulate)
        {
            Keyboard keyboard = Keyboard.current;

            if (keyboard != null && keyboard[toggleKey].wasPressedThisFrame)
            {
                worn = !worn;

                // Reset on the way off, so the next visitor does not inherit the last one's timer.
                if (!worn) offGazeDuration = 0f;

                Debug.Log("[RDHeadsetSimulator] Headset " + (worn ? "PUT ON" : "TAKEN OFF") + ".");
            }
        }

        if (!Worn)
            offGazeDuration += Time.deltaTime;

        if (Worn && uv != null)
            lastUV = uv.CurrentUV;

        debugWorn = Worn;
        debugNoGaze = NoGazeDuration;
    }

    // ---------------------------------------------------------------- signal

    public bool IsGazing => Worn && signal != null && signal.IsGazing;

    public float GazeDuration => Worn && signal != null ? signal.GazeDuration : 0f;

    // The one value that must NOT be forwarded while the headset is off: the wrapped source has
    // its own idea of how long it has been since a hover, and the dead channel, the presence
    // tracker and the session end all key off this.
    public float NoGazeDuration => Worn
        ? (signal != null ? signal.NoGazeDuration : 0f)
        : offGazeDuration;

    public int SwitchCount => signal != null ? signal.SwitchCount : 0;

    public float HeadMotion => Worn && signal != null ? signal.HeadMotion : 0f;

    public bool EyeJump => Worn && signal != null && signal.EyeJump;

    public Vector3 InteractionDirection =>
        signal != null ? signal.InteractionDirection : Vector3.up;

    // ---------------------------------------------------------------- uv

    // The last gaze direction is held rather than zeroed. Snapping the UV to (0,0) on removal
    // would throw the ring to a pole and leave a mark there on every single visit.
    public Vector2 CurrentUV => Worn && uv != null ? uv.CurrentUV : lastUV;

    // ---------------------------------------------------------------- health

    public bool IsTracking => Worn && (health == null || health.IsTracking);

    public string TrackingStatus
    {
        get
        {
            if (!Worn) return "headset on the hook";
            return health != null ? health.TrackingStatus : "";
        }
    }
}
