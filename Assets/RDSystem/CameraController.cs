using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;

/// <summary>
/// Mouse-look for the desktop, and nothing at all once a headset is presenting.
/// </summary>
public class CameraController : MonoBehaviour
{
    [SerializeField] float sensitivity = 0.1f;
    [SerializeField] float verticalClamp = 80f;

    float pitch;
    float yaw;

    // Reused: the check runs every frame and GetSubsystems fills a list rather than returning one.
    static readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();

    /// <summary>
    /// True once a headset is actually presenting, not merely installed.
    ///
    /// Mouse-look and the TrackedPoseDriver both write this transform's rotation, and whichever
    /// runs last in the frame wins. In the headset that fight reads as the view being yanked
    /// back to wherever the pointer last was, every frame. So the pointer stands down while a
    /// display is running - the same bargain RDInputSourceRouter strikes between gaze and mouse,
    /// and for the same reason: two sources, one scene, never both at once.
    /// </summary>
    static bool HeadsetPresenting
    {
        get
        {
            SubsystemManager.GetSubsystems(displays);

            for (int i = 0; i < displays.Count; i++)
                if (displays[i] != null && displays[i].running)
                    return true;

            return false;
        }
    }

    void Update()
    {
        if (HeadsetPresenting) return;

        if (Mouse.current == null) return;

        Vector2 delta = Mouse.current.delta.ReadValue() * sensitivity;

        yaw += delta.x;
        pitch -= delta.y;
        pitch = Mathf.Clamp(pitch, -verticalClamp, verticalClamp);

        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }
}
