using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds the standalone the exhibition actually runs on.
///
/// The Editor was never the plan. A built player has no licence that can decide to revalidate
/// halfway through a visit, no asset import pausing the room, no Editor chrome to hide behind a
/// borderless window, and it takes exclusive fullscreen - which covers the taskbar properly
/// rather than by asking the shell to hide itself.
///
/// The step that matters most here is the first one. RDPlayConfig repairs the scene every time
/// Play is pressed, and none of that exists in a build: it is an editor hook. So the fixes have
/// to be written into the scene and SAVED before the build reads it, or the player ships with
/// the broken catalogue reference, the headset gate still simulating, and no boundary at all.
///
/// Menu:
///   RD / Build Exhibition Player
/// </summary>
public static class RDBuild
{
    private const string Scene = "Assets/Scenes/Exhibition_v2.unity";
    private const string OutputFolder = "Build/RDExhibition";
    private const string Executable = "RD Exhibition.exe";

    /// <summary>Public so it can be driven headlessly: Unity.exe -executeMethod RDBuild.BuildExhibition</summary>
    [MenuItem("RD/Build Exhibition Player", priority = 1)]
    public static void BuildExhibition()
    {
        if (EditorApplication.isPlaying)
        {
            Debug.LogError("[RDBuild] Stop Play mode first.");
            return;
        }

        if (!File.Exists(Scene))
        {
            Debug.LogError("[RDBuild] " + Scene + " is missing.");
            return;
        }

        // 1. The scene has to be correct on disk, because RDPlayConfig cannot run in a player.
        if (SceneManager.GetActiveScene().path != Scene)
            EditorSceneManager.OpenScene(Scene);

        // Direct call, not ExecuteMenuItem: menu dispatch is unreliable in batch mode, and this
        // step is the one that decides whether the player ships repaired or broken.
        RDPlayConfig.ApplyShowConfig();

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();

        // 2. Ship this scene and nothing else. The list still held Exhibition.unity and a mouse
        //    input test, either of which would have loaded instead of the piece.
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(Scene, true) };

        // 3. Exclusive fullscreen, so the player owns the display rather than floating above the
        //    shell and hoping nothing takes focus.
        PlayerSettings.fullScreenMode = FullScreenMode.ExclusiveFullScreen;
        PlayerSettings.defaultIsNativeResolution = true;
        PlayerSettings.runInBackground = true;
        PlayerSettings.visibleInBackground = true;
        PlayerSettings.resizableWindow = false;
        PlayerSettings.forceSingleInstance = true;

        // Company and product are deliberately left alone. They build persistentDataPath, which
        // is where the receipts and the bandit's memory live - renaming them mid-run would point
        // the player at an empty folder and lose the record of the exhibition so far.

        string root = Directory.GetParent(Application.dataPath).FullName;
        string path = Path.Combine(root, OutputFolder, Executable);

        Directory.CreateDirectory(Path.GetDirectoryName(path));

        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = new[] { Scene },
            locationPathName = path,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };

        Debug.Log("[RDBuild] Building to " + path + " ...");

        BuildReport report = BuildPipeline.BuildPlayer(options);

        if (report.summary.result == BuildResult.Succeeded)
        {
            Debug.Log("[RDBuild] Build succeeded: " +
                      (report.summary.totalSize / (1024 * 1024)) + " MB in " +
                      report.summary.totalTime.TotalSeconds.ToString("0") + "s." +
                      System.Environment.NewLine + path);

            EditorUtility.RevealInFinder(path);
        }
        else
        {
            Debug.LogError("[RDBuild] Build " + report.summary.result + " with " +
                           report.summary.totalErrors + " error(s). See the console above.");
        }
    }
}
