using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.SpatialTracking;

/// <summary>
/// The headset half of the camera.
///
/// RDExhibitionV2Setup builds "Camera Rig" with "Main Camera" under it precisely so the ambient
/// drift and the head pose can compose - the drift writes the rig, the headset writes the camera.
/// Only nothing was ever writing the camera: the scene had a Camera and a mouse-look script and
/// no tracked pose at all, so in the headset the view stayed welded to wherever the pointer left
/// it while the visitor turned their head against a motionless room.
///
/// The driver used here is the one from com.unity.xr.legacyinputhelpers, configured exactly as
/// the Varjo sample's "XR Rig" prefab configures it - GenericXRDevice / Center / rotation and
/// position / update and before render. That prefab is what EyeTrackingTest uses, and gaze
/// demonstrably works in that scene on this machine. Matching it removes the variable rather
/// than arguing about it: the Input System's TrackedPoseDriver resolves its pose through
/// &lt;XRHMD&gt; action bindings, and whether those bind is one more thing that can quietly fail
/// in front of visitors.
///
/// It is deliberately a pose driver on the existing camera rather than an XROrigin rebuild: the
/// visitor stands at the centre of a 10 m sphere, so the head belongs at the rig's own origin.
/// An XROrigin would insert a floor offset and start the visitor a metre and a half above the
/// centre of the piece.
///
/// Menu:
///   RD / Upgrade Camera To XR Rig   - patches the scene that is currently open
/// </summary>
public static class RDXRRigSetup
{
    [MenuItem("RD/Upgrade Camera To XR Rig", priority = 20)]
    private static void UpgradeOpenScene()
    {
        Scene scene = SceneManager.GetActiveScene();

        GameObject cameraGo = FindMainCamera(scene);

        if (cameraGo == null)
        {
            Debug.LogError("[RDXRRigSetup] No camera tagged MainCamera in the open scene. " +
                           "Open Exhibition_v2 and try again.");
            return;
        }

        bool alreadyTracked = cameraGo.GetComponent<TrackedPoseDriver>() != null;

        AttachHeadTracking(cameraGo);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Debug.Log(alreadyTracked
            ? "[RDXRRigSetup] " + cameraGo.name + " already had head tracking; settings refreshed."
            : "[RDXRRigSetup] Head tracking added to " + cameraGo.name + " in " + scene.name + ".");
    }

    /// <summary>
    /// Gives the camera the headset's pose. Idempotent: safe to run on a scene that already has it.
    /// </summary>
    public static TrackedPoseDriver AttachHeadTracking(GameObject cameraGo)
    {
        // An earlier version of this script fitted the Input System's driver. Two drivers writing
        // one transform is the same fight as mouse-look versus the headset, so the other one goes
        // before this one arrives.
        var inputSystemDriver = cameraGo.GetComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();

        if (inputSystemDriver != null)
        {
            Debug.Log("[RDXRRigSetup] Removing the Input System TrackedPoseDriver in favour of " +
                      "the one the working EyeTrackingTest rig uses.");
            Object.DestroyImmediate(inputSystemDriver);
        }

        TrackedPoseDriver driver = cameraGo.GetComponent<TrackedPoseDriver>();

        if (driver == null)
            driver = cameraGo.AddComponent<TrackedPoseDriver>();

        // Values copied from Assets/prefabs/XR Rig.prefab, the rig EyeTrackingTest runs on.
        driver.SetPoseSource(TrackedPoseDriver.DeviceType.GenericXRDevice,
                             TrackedPoseDriver.TrackedPose.Center);

        driver.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;

        // BeforeRender as well as Update: a head pose sampled once per frame and then held
        // through rendering is the latency that reads as swimming, and in a room this size there
        // is nothing else for the eye to anchor on.
        driver.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;

        driver.UseRelativeTransform = false;

        return driver;
    }

    private static GameObject FindMainCamera(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Camera camera in root.GetComponentsInChildren<Camera>(true))
                if (camera.CompareTag("MainCamera"))
                    return camera.gameObject;
        }

        return null;
    }
}
