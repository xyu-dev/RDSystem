using UnityEngine;

public class TestGazeDetected : MonoBehaviour
{
    public GameObject demoObject;
    Rigidbody rb;

    void Start()
    {
        rb = GetComponentInChildren<Rigidbody>();

    }

    void Update()
    {

    }

    // Rotates object hit with gaze tracking raycast
    public void RayHit()
    {
        Debug.Log("Detected");
        //rb.AddTorque(Vector3.up * forceAmount * Time.deltaTime, ForceMode.Force);
    }
}

