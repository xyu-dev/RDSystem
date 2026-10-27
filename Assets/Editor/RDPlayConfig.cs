using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Puts the scene into show configuration every time Play is pressed.
///
/// This exists because the same three things kept coming undone between sessions, and each one
/// cost a run to notice:
///
///   - RDStrategyCatalogue.asset loses its script binding, so it will not load as its own class.
///   - When it will not load, Unity serialises the director's reference to it as null, and the
///     scene is then permanently missing its strategies even after the asset is repaired. That
///     second step is the one that makes the problem look unfixable: repairing the asset alone
///     changes nothing, because the scene no longer points at it.
///   - RDHeadsetSimulator gets left simulating with the headset off, which silently gates the
///     whole piece - gaze works perfectly and nothing moves.
///
/// Running on ExitingEditMode rather than after load: the fixes are applied to the edit-mode
/// scene, so Play starts from a correct scene rather than from one being corrected underneath it.
///
/// Menu:
///   RD / Apply Show Config  - the same pass, on demand
/// </summary>
[InitializeOnLoad]
public static class RDPlayConfig
{
    private const string ConfigFolder = "Assets/Settings/RD";

    static RDPlayConfig()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode)
            Apply(false);
    }

    [MenuItem("RD/Apply Show Config", priority = 3)]
    private static void ApplyFromMenu() => Apply(true);

    /// <summary>
    /// Clears what the bandit has learned, so exploration starts again from startEpsilon.
    ///
    /// Values learned during the sessions that were misreporting saccades are not merely stale.
    /// Epsilon decays with impressions, so thousands of false ones leave the machine barely
    /// exploring at all - it stops trying alternatives exactly when its estimates are worst and
    /// sits on whatever won during the noise. That does not recover on its own in a useful time.
    /// </summary>
    /// <summary>
    /// The link the console cannot give you.
    ///
    /// Unity only makes a console path clickable when it points inside the project, and
    /// receipts are written to persistentDataPath on purpose - they are a record of the show,
    /// not project assets, and they must survive a reinstall. So the console prints the path
    /// to copy and this opens the folder.
    /// </summary>
    /// <summary>
    /// Back to #001. Run it when the exhibition opens, not during it: the numbers on the paper
    /// are the only record of how many receipts went out, and restarting them mid-run puts two
    /// sheets with the same number into the world.
    /// </summary>
    [MenuItem("RD/Reset Receipt Numbering", priority = 8)]
    private static void ResetReceiptNumbering()
    {
        int previous = PlayerPrefs.GetInt("RDReceiptSerial", 0);

        PlayerPrefs.SetInt("RDReceiptSerial", 0);
        PlayerPrefs.Save();

        Debug.Log("[RDPlayConfig] Receipt numbering reset. The last one issued was #" +
                  previous.ToString("000") + "; the next will be #001.");
    }

    [MenuItem("RD/Report Gaze State", priority = 7)]
    private static void ReportGazeState()
    {
        VarjoGazeInput gaze = Object.FindAnyObjectByType<VarjoGazeInput>(FindObjectsInactive.Include);

        if (gaze == null)
        {
            Debug.LogError("[RDPlayConfig] No VarjoGazeInput in the open scene.");
            return;
        }

        if (!Application.isPlaying)
        {
            Debug.LogError("[RDPlayConfig] Enter Play mode first - there is no Varjo session " +
                           "to ask in edit mode.");
            return;
        }

        Debug.Log("[RDPlayConfig] Gaze state: " + gaze.DescribeGazeState());
    }

    [MenuItem("RD/Open Receipts Folder", priority = 6)]
    private static void OpenReceiptsFolder()
    {
        string folder = System.IO.Path.Combine(Application.persistentDataPath, "Receipts");

        RDReceipt receipt = Object.FindAnyObjectByType<RDReceipt>(FindObjectsInactive.Include);

        if (receipt != null)
        {
            SerializedProperty custom = new SerializedObject(receipt).FindProperty("outputFolder");

            if (custom != null && !string.IsNullOrEmpty(custom.stringValue))
                folder = custom.stringValue;
        }

        System.IO.Directory.CreateDirectory(folder);

        Debug.Log("[RDPlayConfig] Receipts folder:" + System.Environment.NewLine + folder);

        EditorUtility.RevealInFinder(folder);
    }

    [MenuItem("RD/Print Test Receipt", priority = 5)]
    private static void PrintTestReceipt()
    {
        RDReceipt receipt = Object.FindAnyObjectByType<RDReceipt>(FindObjectsInactive.Include);

        if (receipt == null)
        {
            Debug.LogError("[RDPlayConfig] No RDReceipt in the open scene.");
            return;
        }

        if (!Application.isPlaying)
        {
            Debug.LogError("[RDPlayConfig] Enter Play mode first - the clip is taken from the " +
                           "live simulation, which is not running in edit mode.");
            return;
        }

        receipt.PrintNow();
    }

    [MenuItem("RD/Reset Strategy Memory", priority = 4)]
    private static void ResetStrategyMemory()
    {
        RDGreedyDirector director = Object.FindAnyObjectByType<RDGreedyDirector>();

        if (director != null && Application.isPlaying)
        {
            director.ResetMemory();
            return;
        }

        string file = "rd_strategy_memory.json";

        if (director != null)
        {
            SerializedProperty named = new SerializedObject(director).FindProperty("saveFileName");

            if (named != null && !string.IsNullOrEmpty(named.stringValue))
                file = named.stringValue;
        }

        string path = System.IO.Path.Combine(Application.persistentDataPath, file);

        if (System.IO.File.Exists(path))
        {
            System.IO.File.Delete(path);
            Debug.Log("[RDPlayConfig] Deleted " + path + " - the bandit starts from scratch.");
        }
        else
        {
            Debug.Log("[RDPlayConfig] No stored strategy memory at " + path + ".");
        }
    }

    /// <summary>Entry point for the build, which cannot rely on ExecuteMenuItem in batch mode.</summary>
    internal static void ApplyShowConfig() => Apply(true);

    private static void Apply(bool verbose)
    {
        int fixes = 0;

        RDStrategyCatalogue catalogue =
            RDConfigAssets.Ensure<RDStrategyCatalogue>(ConfigFolder, "RDStrategyCatalogue");

        fixes += FixDirector(catalogue);
        fixes += FixHeadsetGate();
        fixes += FixRouter();
        fixes += StripMissingScripts();
        fixes += FixBoundary();
        fixes += FixCalibrationMode();
        fixes += FixPrinter();

        if (fixes > 0)
        {
            Scene scene = SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log("[RDPlayConfig] Show config applied - " + fixes + " thing(s) put right.");
        }
        else if (verbose)
        {
            Debug.Log("[RDPlayConfig] Nothing to fix; the scene is already in show configuration.");
        }
    }

    /// <summary>
    /// Reassigns the catalogue when the scene has lost it.
    ///
    /// The null reference is a consequence, not the cause: Unity writes it the moment the asset
    /// stops loading. Repairing the asset without this leaves the director just as empty.
    /// </summary>
    private static int FixDirector(RDStrategyCatalogue catalogue)
    {
        if (catalogue == null)
        {
            Debug.LogError("[RDPlayConfig] No usable RDStrategyCatalogue; the director will do " +
                           "nothing. Check " + ConfigFolder + ".");
            return 0;
        }

        RDGreedyDirector director = Object.FindAnyObjectByType<RDGreedyDirector>();
        if (director == null) return 0;

        SerializedObject data = new SerializedObject(director);
        SerializedProperty field = data.FindProperty("catalogue");

        if (field == null || field.objectReferenceValue == catalogue) return 0;

        field.objectReferenceValue = catalogue;
        data.ApplyModifiedPropertiesWithoutUndo();

        Debug.Log("[RDPlayConfig] Reassigned the strategy catalogue to " + director.name + ".");
        return 1;
    }

    /// <summary>
    /// Takes the simulation off the gate.
    ///
    /// With simulate on and worn false, RDHeadsetSimulator reports "not worn" to everything
    /// downstream - so the surface does not move, the reticle does not draw, and gaze looks
    /// broken while being perfectly healthy. It is a development default that has no business
    /// surviving into a run.
    /// </summary>
    private static int FixHeadsetGate()
    {
        RDHeadsetSimulator headset = Object.FindAnyObjectByType<RDHeadsetSimulator>();
        if (headset == null) return 0;

        SerializedObject data = new SerializedObject(headset);
        SerializedProperty simulate = data.FindProperty("simulate");

        if (simulate == null || !simulate.boolValue) return 0;

        simulate.boolValue = false;
        data.ApplyModifiedPropertiesWithoutUndo();

        Debug.Log("[RDPlayConfig] Headset simulation switched off; the gate now forwards the " +
                  "router unchanged. Press H only if you want it back for testing.");

        return 1;
    }

    /// <summary>
    /// Points the receipt at the printer script, with an absolute path built from this project.
    ///
    /// Left to the Inspector it stayed empty, so every receipt was written to disk and none was
    /// ever sent - the chain looked broken when only its last link was missing. The path has to
    /// be absolute because the process Unity spawns does not inherit the project as its working
    /// directory, and -NoProfile matters because a PowerShell profile that prompts or writes to
    /// the console would hang that process with nobody watching.
    /// </summary>
    private static int FixPrinter()
    {
        RDReceipt receipt = Object.FindAnyObjectByType<RDReceipt>(FindObjectsInactive.Include);
        if (receipt == null) return 0;

        string project = System.IO.Directory.GetParent(Application.dataPath).FullName;
        string script = System.IO.Path.Combine(project, "Tools", "print-receipt.ps1");

        if (!System.IO.File.Exists(script))
        {
            Debug.LogWarning("[RDPlayConfig] " + script + " is missing, so receipts will be " +
                             "written to disk but not printed.");
            return 0;
        }

        string arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script +
                           "\" -File \"{file}\"";

        SerializedObject data = new SerializedObject(receipt);
        SerializedProperty command = data.FindProperty("printCommand");
        SerializedProperty args = data.FindProperty("printArguments");

        if (command == null || args == null) return 0;
        if (command.stringValue == "powershell.exe" && args.stringValue == arguments) return 0;

        command.stringValue = "powershell.exe";
        args.stringValue = arguments;
        data.ApplyModifiedPropertiesWithoutUndo();

        Debug.Log("[RDPlayConfig] Receipt printing wired to " + script + ".");
        return 1;
    }

    /// <summary>
    /// One dot rather than the two-dot routine.
    ///
    /// Fast is the better calibration and the wrong one for a queue: it asks a stranger to hold
    /// still through a longer sequence, and every visitor who gives up halfway leaves the
    /// headset uncalibrated for the next one. OneDot is coarser and finishes.
    /// </summary>
    private static int FixCalibrationMode()
    {
        VarjoGazeInput gaze = Object.FindAnyObjectByType<VarjoGazeInput>(FindObjectsInactive.Include);
        if (gaze == null) return 0;

        SerializedObject data = new SerializedObject(gaze);
        // The renamed field. The old "calibrationMode" is still in the scene YAML as Fast and is
        // now ignored by the component; Unity drops it on the next save.
        SerializedProperty mode = data.FindProperty("gazeCalibrationMode");

        if (mode == null || mode.enumValueIndex == 1) return 0;

        mode.enumValueIndex = 1;
        data.ApplyModifiedPropertiesWithoutUndo();

        Debug.Log("[RDPlayConfig] Eye tracking calibration set to OneDot (single point).");
        return 1;
    }

    /// <summary>
    /// Makes sure the safe-area boundary actually exists in the scene.
    ///
    /// RDBoundary is fitted by the scene generator, and the scene in use predates it - so
    /// see-through simply was not in the build at all, which looks exactly like the feature
    /// being broken. Adding it here means it survives a scene that is never regenerated.
    /// </summary>
    private static int FixBoundary()
    {
        Camera headset = Camera.main;
        if (headset == null) return 0;

        Transform rig = headset.transform.parent;
        if (rig == null) return 0;

        if (rig.GetComponent<RDBoundary>() != null) return 0;

        RDBoundary boundary = rig.gameObject.AddComponent<RDBoundary>();

        RDController controller = Object.FindAnyObjectByType<RDController>();
        Renderer surface = controller != null ? controller.GetComponent<Renderer>() : null;

        SerializedObject data = new SerializedObject(boundary);
        data.FindProperty("head").objectReferenceValue = headset.transform;
        data.FindProperty("centre").objectReferenceValue = rig;
        data.FindProperty("headsetCamera").objectReferenceValue = headset;
        data.FindProperty("roomSurface").objectReferenceValue = surface;
        data.ApplyModifiedPropertiesWithoutUndo();

        Debug.Log("[RDPlayConfig] Added the safe-area boundary to " + rig.name +
                  ". Set its Radius to the real floor space before the show.");

        return 1;
    }

    /// <summary>
    /// Clears out components whose script no longer exists.
    ///
    /// Deleting a script leaves every object that used it carrying a broken slot, and Unity
    /// logs about it on load forever after. Harmless, but it buries the warnings that are not.
    /// </summary>
    private static int StripMissingScripts()
    {
        int removed = 0;

        foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
            foreach (Transform node in root.GetComponentsInChildren<Transform>(true))
                removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(node.gameObject);

        if (removed == 0) return 0;

        Debug.Log("[RDPlayConfig] Removed " + removed + " component(s) whose script is gone.");
        return 1;
    }

    /// <summary>
    /// Pins the router to the gaze source.
    ///
    /// Automatic is the right default on a desk and the wrong one in a gallery. There is no mouse
    /// at the exhibition, so falling back to MouseInput does not degrade gracefully - it hands
    /// the room to a pointer that never moves, which paints one spot forever and leaves the
    /// field saturated and blank. Far better to show nothing and say why than to keep running on
    /// an input nobody is touching.
    /// </summary>
    private static int FixRouter()
    {
        RDInputSourceRouter router = Object.FindAnyObjectByType<RDInputSourceRouter>();
        if (router == null) return 0;

        SerializedObject data = new SerializedObject(router);
        SerializedProperty selection = data.FindProperty("selection");

        if (selection == null || selection.enumValueIndex == 1) return 0;

        selection.enumValueIndex = 1;
        data.ApplyModifiedPropertiesWithoutUndo();

        Debug.Log("[RDPlayConfig] Input source selection set back to Gaze Input.");
        return 1;
    }
}
