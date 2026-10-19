using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds a standalone look-development scene for the QR display shader.
///
/// Menu:
///   RD / Set Up QR Pixel Test Scene   - creates Assets/Scenes/PixelQRTest.unity
///
/// Deliberately independent of RDSceneSetup rather than sharing its helpers: this scene has to
/// keep building even while the exhibition rig is being changed, and it carries none of the
/// exhibition machinery - no audio, no bandit, no session memory, no headset path. Simulation,
/// mouse gaze, the QR material, and sliders. Nothing else to load or go wrong.
/// </summary>
public static class RDQRTestSetup
{
    private const string ScenePath = "Assets/Scenes/PixelQRTest.unity";
    private const string ConfigFolder = "Assets/Settings/RD";

    private const string CrtPath = "Assets/RDSystem/RDSystemBuffer.asset";
    private const string SimMaterialPath = "Assets/RDSystem/RDSystem.mat";
    private const string RoomModelPath = "Assets/Model/SphereRoom_v2.fbx";

    private const string ShaderName = "RDSystem/RDQRDisplay";
    private const string MaterialPath = "Assets/Shader/RDSystem_RDQRDisplay.mat";

    private const string PixelCrtPath = "Assets/RDSystem/RDSystemPixelBuffer.asset";
    private const string PixelSimMaterialPath = "Assets/RDSystem/RDSystemPixel.mat";

    // 2:1, because the UV is equirectangular - 2*pi across, pi down. At this aspect a u-texel and
    // a v-texel subtend the same angle (2*pi/512 == pi/256), so the simulation stencil is
    // isotropic with nothing wasted. A square buffer under-samples longitude by 2x everywhere.
    private const int PixelWidth = 512;
    private const int PixelHeight = 256;

    [MenuItem("RD/Set Up QR Pixel Test Scene", priority = 20)]
    public static void SetUpQRTestScene()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        CustomRenderTexture crt = EnsurePixelSimulation();
        if (crt == null)
            return;

        Material display = EnsureQRMaterial(crt);
        if (display == null)
            return;

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // ---- camera ------------------------------------------------------

        GameObject cameraGo = new GameObject("Main Camera");
        cameraGo.tag = "MainCamera";
        Camera camera = cameraGo.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.nearClipPlane = 0.01f;
        cameraGo.AddComponent<CameraController>();

        // ---- surface -----------------------------------------------------

        GameObject surface = BuildSurface(display);

        // ---- simulation --------------------------------------------------

        GameObject rdSystemGo = new GameObject("RDSystem");
        var updater = rdSystemGo.AddComponent<global::RDSystem.RDSystemUpdater>();
        SetObject(updater, "_texture", crt);
        SetInt(updater, "_stepsPerFrame", 4);

        // ---- gaze by mouse, and the minimum pipeline that makes the ring follow it ----

        MouseInput mouse = surface.AddComponent<MouseInput>();
        SetEnum(mouse, "mode", (int)MouseInput.PointerMode.HoverAsGaze);
        SetObject(mouse, "sourceCamera", camera);
        SetInt(mouse, "interactionMask", ~0);
        SetFloat(mouse, "maxDistance", 50f);
        SetBool(mouse, "useScreenCentre", false);

        RDStateConfig stateConfig = CreateConfigIfMissing<RDStateConfig>("RDStateConfig");
        RDMappingConfig mappingConfig = CreateConfigIfMissing<RDMappingConfig>("RDMappingConfig");
        RDOutputConfig outputConfig = CreateConfigIfMissing<RDOutputConfig>("RDOutputConfig");

        RDBehaviorStateLayer stateLayer = surface.AddComponent<RDBehaviorStateLayer>();
        SetObject(stateLayer, "config", stateConfig);

        LinearBehaviorMapper mapper = surface.AddComponent<LinearBehaviorMapper>();
        SetObject(mapper, "config", mappingConfig);
        SetObject(mapper, "uvSourceBehaviour", mouse);

        RDMaterialParameterDriver driver = surface.AddComponent<RDMaterialParameterDriver>();
        SetObject(driver, "rdTexture", crt);
        SetObject(driver, "outputConfig", outputConfig);

        RDController controller = surface.AddComponent<RDController>();
        SetObject(controller, "signalSourceBehaviour", mouse);
        SetObject(controller, "stateLayer", stateLayer);
        SetObject(controller, "mapper", mapper);
        SetObject(controller, "driver", driver);

        // ---- the sliders -------------------------------------------------

        RDQRTuner tuner = surface.AddComponent<RDQRTuner>();
        SetObject(tuner, "target", surface.GetComponent<Renderer>());
        SetBool(tuner, "visibleOnStart", true);

        // ---- save --------------------------------------------------------

        EnsureFolder("Assets/Scenes");
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log("[RDQRTestSetup] Built " + ScenePath + ". Press Play, move the mouse to place the " +
                  "ring, and tune with the sliders (F2 hides them). Slider values are written to " +
                  MaterialPath + " and survive leaving Play mode.");

        Selection.activeGameObject = surface;
    }

    // ==================================================================== pixel simulation

    /// <summary>
    /// The pixel version of the simulation: the same RD shader, run on a small point-filtered
    /// buffer so one simulation cell is one module. The blockiness is native rather than a coarse
    /// rendering of a smooth field, which is the difference between a pixel image and a
    /// pixel-filtered image.
    ///
    /// Note what is deliberately NOT done here: the stored state is not quantised. Gray-Scott is
    /// a PDE solver and its state has to stay continuous - posterise u and v and the reaction
    /// stops progressing. Resolution is the pixelation; quantisation belongs at display.
    /// </summary>
    private static CustomRenderTexture EnsurePixelSimulation()
    {
        CustomRenderTexture existingCrt = AssetDatabase.LoadAssetAtPath<CustomRenderTexture>(PixelCrtPath);
        Material simMaterial = EnsurePixelSimMaterial();

        if (simMaterial == null)
            return null;

        if (existingCrt != null)
        {
            existingCrt.material = simMaterial;
            existingCrt.initializationMaterial = simMaterial;
            EditorUtility.SetDirty(existingCrt);
            return existingCrt;
        }

        CustomRenderTexture crt = new CustomRenderTexture(
            PixelWidth, PixelHeight, RenderTextureFormat.RGHalf, RenderTextureReadWrite.Linear);

        // Point filtering is the whole point: a bilinear tap between two cells invents an
        // intermediate value, and an intermediate value is a soft edge.
        crt.filterMode = FilterMode.Point;
        crt.useMipMap = false;
        crt.autoGenerateMips = false;

        // Longitude wraps, latitude does not.
        crt.wrapModeU = TextureWrapMode.Repeat;
        crt.wrapModeV = TextureWrapMode.Clamp;

        crt.material = simMaterial;
        crt.initializationMaterial = simMaterial;
        crt.initializationSource = CustomRenderTextureInitializationSource.Material;

        // On demand for both: RDSystemUpdater drives the stepping itself.
        crt.initializationMode = CustomRenderTextureUpdateMode.OnDemand;
        crt.updateMode = CustomRenderTextureUpdateMode.OnDemand;

        crt.doubleBuffered = true;
        crt.shaderPass = 1;

        AssetDatabase.CreateAsset(crt, PixelCrtPath);
        AssetDatabase.SaveAssets();

        Debug.Log("[RDQRTestSetup] Created " + PixelCrtPath + " at " + PixelWidth + "x" + PixelHeight +
                  ", point filtered, no mips.");
        return crt;
    }

    private static Material EnsurePixelSimMaterial()
    {
        Material reference = AssetDatabase.LoadAssetAtPath<Material>(SimMaterialPath);
        if (reference == null)
        {
            EditorUtility.DisplayDialog(
                "RD QR Test",
                "Could not find the simulation material (expected at\n" + SimMaterialPath +
                ").\n\nNothing was created.",
                "OK");
            return null;
        }

        Material existing = AssetDatabase.LoadAssetAtPath<Material>(PixelSimMaterialPath);
        if (existing != null)
            return existing;

        Material copy = new Material(reference);
        copy.SetFloat("_Pop", ScaledPop(reference));

        AssetDatabase.CreateAsset(copy, PixelSimMaterialPath);
        AssetDatabase.SaveAssets();

        Debug.Log("[RDQRTestSetup] Created " + PixelSimMaterialPath + " from " + SimMaterialPath +
                  ", with _Pop raised to " + copy.GetFloat("_Pop").ToString("0.000") +
                  " to keep the same number of initial seeds at the lower cell count.");
        return copy;
    }

    /// <summary>
    /// InitialSeedState seeds a cell with probability _Pop * _Pop * 0.01, so the expected number
    /// of seeds scales with the cell count. Dropping from 1024x1024 to 512x256 is 8x fewer cells,
    /// which at the exhibition _Pop of 0.1 would leave about a dozen seeds on the whole sphere -
    /// not enough for the reaction to get going. Derived rather than hardcoded so it stays right
    /// if the reference material is retuned.
    /// </summary>
    private static float ScaledPop(Material reference)
    {
        float referencePop = reference.HasProperty("_Pop") ? reference.GetFloat("_Pop") : 0.1f;

        CustomRenderTexture referenceCrt = AssetDatabase.LoadAssetAtPath<CustomRenderTexture>(CrtPath);
        float referenceCells = referenceCrt != null
            ? (float)referenceCrt.width * referenceCrt.height
            : 1024f * 1024f;

        float pixelCells = (float)PixelWidth * PixelHeight;

        // p scales as Pop^2, so matching the seed count means scaling Pop by sqrt(cell ratio).
        float scaled = referencePop * Mathf.Sqrt(referenceCells / Mathf.Max(1f, pixelCells));
        return Mathf.Clamp01(scaled);
    }

    // ==================================================================== material

    private static Material EnsureQRMaterial(CustomRenderTexture crt)
    {
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (existing != null)
        {
            existing.SetTexture("_MainTex", crt);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        Shader shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            EditorUtility.DisplayDialog(
                "RD QR Test",
                "Shader \"" + ShaderName + "\" was not found.\n\nExpected " +
                "Assets/Shader/RDQRDisplay.shader to be present and compiling. Nothing was created.",
                "OK");
            return null;
        }

        Material material = new Material(shader);
        material.SetTexture("_MainTex", crt);

        // Equirectangular UV: twice as many patches around as over, so modules are square at
        // the equator. They still pinch at the poles - that is the UV mapping, not the shader.
        //
        // 16 x 25 = 400 modules around, and the quiet zone compresses the code into 0.84 of each
        // patch, so the effective module pitch is about 476 against a 512-cell field. Near enough
        // to one cell per module that the modules carry the simulation rather than resample it.
        material.SetFloat("_PatchesX", 16f);
        material.SetFloat("_PatchesY", 8f);
        material.SetFloat("_ModulesPerPatch", 25f);
        material.SetFloat("_QuietZone", 0.08f);
        material.SetFloat("_Gain", 1.7f);
        material.SetFloat("_Threshold", 0.35f);
        material.SetFloat("_Dither", 0.75f);
        material.SetFloat("_MaskPattern", 8f);
        material.SetFloat("_ModuleGap", 0f);

        EnsureFolder("Assets/Shader");
        AssetDatabase.CreateAsset(material, MaterialPath);
        AssetDatabase.SaveAssets();

        Debug.Log("[RDQRTestSetup] Created " + MaterialPath + ".");
        return material;
    }

    // ==================================================================== surface

    private static GameObject BuildSurface(Material display)
    {
        EnsureModelIsReadable(RoomModelPath);

        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(RoomModelPath);
        GameObject surface;

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
            Debug.LogWarning("[RDQRTestSetup] " + RoomModelPath + " not found; used a primitive sphere. " +
                             "The viewer is outside it, not inside.");
        }

        surface.name = "Surface";
        surface.transform.position = Vector3.zero;

        MeshFilter filter = surface.GetComponentInChildren<MeshFilter>();
        if (filter != null && filter.sharedMesh != null)
        {
            GameObject meshHost = filter.gameObject;

            MeshCollider collider = meshHost.GetComponent<MeshCollider>();
            if (collider == null) collider = meshHost.AddComponent<MeshCollider>();
            collider.sharedMesh = filter.sharedMesh;
            collider.convex = false;

            if (meshHost != surface)
                surface = meshHost;

            if (!filter.sharedMesh.isReadable)
            {
                Debug.LogError("[RDQRTestSetup] Mesh '" + filter.sharedMesh.name + "' is still not " +
                               "Read/Write enabled; hit.textureCoord will return (0,0).");
            }
        }
        else
        {
            Debug.LogError("[RDQRTestSetup] No MeshFilter on the surface; gaze will never register a hit.");
        }

        Renderer renderer = surface.GetComponent<Renderer>();
        if (renderer != null && display != null)
            renderer.sharedMaterial = display;

        return surface;
    }

    private static void EnsureModelIsReadable(string modelPath)
    {
        ModelImporter importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
        if (importer == null) return;
        if (importer.isReadable) return;

        importer.isReadable = true;
        importer.SaveAndReimport();
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
        string running = parts[0];

        for (int i = 1; i < parts.Length; i++)
        {
            string next = running + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(running, parts[i]);
            running = next;
        }
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
        if (target == null) return;

        SerializedObject so = new SerializedObject(target);
        SerializedProperty prop = so.FindProperty(field);

        if (prop == null)
        {
            Debug.LogWarning("[RDQRTestSetup] " + target.GetType().Name + " has no field '" + field + "'.");
            return;
        }

        write(prop);
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}
