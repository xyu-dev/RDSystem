using UnityEngine;
using UnityEngine.InputSystem;

public class CameraController : MonoBehaviour
{
    [SerializeField] float sensitivity = 0.1f;
    [SerializeField] float verticalClamp = 80f;

    float pitch;
    float yaw;

    void Update()
    {
        if (Mouse.current == null) return;

        Vector2 delta = Mouse.current.delta.ReadValue() * sensitivity;

        yaw += delta.x;
        pitch -= delta.y;
        pitch = Mathf.Clamp(pitch, -verticalClamp, verticalClamp);

        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }
}