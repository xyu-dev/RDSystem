using UnityEditor;
using UnityEngine;

/// <summary>
/// Adds the ambient drift to the open scene, building the parent rig it needs.
///
/// Menu:
///   RD / Add Ambient Drift      - inserts a rig above the main camera and attaches the drift
///   RD / Remove Ambient Drift   - takes it off again, leaving the rig
/// </summary>
public static class RDAmbientDriftSetup
{
    private const string RigName = "Camera Rig";

    [MenuItem("RD/Add Ambient Drift", priority = 50)]
    public static void AddAmbientDrift()
    {
        Camera camera = Camera.main;

        if (camera == null)
        {
            camera = Object.FindFirstObjectByType<Camera>();

            if (camera == null)
            {
                Debug.LogError("[RDAmbientDriftSetup] No camera in the open scene.");
                return;
            }
        }

        Transform parent = camera.transform.parent;

        // The drift has to sit on a PARENT of the camera. Mouse-look writes the camera's local
        // rotation and the HMD writes its local pose, so a drift on the same transform would be
        // overwritten every frame by whichever component ran last.
        if (parent == null || parent.GetComponent<RDAmbientDrift>() == null && parent.name != RigName)
        {
            GameObject rig = new GameObject(RigName);
            Undo.RegisterCreatedObjectUndo(rig, "Add ambient drift");

            rig.transform.SetParent(parent, false);
            rig.transform.localPosition = camera.transform.localPosition;
            rig.transform.localRotation = Quaternion.identity;

            Undo.SetTransformParent(camera.transform, rig.transform, "Add ambient drift");

            camera.transform.localPosition = Vector3.zero;

            parent = rig.transform;

            Debug.Log("[RDAmbientDriftSetup] Created '" + RigName + "' above " + camera.name +
                      " and moved the camera's offset onto it.");
        }

        RDAmbientDrift drift = parent.GetComponent<RDAmbientDrift>();

        if (drift == null)
        {
            drift = Undo.AddComponent<RDAmbientDrift>(parent.gameObject);

            Debug.Log("[RDAmbientDriftSetup] Added RDAmbientDrift to '" + parent.name +
                      "'. Defaults are 0.25 m of translation and 1.2 degrees of pitch and roll, " +
                      "with YAW OFF - yaw is the axis most likely to make a visitor ill. " +
                      "Test on several people before the show, not only on yourself.");
        }
        else
        {
            Debug.Log("[RDAmbientDriftSetup] '" + parent.name + "' already has RDAmbientDrift.");
        }

        Selection.activeGameObject = parent.gameObject;
    }

    [MenuItem("RD/Remove Ambient Drift", priority = 51)]
    public static void RemoveAmbientDrift()
    {
        RDAmbientDrift[] found = Object.FindObjectsByType<RDAmbientDrift>(FindObjectsSortMode.None);

        if (found.Length == 0)
        {
            Debug.Log("[RDAmbientDriftSetup] No RDAmbientDrift in the open scene.");
            return;
        }

        foreach (RDAmbientDrift drift in found)
        {
            // Put the transform back where it started before removing the thing that moved it,
            // or the rig keeps whatever offset the drift happened to be at.
            drift.transform.localPosition = Vector3.zero;
            drift.transform.localRotation = Quaternion.identity;

            Undo.DestroyObjectImmediate(drift);
        }

        Debug.Log("[RDAmbientDriftSetup] Removed the drift and reset the rig to centre. The '" +
                  RigName + "' object is left in place; it is harmless.");
    }
}
