using UnityEditor;
using UnityEngine;

/// <summary>
/// Names the layer the monitor sphere lives on, so the headset camera can exclude it.
///
/// Menu:
///   RD / Name The Monitor Layer
///
/// The scene setup calls this itself, so the menu item is only for checking or repairing. Layers
/// live in ProjectSettings/TagManager.asset, which is edited through SerializedObject like any
/// other asset - there is no reason for this to be a manual step.
/// </summary>
internal static class RDLayerSetup
{
    internal const string MonitorLayerName = "RDMonitor";

    [MenuItem("RD/Name The Monitor Layer", priority = 60)]
    private static void NameMonitorLayerMenu()
    {
        int layer = EnsureMonitorLayer();

        if (layer >= 0)
            Debug.Log("[RDLayerSetup] '" + MonitorLayerName + "' is layer " + layer + ".");
    }

    /// <summary>
    /// Returns the index of the monitor layer, creating it if it does not exist yet.
    ///
    /// Prefers 31 and walks down, because the high slots are the ones a project is least likely
    /// to want later. Returns -1 and logs if every user layer is taken, which the caller should
    /// treat as "put the ball on Default and accept that the headset can see it".
    /// </summary>
    internal static int EnsureMonitorLayer()
    {
        Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");

        if (assets == null || assets.Length == 0)
        {
            Debug.LogError("[RDLayerSetup] Could not open ProjectSettings/TagManager.asset.");
            return -1;
        }

        SerializedObject tagManager = new SerializedObject(assets[0]);
        SerializedProperty layers = tagManager.FindProperty("layers");

        if (layers == null || !layers.isArray)
        {
            Debug.LogError("[RDLayerSetup] TagManager has no 'layers' array.");
            return -1;
        }

        // Already named? Use it, wherever it sits.
        for (int i = 0; i < layers.arraySize; i++)
        {
            if (layers.GetArrayElementAtIndex(i).stringValue == MonitorLayerName)
                return i;
        }

        // Layers 0-7 are Unity's; 8 upward are the project's.
        for (int i = layers.arraySize - 1; i >= 8; i--)
        {
            SerializedProperty slot = layers.GetArrayElementAtIndex(i);

            if (!string.IsNullOrEmpty(slot.stringValue)) continue;

            slot.stringValue = MonitorLayerName;
            tagManager.ApplyModifiedProperties();

            AssetDatabase.SaveAssets();

            Debug.Log("[RDLayerSetup] Named layer " + i + " '" + MonitorLayerName +
                      "'. The monitor sphere goes on it and the headset camera excludes it.");

            return i;
        }

        Debug.LogError("[RDLayerSetup] Every user layer is taken, so the monitor sphere has " +
                       "nowhere of its own to live. Free one, or the headset will see it.");

        return -1;
    }
}
