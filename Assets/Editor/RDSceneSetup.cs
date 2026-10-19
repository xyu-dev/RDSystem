using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds and wires the exhibition rig, so the scene can be rebuilt from scratch at any point
/// instead of being a hand-assembled artefact nobody dares touch three days before a show.
///
/// Menu:
///   RD / Set Up Exhibition Scene   - creates Assets/Scenes/Exhibition.unity, fully wired
///   RD / Validate Current Scene    - reports what is missing or unassigned, without changing anything
///
/// Everything is assigned through SerializedObject, because the runtime fields are private
/// [SerializeField] - which is correct, and means the Inspector is the only other way in.
/// </summary>
public static class RDSceneSetup
{
    private const string ScenePath = "Assets/Scenes/Exhibition.unity";
    private const string ConfigFolder = "Assets/Settings/RD";

    private const string CrtPath = "Assets/RDSystem/RDSystemBuffer.asset";
    private const string DisplayMaterialPath = "Assets/Shader/RDSystem_RDDisplayInk.mat";
    private const string RoomModelPath = "Assets/Model/SphereRoom_v2.fbx";

    // ==================================================================== menu

    [MenuItem("RD/Set Up Exhibition Scene", priority = 0)]
    public static void SetUpExhibitionScene()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        CustomRenderTexture crt = LoadOrFind<CustomRenderTexture>(CrtPath, "RDSystemBuffer");
        if (crt == null)
        {
            EditorUtility.DisplayDialog(
                "RD Setup",
                "Could not find the reaction-diffusion CustomRenderTexture (expected at\n" +
                CrtPath + ").\n\nNothing was created.",
                "OK");
            return;
        }

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // ---- assets ------------------------------------------------------

        RDStateConfig stateConfig = CreateConfigIfMissing<RDStateConfig>("RDStateConfig");
        RDMappingConfig mappingConfig = CreateConfigIfMissing<RDMappingConfig>("RDMappingConfig");
        RDOutputConfig outputConfig = CreateConfigIfMissing<RDOutputConfig>("RDOutputConfig");
        RDMusicConfig musicConfig = CreateConfigIfMissing<RDMusicConfig>("RDMusicConfig");

        // ---- camera ------------------------------------------------------

        GameObject cameraGo = new GameObject("Main Camera");
        cameraGo.tag = "MainCamera";
        Camera camera = cameraGo.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.nearClipPlane = 0.01f;
        cameraGo.AddComponent<AudioListener>();

        // Mouse-look, so the desktop build can be driven without a headset.
        cameraGo.AddComponent<CameraController>();

        // ---- the room ----------------------------------------------------

        GameObject surface = BuildSurface();

        // ---- simulation driver -------------------------------------------

        GameObject rdSystemGo = new GameObject("RDSystem");
        var updater = rdSystemGo.AddComponent<global::RDSystem.RDSystemUpdater>();
        SetObject(updater, "_texture", crt);
        SetInt(updater, "_stepsPerFrame", 4);

        // ---- input sources (must sit on the collider they raycast against) --

        MouseInput mouse = surface.AddComponent<MouseInput>();
        SetEnum(mouse, "mode", (int)MouseInput.PointerMode.HoverAsGaze);
        SetObject(mouse, "sourceCamera", camera);
        SetInt(mouse, "interactionMask", ~0);
        SetFloat(mouse, "maxDistance", 50f);
        SetBool(mouse, "useScreenCentre", false);

        VarjoGazeInput varjo = surface.AddComponent<VarjoGazeInput>();
        SetObject(varjo, "xrCamera", camera);
        SetInt(varjo, "interactionMask", ~0);
        SetFloat(varjo, "maxDistance", 20f);
        SetBool(varjo, "requireThisObject", true);

        RDInputSourceRouter router = surface.AddComponent<RDInputSourceRouter>();
        SetObject(router, "preferredSource", varjo);
        SetObject(router, "fallbackSource", mouse);
        SetEnum(router, "selection", (int)RDInputSourceRouter.SourceSelection.Automatic);
        SetBool(router, "disableInactiveSource", true);

        // ---- behaviour pipeline -------------------------------------------

        RDLongTermMemory memory = surface.AddComponent<RDLongTermMemory>();

        RDBehaviorStateLayer stateLayer = surface.AddComponent<RDBehaviorStateLayer>();
        SetObject(stateLayer, "config", stateConfig);
        SetObject(stateLayer, "longTermMemory", memory);

        LinearBehaviorMapper mapper = surface.AddComponent<LinearBehaviorMapper>();
        SetObject(mapper, "config", mappingConfig);
        SetObject(mapper, "uvSourceBehaviour", router);

        RDMaterialParameterDriver driver = surface.AddComponent<RDMaterialParameterDriver>();
        SetObject(driver, "rdTexture", crt);
        SetObject(driver, "outputConfig", outputConfig);

        RDController controller = surface.AddComponent<RDController>();
        SetObject(controller, "signalSourceBehaviour", router);
        SetObject(controller, "stateLayer", stateLayer);
        SetObject(controller, "mapper", mapper);
        SetObject(controller, "driver", driver);

        // ---- session -------------------------------------------------------

        RDPresenceTracker presence = surface.AddComponent<RDPresenceTracker>();
        SetObject(presence, "signalSourceBehaviour", router);
        SetObject(presence, "stateLayer", stateLayer);
        SetObject(presence, "longTermMemory", memory);

        // ---- the greedy algorithm -------------------------------------------

        RDStrategyCatalogue catalogue = CreateConfigIfMissing<RDStrategyCatalogue>("RDStrategyCatalogue");

        RDGreedyDirector director = surface.AddComponent<RDGreedyDirector>();
        SetObject(director, "catalogue", catalogue);
        SetObject(director, "stateLayer", stateLayer);
        SetObject(director, "presenceTracker", presence);

        // It steers the pattern, the ring, and the ensemble.
        SetObject(mapper, "director", director);
        SetObject(controller, "director", director);

        // ---- sound ---------------------------------------------------------

        GameObject audioGo = new GameObject("Audio");
        AudioSource source = audioGo.AddComponent<AudioSource>();
        source.playOnAwake = true;
        source.loop = true;
        source.spatialBlend = 0f;   // the engine does its own panning
        source.clip = null;         // OnAudioFilterRead still runs with no clip

        RDAudioEngine engine = audioGo.AddComponent<RDAudioEngine>();

        RDMusicConductor conductor = audioGo.AddComponent<RDMusicConductor>();
        SetObject(conductor, "engine", engine);
        SetObject(conductor, "controller", controller);
        SetObject(conductor, "mapper", mapper);
        SetObject(conductor, "config", musicConfig);
        SetObject(conductor, "director", director);

        // ---- operator overlay -----------------------------------------------

        GameObject operatorGo = new GameObject("Operator");
        RDOperatorOverlay overlay = operatorGo.AddComponent<RDOperatorOverlay>();
        SetObject(overlay, "presenceTracker", presence);
        SetObject(overlay, "router", router);
        SetObject(overlay, "mapper", mapper);
        SetObject(overlay, "director", director);
        SetBool(overlay, "visibleOnStart", false);

        // ---- event system (click mode needs the uGUI pointer path) ----------

        GameObject eventSystemGo = new GameObject("EventSystem");
        eventSystemGo.AddComponent<EventSystem>();
        eventSystemGo.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();

        // ---- save ------------------------------------------------------------

        EnsureFolder("Assets/Scenes");
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        AddSceneToBuildSettings(ScenePath);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log("[RDSceneSetup] Built " + ScenePath + ". Run RD/Validate Current Scene for a checklist.");

        ValidateCurrentScene();
        Selection.activeGameObject = surface;
    }

    // ==================================================================== surface

    /// <summary>
    /// Instantiates the sphere room if the model is present, otherwise falls back to an
    /// inverted primitive sphere so the scene is still usable.
    /// </summary>
    private static GameObject BuildSurface()
    {
        GameObject surface = null;

        // Reimport BEFORE instantiating. Doing it afterwards replaces the mesh objects, leaving
        // any collider already pointing at the pre-import mesh - which is still not readable, so
        // hit.textureCoord keeps returning (0,0) and the fix appears not to have worked.
        EnsureModelIsReadable(RoomModelPath);

        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(RoomModelPath);

        if (model != null)
        {
            surface = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(surface, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        }
        else
        {
            surface = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(surface.GetComponent<SphereCollider>());
            surface.transform.localScale = Vector3.one * 10f;
            Debug.LogWarning("[RDSceneSetup] " + RoomModelPath + " not found; used a primitive sphere. " +
                             "The viewer is outside it, not inside - swap in the room model before the show.");
        }

        surface.name = "Surface";
        surface.transform.position = Vector3.zero;

        // hit.textureCoord - which is how gaze becomes a UV - requires a non-convex MeshCollider.
        MeshFilter filter = surface.GetComponentInChildren<MeshFilter>();
        if (filter != null && filter.sharedMesh != null)
        {
            GameObject meshHost = filter.gameObject;

            MeshCollider collider = meshHost.GetComponent<MeshCollider>();
            if (collider == null) collider = meshHost.AddComponent<MeshCollider>();
            collider.sharedMesh = filter.sharedMesh;
            collider.convex = false;

            // The components that raycast must live on the object that owns the collider.
            if (meshHost != surface)
                surface = meshHost;

            if (!filter.sharedMesh.isReadable)
            {
                Debug.LogError("[RDSceneSetup] Mesh '" + filter.sharedMesh.name + "' is still not " +
                               "Read/Write enabled. hit.textureCoord will return (0,0) and the ring " +
                               "will sit at one pole. Enable Read/Write on the model importer.");
            }
        }
        else
        {
            Debug.LogError("[RDSceneSetup] No MeshFilter found on the surface; gaze will never register a hit.");
        }

        Material display = AssetDatabase.LoadAssetAtPath<Material>(DisplayMaterialPath);
        Renderer renderer = surface.GetComponent<Renderer>();
        if (display != null && renderer != null)
            renderer.sharedMaterial = display;

        return surface;
    }

    /// <summary>
    /// hit.textureCoord silently returns (0,0) when the mesh is not CPU-readable, which shows up
    /// as "the ring is stuck at one pole" rather than as an error. Fix it at the importer, before
    /// anything has been instantiated from the model.
    /// </summary>
    private static void EnsureModelIsReadable(string modelPath)
    {
        ModelImporter importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
        if (importer == null) return;          // no such model; the caller falls back to a primitive
        if (importer.isReadable) return;

        importer.isReadable = true;
        importer.SaveAndReimport();

        Debug.Log("[RDSceneSetup] Enabled Read/Write on " + modelPath + " so gaze can be turned into a UV.");
    }

    // ==================================================================== validation

    [MenuItem("RD/Validate Current Scene", priority = 1)]
    public static void ValidateCurrentScene()
    {
        List<string> problems = new List<string>();

        RDController controller = Object.FindFirstObjectByType<RDController>();
        if (controller == null)
        {
            problems.Add("No RDController in the scene.");
        }
        else
        {
            CheckRef(controller, "signalSourceBehaviour", problems);
            CheckRef(controller, "stateLayer", problems);
            CheckRef(controller, "mapper", problems);
            CheckRef(controller, "driver", problems);
        }

        var stateLayer = Object.FindFirstObjectByType<RDBehaviorStateLayer>();
        if (stateLayer == null) problems.Add("No RDBehaviorStateLayer.");
        else CheckRef(stateLayer, "config", problems);

        var mapper = Object.FindFirstObjectByType<LinearBehaviorMapper>();
        if (mapper == null) problems.Add("No LinearBehaviorMapper.");
        else
        {
            CheckRef(mapper, "config", problems);
            CheckRef(mapper, "uvSourceBehaviour", problems);
        }

        var driver = Object.FindFirstObjectByType<RDMaterialParameterDriver>();
        if (driver == null) problems.Add("No RDMaterialParameterDriver.");
        else CheckRef(driver, "rdTexture", problems);

        var updater = Object.FindFirstObjectByType<global::RDSystem.RDSystemUpdater>();
        if (updater == null) problems.Add("No RDSystemUpdater - the simulation will never step.");
        else CheckRef(updater, "_texture", problems);

        var router = Object.FindFirstObjectByType<RDInputSourceRouter>();
        if (router == null)
        {
            problems.Add("No RDInputSourceRouter - there is no mouse/headset fallback.");
        }
        else
        {
            CheckRef(router, "preferredSource", problems);
            CheckRef(router, "fallbackSource", problems);

            MeshCollider collider = router.GetComponent<MeshCollider>();
            if (collider == null)
                problems.Add("The input sources are not on a GameObject with a MeshCollider; gaze will never hit.");
            else if (collider.convex)
                problems.Add("The MeshCollider is convex; hit.textureCoord needs a non-convex collider.");
            else if (collider.sharedMesh != null && !collider.sharedMesh.isReadable)
                problems.Add("The collider mesh is not Read/Write enabled; hit.textureCoord will return (0,0).");
        }

        var engine = Object.FindFirstObjectByType<RDAudioEngine>();
        if (engine == null)
        {
            problems.Add("No RDAudioEngine - the piece will be silent.");
        }
        else
        {
            AudioSource source = engine.GetComponent<AudioSource>();
            if (source == null) problems.Add("RDAudioEngine has no AudioSource; OnAudioFilterRead will never be called.");
            else if (!source.playOnAwake) problems.Add("The AudioSource is not Play On Awake; no audio callback until it plays.");
            else if (source.spatialBlend > 0.01f) problems.Add("The AudioSource is 3D; set Spatial Blend to 2D (the engine pans internally).");
        }

        var conductor = Object.FindFirstObjectByType<RDMusicConductor>();
        if (conductor == null) problems.Add("No RDMusicConductor.");
        else
        {
            CheckRef(conductor, "engine", problems);
            CheckRef(conductor, "controller", problems);
            CheckRef(conductor, "config", problems);
        }

        var director = Object.FindFirstObjectByType<RDGreedyDirector>();
        if (director == null)
        {
            problems.Add("No RDGreedyDirector - the room will not adapt to what holds attention.");
        }
        else
        {
            CheckRef(director, "catalogue", problems);
            CheckRef(director, "stateLayer", problems);

            if (mapper != null && new SerializedObject(mapper).FindProperty("director").objectReferenceValue == null)
                problems.Add("LinearBehaviorMapper.director is unassigned; the strategy will not steer the pattern.");

            if (controller != null && new SerializedObject(controller).FindProperty("director").objectReferenceValue == null)
                problems.Add("RDController.director is unassigned; the strategy will not steer the ring.");

            if (conductor != null && new SerializedObject(conductor).FindProperty("director").objectReferenceValue == null)
                problems.Add("RDMusicConductor.director is unassigned; the strategy will not be heard.");
        }

        if (Object.FindFirstObjectByType<EventSystem>() == null)
            problems.Add("No EventSystem - MouseInput's ClickAndDrag mode will not receive pointer events.");

        if (Camera.main == null)
            problems.Add("No camera tagged MainCamera - both input sources fall back to Camera.main.");

        if (problems.Count == 0)
        {
            Debug.Log("[RDSceneSetup] Scene validates clean. " + SceneManager.GetActiveScene().name + " is ready.");
        }
        else
        {
            Debug.LogWarning("[RDSceneSetup] " + problems.Count + " problem(s):\n  - " + string.Join("\n  - ", problems));
        }
    }

    private static void CheckRef(Object target, string field, List<string> problems)
    {
        SerializedObject so = new SerializedObject(target);
        SerializedProperty prop = so.FindProperty(field);

        if (prop == null)
            problems.Add(target.GetType().Name + "." + field + " no longer exists (script changed?).");
        else if (prop.objectReferenceValue == null)
            problems.Add(target.GetType().Name + "." + field + " is unassigned.");
    }

    // ==================================================================== helpers

    /// <summary>
    /// Delegates to the one shared implementation. The four private copies this replaced all
    /// returned an unverified load, which is how a broken asset became a silent null reference.
    /// </summary>
    private static T CreateConfigIfMissing<T>(string assetName) where T : ScriptableObject =>
        RDConfigAssets.Ensure<T>(ConfigFolder, assetName);

    private static T LoadOrFind<T>(string path, string nameHint) where T : Object
    {
        T direct = AssetDatabase.LoadAssetAtPath<T>(path);
        if (direct != null) return direct;

        foreach (string guid in AssetDatabase.FindAssets(nameHint + " t:" + typeof(T).Name))
        {
            T found = AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guid));
            if (found != null) return found;
        }

        return null;
    }

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;

        string[] parts = folder.Split('/');
        string current = parts[0];

        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }

    private static void AddSceneToBuildSettings(string path)
    {
        List<EditorBuildSettingsScene> scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);

        foreach (EditorBuildSettingsScene existing in scenes)
        {
            if (existing.path == path)
            {
                existing.enabled = true;
                EditorBuildSettings.scenes = scenes.ToArray();
                return;
            }
        }

        // The exhibition scene goes first, so a build with no scene argument opens the show.
        scenes.Insert(0, new EditorBuildSettingsScene(path, true));
        EditorBuildSettings.scenes = scenes.ToArray();

        Debug.Log("[RDSceneSetup] Added " + path + " to Build Settings as scene 0.");
    }

    private static void SetObject(Object target, string field, Object value) =>
        Apply(target, field, p => p.objectReferenceValue = value);

    private static void SetFloat(Object target, string field, float value) =>
        Apply(target, field, p => p.floatValue = value);

    private static void SetInt(Object target, string field, int value) =>
        Apply(target, field, p => p.intValue = value);

    private static void SetBool(Object target, string field, bool value) =>
        Apply(target, field, p => p.boolValue = value);

    private static void SetEnum(Object target, string field, int index) =>
        Apply(target, field, p => p.enumValueIndex = index);

    private static void Apply(Object target, string field, System.Action<SerializedProperty> write)
    {
        SerializedObject so = new SerializedObject(target);
        SerializedProperty prop = so.FindProperty(field);

        if (prop == null)
        {
            Debug.LogWarning("[RDSceneSetup] " + target.GetType().Name + " has no serialized field '" + field + "'.");
            return;
        }

        write(prop);
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}
