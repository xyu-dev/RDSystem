using UnityEditor;
using UnityEngine;

/// <summary>
/// One correct way to get a generated config asset, shared by every scene builder.
///
/// There were four copies of this before, and all four had the same bug. The old version did:
///
///     string[] found = AssetDatabase.FindAssets("t:" + typeof(T).Name);
///     if (found.Length > 0)
///         return AssetDatabase.LoadAssetAtPath&lt;T&gt;(AssetDatabase.GUIDToAssetPath(found[0]));
///
/// and returned whatever that load produced - including null. An asset can exist on disk and
/// still fail to load: RDStrategyCatalogue.asset was written with "m_Script: {fileID: 0}", no
/// binding to its class, only an m_EditorClassIdentifier fallback. Unity could not resolve it to
/// RDStrategyCatalogue, LoadAssetAtPath returned null, the helper passed that null straight
/// through, and SerializedObject silently assigned nothing. The first sign anything was wrong was
/// RDGreedyDirector complaining at Awake, in a scene built hours earlier.
///
/// So: never return an unverified load, treat a file that will not load as BROKEN rather than
/// missing, and say so at build time instead of letting a component discover it at runtime.
/// </summary>
internal static class RDConfigAssets
{
    internal static T Ensure<T>(string folder, string assetName) where T : ScriptableObject
    {
        EnsureFolder(folder);

        string path = folder + "/" + assetName + ".asset";

        T existing = AssetDatabase.LoadAssetAtPath<T>(path);
        if (existing != null) return existing;

        if (System.IO.File.Exists(path))
        {
            // A file that will not load as T is broken, not absent. Repair the binding IN PLACE
            // before considering replacement: the serialized data is fine, and deleting the
            // asset mints a new GUID, which silently breaks every scene reference pointing at
            // the old one. That is how the catalogue came back a second time - repaired on disk,
            // then overwritten when Unity re-serialized the broken copy it still held.
            if (TryRepairScriptBinding<T>(path))
            {
                T repaired = AssetDatabase.LoadAssetAtPath<T>(path);

                if (repaired != null)
                {
                    Debug.Log("[RDConfigAssets] Repaired the script binding on " + path +
                              "; its GUID and data are intact.");

                    return repaired;
                }
            }

            Debug.LogWarning("[RDConfigAssets] " + path + " exists but will not load as " +
                             typeof(T).Name + " and could not be repaired. Replacing it - any " +
                             "scene reference to it will need reassigning.");

            AssetDatabase.DeleteAsset(path);
        }
        else
        {
            // Only look elsewhere when the expected path is genuinely empty, and only accept a
            // hit that actually loads.
            foreach (string guid in AssetDatabase.FindAssets("t:" + typeof(T).Name))
            {
                T found = AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guid));
                if (found != null) return found;
            }
        }

        T created = ScriptableObject.CreateInstance<T>();

        AssetDatabase.CreateAsset(created, path);
        AssetDatabase.SaveAssets();

        // Force the re-import that writes the script reference. Without it the asset can land
        // with m_Script {fileID: 0} - which is exactly how the catalogue broke.
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        T verified = AssetDatabase.LoadAssetAtPath<T>(path);

        if (verified == null)
        {
            Debug.LogError("[RDConfigAssets] Created " + path + " but it still will not load as " +
                           typeof(T).Name + ". Using the in-memory instance so this build is " +
                           "wired, but the asset needs looking at before the show.");

            return created;
        }

        return verified;
    }

    /// <summary>
    /// Writes the missing script reference back onto an asset that lost it, keeping its GUID.
    ///
    /// A ScriptableObject saved with "m_Script: {fileID: 0}" still loads as an object - just not
    /// as its own class - so SerializedObject can reach the field and put the MonoScript back.
    /// That is the whole repair, and it is worth preferring over delete-and-recreate for one
    /// reason: the GUID survives, so every scene already pointing at this asset keeps working.
    /// </summary>
    private static bool TryRepairScriptBinding<T>(string path) where T : ScriptableObject
    {
        Object broken = AssetDatabase.LoadMainAssetAtPath(path);
        if (broken == null) return false;

        SerializedObject data = new SerializedObject(broken);
        SerializedProperty script = data.FindProperty("m_Script");

        if (script == null) return false;

        T probe = ScriptableObject.CreateInstance<T>();
        MonoScript mono = MonoScript.FromScriptableObject(probe);
        Object.DestroyImmediate(probe);

        if (mono == null) return false;

        script.objectReferenceValue = mono;
        data.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(broken);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        return true;
    }

    /// <summary>
    /// Repairs the generated configs without rebuilding a scene.
    ///
    /// The scene builders call Ensure as a side effect of regenerating everything, which is no
    /// use once a scene has been tuned by hand. This runs the same check on its own.
    /// </summary>
    [MenuItem("RD/Repair Config Assets", priority = 61)]
    private static void RepairConfigAssets()
    {
        const string folder = "Assets/Settings/RD";

        Ensure<RDStrategyCatalogue>(folder, "RDStrategyCatalogue");
        Ensure<RDMusicConfig>(folder, "RDMusicConfig");

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log("[RDConfigAssets] Config assets checked.");
    }

    /// <summary>
    /// Reports any config that came back null, at build time. A scene that is quietly missing a
    /// reference is worse than one that refuses to build.
    /// </summary>
    internal static bool Verify(string context, params (string name, Object asset)[] configs)
    {
        bool ok = true;

        foreach ((string name, Object asset) in configs)
        {
            if (asset != null) continue;

            Debug.LogError("[" + context + "] " + name + " is null - whatever reads it will do " +
                           "nothing. Check Assets/Settings/RD and run the menu item again.");
            ok = false;
        }

        return ok;
    }

    private static void EnsureFolder(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return;
        if (AssetDatabase.IsValidFolder(folder)) return;

        string[] parts = folder.Split('/');
        string running = parts[0];

        for (int i = 1; i < parts.Length; i++)
        {
            string next = running + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(running, parts[i]);
            running = next;
        }
    }
}
