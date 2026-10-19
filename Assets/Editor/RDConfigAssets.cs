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
            // A file that will not load as T is broken, not absent. Replacing it is the only
            // repair: the serialized data is fine, but nothing can read it without the binding.
            Debug.LogWarning("[RDConfigAssets] " + path + " exists but will not load as " +
                             typeof(T).Name + " - its script binding is broken. Replacing it.");

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
