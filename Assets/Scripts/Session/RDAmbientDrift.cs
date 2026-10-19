using UnityEngine;

/// <summary>
/// A slow, continuous drift of the viewer's position and orientation, so the room never sits
/// completely still and the surface keeps yielding parallax without anything deforming.
///
/// Put this on a PARENT transform above the camera, never on the camera itself. Mouse-look writes
/// the camera's local rotation and the HMD writes its local pose, so a drift applied to the same
/// transform would be overwritten every frame by whichever ran last. On a parent it composes with
/// both.
///
/// -------------------------------------------------------------------------------------------
/// VR SICKNESS. Camera motion the viewer did not ask for is the single largest cause of
/// discomfort in VR, because the eyes report movement the inner ear does not. Everything here is
/// therefore deliberately slower and smaller than it was in the desktop test scene:
///
///   - Translation is far better tolerated than rotation. It is on by default; rotation is small.
///   - YAW is the worst of the three axes - it reads as the room turning, and it is the one most
///     likely to make someone take the headset off. It is OFF by default and should stay off
///     unless a long test says otherwise.
///   - Nothing is fast. Peak speed at the defaults is about 8 cm/s, which reads as floating
///     rather than as being moved.
///   - The drift ramps in over warmupSeconds, so putting the headset on is not a lurch.
///
/// Test this on several people before the show, not only on yourself: tolerance varies enormously
/// and the person who built it is always the most adapted to it.
/// -------------------------------------------------------------------------------------------
/// </summary>
public class RDAmbientDrift : MonoBehaviour
{
    [Header("Translation")]
    [Tooltip("Metres of drift from the centre. The desktop test scene used 0.45, which is too " +
             "much for a headset; 0.25 keeps the parallax and loses most of the risk.")]
    [Range(0f, 1f)] [SerializeField] private float translationAmplitude = 0.25f;

    [Tooltip("Seconds per cycle on each axis. Deliberately not whole-number ratios, so the path " +
             "never retraces itself and the parallax keeps revealing the surface from new angles.")]
    [SerializeField] private Vector3 translationPeriods = new Vector3(19f, 29f, 23f);

    [Header("Rotation")]
    [Tooltip("Degrees of pitch and roll. Small: this reads as the room breathing, and anything " +
             "larger reads as being tilted.")]
    [Range(0f, 8f)] [SerializeField] private float rotationAmplitude = 1.2f;

    [SerializeField] private Vector2 rotationPeriods = new Vector2(37f, 43f);

    [Tooltip("Yaw drift. OFF by default and it should stay off: it reads as the whole room turning, " +
             "it costs the viewer their bearings, and it is the axis most likely to make someone " +
             "ill. Turn it on only after testing it on people who are not you.")]
    [Range(0f, 8f)] [SerializeField] private float yawAmplitude = 0f;

    [SerializeField] private float yawPeriod = 61f;

    [Header("Behaviour")]
    [Tooltip("Seconds to ramp the drift in from nothing, so putting the headset on is not a lurch.")]
    [SerializeField] private float warmupSeconds = 8f;

    [Tooltip("Turn the whole thing off without removing the component. The operator switch.")]
    [SerializeField] private bool driftEnabled = true;

    [Header("Read-only")]
    [SerializeField] private float debugSpeed;

    /// <summary>
    /// The values RDTestHeadSway used in DeformTest. Stronger and faster than the defaults here:
    /// 0.45 m at periods of 9 to 14 seconds, and translation ONLY - the test component had no
    /// rotation at all, so any rotation would be an addition rather than a reproduction.
    ///
    /// Right-click the component header to apply it. Peak speed goes from about 8 cm/s to about
    /// 31 cm/s, so re-read the sickness note above before taking this into the show.
    /// </summary>
    [ContextMenu("Apply Deform Test feel")]
    private void ApplyTestSceneFeel()
    {
        translationAmplitude = 0.45f;
        translationPeriods = new Vector3(9.09f, 14.29f, 12.05f);
        rotationAmplitude = 0f;
        yawAmplitude = 0f;

        Debug.Log("[RDAmbientDrift] Applied the DeformTest feel: 0.45 m, periods 9.1 / 14.3 / " +
                  "12.1 s, no rotation. Peak speed about 0.31 m/s, roughly 4x the default.");
    }

    /// <summary>
    /// For when the gentle setting is still too much. Peak speed about 3 cm/s - slow enough that
    /// it is below the threshold of feeling like motion, while still giving the parallax that
    /// keeps the surface from reading as a flat painted backdrop.
    /// </summary>
    [ContextMenu("Apply minimal feel")]
    private void ApplyMinimalFeel()
    {
        translationAmplitude = 0.15f;
        translationPeriods = new Vector3(31f, 47f, 37f);
        rotationAmplitude = 0.4f;
        yawAmplitude = 0f;

        Debug.Log("[RDAmbientDrift] Applied the minimal feel: 0.15 m, periods 31 / 47 / 37 s, " +
                  "0.4 degrees of pitch and roll. Peak speed about 0.03 m/s.");
    }

    [ContextMenu("Apply gentle (headset-safe) feel")]
    private void ApplyGentleFeel()
    {
        translationAmplitude = 0.25f;
        translationPeriods = new Vector3(19f, 29f, 23f);
        rotationAmplitude = 1.2f;
        yawAmplitude = 0f;

        Debug.Log("[RDAmbientDrift] Applied the gentle feel: 0.25 m, periods 19 / 29 / 23 s, " +
                  "1.2 degrees of pitch and roll. Peak speed about 0.08 m/s.");
    }

    private Vector3 basePosition;
    private Quaternion baseRotation;

    private float clock;
    private Vector3 lastPosition;

    /// <summary>Turn the drift on or off at runtime. Resets to centre when switched off.</summary>
    public bool DriftEnabled
    {
        get => driftEnabled;
        set
        {
            driftEnabled = value;

            if (!value)
            {
                transform.localPosition = basePosition;
                transform.localRotation = baseRotation;
            }
        }
    }

    private void Awake()
    {
        basePosition = transform.localPosition;
        baseRotation = transform.localRotation;
        lastPosition = basePosition;
    }

    private void Update()
    {
        if (!driftEnabled) return;

        clock += Time.deltaTime;

        float warmup = warmupSeconds > 0.01f ? Mathf.Clamp01(clock / warmupSeconds) : 1f;

        // Smoothstep the ramp, so the drift eases in rather than starting at a constant rate.
        warmup = warmup * warmup * (3f - 2f * warmup);

        Vector3 offset = new Vector3(
            Wave(translationPeriods.x, 0.00f),
            Wave(translationPeriods.y, 0.37f) * 0.5f,   // less vertical: up and down is the least
            Wave(translationPeriods.z, 0.71f))          // comfortable translation of the three
            * translationAmplitude * warmup;

        transform.localPosition = basePosition + offset;

        float pitch = Wave(rotationPeriods.x, 0.13f) * rotationAmplitude * warmup;
        float roll = Wave(rotationPeriods.y, 0.59f) * rotationAmplitude * warmup;
        float yaw = yawAmplitude > 0.001f ? Wave(yawPeriod, 0.29f) * yawAmplitude * warmup : 0f;

        transform.localRotation = baseRotation * Quaternion.Euler(pitch, yaw, roll);

        debugSpeed = Time.deltaTime > 0.0001f
            ? (transform.localPosition - lastPosition).magnitude / Time.deltaTime
            : 0f;

        lastPosition = transform.localPosition;
    }

    /// <summary>
    /// One sine, with the phase offset so no two axes cross zero together. Periods rather than
    /// frequencies, because seconds are what you can judge standing in the room with a stopwatch.
    /// </summary>
    private float Wave(float periodSeconds, float phase)
    {
        float period = Mathf.Max(0.5f, periodSeconds);
        return Mathf.Sin((clock / period + phase) * Mathf.PI * 2f);
    }
}
