using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Moves the camera a little, so the deformation can be judged without a headset.
///
/// This exists because of a trap. Parallax is the honest depth cue for a dent in a surface, and
/// CameraController is mouse-LOOK only - it rotates and never translates. Evaluated on the
/// desktop with no head movement at all, a dent two and a half metres deep is almost invisible,
/// and the obvious conclusion is that the mechanic does not work. It does; the desktop just
/// cannot show it.
///
/// In the headset this component is not needed and should be OFF: real head movement supplies the
/// parallax, and an automatic sway on top of it is a reliable way to make someone ill.
/// </summary>
public class RDTestHeadSway : MonoBehaviour
{
    [Header("Manual")]
    [Tooltip("WASD to slide, Q and E for down and up. The strongest way to read the depth: move " +
             "deliberately and watch the dent shift against the surface around it.")]
    [SerializeField] private bool allowManual = true;

    [SerializeField] private float manualSpeed = 1.2f;

    [Header("Automatic")]
    [Tooltip("A slow drift, so the depth is visible without touching anything. Leave this ON for " +
             "desktop evaluation and OFF in the headset.")]
    [SerializeField] private bool autoSway = true;

    [Tooltip("Metres of drift from centre.")]
    [Range(0f, 1.5f)] [SerializeField] private float swayAmplitude = 0.45f;

    [Tooltip("Cycles per second. Deliberately not a round ratio between the axes, so the path " +
             "does not retrace itself and the parallax keeps revealing new depth.")]
    [SerializeField] private Vector3 swayFrequency = new Vector3(0.11f, 0.07f, 0.083f);

    [Header("Bounds")]
    [Tooltip("Metres from the origin the camera may reach. Keeps the viewer near the centre, " +
             "where the sphere is meant to be seen from.")]
    [SerializeField] private float maxOffset = 2.5f;

    [SerializeField] private Key toggleKey = Key.F4;

    private Vector3 manualOffset;
    private float phase;
    private bool enabledNow = true;

    private void Update()
    {
        Keyboard keyboard = Keyboard.current;

        if (keyboard != null && keyboard[toggleKey].wasPressedThisFrame)
        {
            enabledNow = !enabledNow;

            if (!enabledNow)
            {
                manualOffset = Vector3.zero;
                transform.localPosition = Vector3.zero;
            }
        }

        if (!enabledNow) return;

        if (allowManual && keyboard != null)
        {
            Vector3 input = Vector3.zero;

            if (keyboard[Key.A].isPressed) input.x -= 1f;
            if (keyboard[Key.D].isPressed) input.x += 1f;
            if (keyboard[Key.Q].isPressed) input.y -= 1f;
            if (keyboard[Key.E].isPressed) input.y += 1f;
            if (keyboard[Key.S].isPressed) input.z -= 1f;
            if (keyboard[Key.W].isPressed) input.z += 1f;

            // In the camera's own frame, so sliding left is always left of where you are looking.
            if (input.sqrMagnitude > 0.0001f)
            {
                Vector3 world =
                    transform.right * input.x +
                    Vector3.up * input.y +
                    transform.forward * input.z;

                manualOffset += world.normalized * manualSpeed * Time.deltaTime;
                manualOffset = Vector3.ClampMagnitude(manualOffset, maxOffset);
            }
        }

        Vector3 auto = Vector3.zero;

        if (autoSway)
        {
            phase += Time.deltaTime;

            auto = new Vector3(
                Mathf.Sin(phase * swayFrequency.x * Mathf.PI * 2f),
                Mathf.Sin(phase * swayFrequency.y * Mathf.PI * 2f) * 0.5f,
                Mathf.Sin(phase * swayFrequency.z * Mathf.PI * 2f)) * swayAmplitude;
        }

        transform.localPosition = Vector3.ClampMagnitude(manualOffset + auto, maxOffset);
    }
}
