using UnityEngine;

/// <summary>
/// Two sliders for the two things that actually get tuned by eye, in front of the dozen that do
/// not.
///
/// RDRecession has to expose every clock it runs, because each one was a separate bug at some
/// point and each one needs to stay reachable. But almost all of that is settled now, and what is
/// left to judge in the room is only: how hard does it pump, and how long does it take to go. This
/// sits in front of those two and leaves the rest alone.
///
/// It writes on Awake and whenever a value changes in the inspector - including during play, which
/// is when this is actually useful. It deliberately does NOT write every frame: a tuner that
/// overwrites continuously makes the fields it drives impossible to touch directly, and the point
/// of a tuner is to be a shortcut, not a lock.
/// </summary>
[RequireComponent(typeof(RDRecession))]
public class RDRecessionTuner : MonoBehaviour
{
    [SerializeField] private RDRecession recession;

    [Header("Idle swing - the big one")]
    [Tooltip("How far the empty room swings back towards its active look each cycle. This is " +
             "what pressing H on and off looks like, and it is the move that carries the idle " +
             "state - the beat pump below is detail on top of it.\n\n" +
             "0.4 is a slow tide. 0.65 reads from across the room. Above 0.8 the empty room " +
             "starts looking like an occupied one, and the monitor stops telling anyone whether " +
             "somebody is inside.")]
    [Range(0f, 1f)] [SerializeField] private float idleSwing = 0.65f;

    [Tooltip("Seconds per swing. Hand-toggling sits around three to five; longer than about " +
             "twelve stops reading as one gesture and starts reading as the picture drifting.")]
    [Range(2f, 30f)] [SerializeField] private float cycleSeconds = 9f;

    [Header("Blob strength - the beat on top")]
    [Tooltip("How hard the surface pumps on each beat once the room is empty.\n\n" +
             "This moves the ink thresholds, which slides the edge of every blob outward and back " +
             "- so the blobs change SIZE. Pumping brightness instead barely shows, because the " +
             "value inside a blob is already clamped at 1 and nothing the gain does can move it.\n\n" +
             "0.05 is a flicker you have to look for. 0.12 reads across the room. 0.20 thumps.")]
    [Range(0f, 1000f)] [SerializeField] private float blobStrength = 0.12f;

    [Tooltip("How much of the pump also goes to the value gain, relative to the threshold. The " +
             "gain only lifts the blob edges, so this is a garnish - it stops the swell looking " +
             "completely flat without doing the work.")]
    [Range(0f, 3f)] [SerializeField] private float gainShare = 1.25f;

    [Header("Shrink back")]
    [Tooltip("Seconds from the headset coming off to the picture being fully still. The visitor " +
             "watches this happen on the monitor, so it is long on purpose - under about 60 it " +
             "stops reading as subsiding and starts reading as switching off.")]
    [Range(20f, 600f)] [SerializeField] private float shrinkBackSeconds = 150f;

    [Tooltip("How much of the shrink is the dent genuinely contracting rather than the ink " +
             "fading over it. 0.47 is the ratio the two were tuned at.\n\n" +
             "Raise it and the shape holds longer and then goes abruptly; lower it and the dent " +
             "is gone well before the fade finishes, which looks like two separate events.")]
    [Range(0.15f, 1f)] [SerializeField] private float contractionShare = 0.47f;

    private void Awake()
    {
        if (recession == null) recession = GetComponent<RDRecession>();
        Push();
    }

    private void OnValidate()
    {
        if (recession == null) recession = GetComponent<RDRecession>();
        Push();
    }

    /// <summary>Apply the current settings. Public so an operator UI can call it.</summary>
    public void Push()
    {
        if (recession == null) return;

        recession.IdleSwing = idleSwing;
        recession.CycleSeconds = cycleSeconds;

        recession.PulseThreshold = blobStrength;
        recession.PulseAmount = blobStrength * gainShare;

        recession.RecessionSeconds = shrinkBackSeconds;
        recession.RestingDecayTau = shrinkBackSeconds * contractionShare;
    }
}
