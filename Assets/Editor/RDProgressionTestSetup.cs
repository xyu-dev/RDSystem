using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds the two progression test scenes.
///
/// Menu:
///   RD / Set Up Resolve Test Scene   - Assets/Scenes/ResolveTest.unity   (pixel resolves to smooth)
///   RD / Set Up Shell Test Scene     - Assets/Scenes/ShellTest.unity     (expanding translucent stack)
///
/// Both are independent of the exhibition rig and of each other: simulation, mouse gaze, the one
/// mechanic under test, and a readout. No audio, no bandit, no session memory, no headset path.
/// </summary>
public static class RDProgressionTestSetup
{
    private const string ResolveScenePath = "Assets/Scenes/ResolveTest.unity";
    private const string ShellScenePath = "Assets/Scenes/ShellTest.unity";

    private const string ConfigFolder = "Assets/Settings/RD";
    private const string CrtPath = "Assets/RDSystem/RDSystemBuffer.asset";
    private const string SimMaterialPath = "Assets/RDSystem/RDSystem.mat";
    private const string RoomModelPath = "Assets/Model/SphereRoom_v2.fbx";

    private const string ResolveShaderName = "RDSystem/RDResolveField";
    private const string ResolveFieldMaterialPath = "Assets/RDSystem/RDResolveField.mat";
    private const string ResolveFieldCrtPath = "Assets/RDSystem/RDResolveBuffer.asset";

    private const string ResolveDisplayShaderName = "RDSystem/RDDisplayInkResolve";
    private const string ResolveDisplayMaterialPath = "Assets/Shader/RDSystem_RDDisplayInkResolve.mat";

    private const string ShellShaderName = "RDSystem/RDDisplayInkShell";
    private const string ShellMaterialPath = "Assets/Shader/RDSystem_RDDisplayInkShell.mat";

    private const string DeformScenePath = "Assets/Scenes/DeformTest.unity";
    private const string DepthFieldShaderName = "RDSystem/RDGazeDepthField";
    private const string DepthFieldMaterialPath = "Assets/RDSystem/RDGazeDepthField.mat";
    private const string DepthFieldCrtPath = "Assets/RDSystem/RDGazeDepthBuffer.asset";
    private const string DeformShaderName = "RDSystem/RDDisplayInkDeform";
    private const string DeformMaterialPath = "Assets/Shader/RDSystem_RDDisplayInkDeform.mat";

    // The generated sphere. 10 m radius, 2.5 m of displacement - a quarter of the radius, which is
    // unmistakable - and 256 x 128 segments, giving 25 cm between vertices at the equator.
    private const float DeformRadius = 10f;
    private const float DeformMetres = 2.5f;
    private const int DeformSegmentsU = 256;
    private const int DeformSegmentsV = 128;

    // Matches the simulation's own aspect reasoning: equirectangular wants 2:1.
    private const int ResolveWidth = 512;
    private const int ResolveHeight = 256;

    private static readonly string[] CopiedInkFloats =
    {
        "_CoreThreshold", "_CoreSoftness",
        "_HaloThreshold", "_HaloSoftness", "_HaloIntensity",
        "_ValuePower", "_ValueGain"
    };

    private static readonly string[] CopiedInkColors =
    {
        "_BackgroundColor", "_CoreColor", "_HaloColor"
    };

    // ==================================================================== scene 1

    [MenuItem("RD/Set Up Resolve Test Scene", priority = 30)]
    public static void SetUpResolveTestScene()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        CustomRenderTexture crt = AssetDatabase.LoadAssetAtPath<CustomRenderTexture>(CrtPath);
        if (crt == null)
        {
            Fail("Could not find the simulation buffer at\n" + CrtPath);
            return;
        }

        Material resolveFieldMaterial = EnsureMaterial(ResolveShaderName, ResolveFieldMaterialPath, null);
        if (resolveFieldMaterial == null) return;

        CustomRenderTexture resolveCrt = EnsureResolveBuffer(resolveFieldMaterial);
        if (resolveCrt == null) return;

        Material display = EnsureMaterial(ResolveDisplayShaderName, ResolveDisplayMaterialPath, m =>
        {
            CopyInkLook(m);
            m.SetTexture("_MainTex", crt);
            m.SetTexture("_ResolveTex", resolveCrt);
            m.SetFloat("_CellsPerFace", 64f);
            m.SetFloat("_Levels", 12f);
            m.SetFloat("_EdgeSoftness", 1f);
            m.SetFloat("_ResolveThreshold", 0.30f);
            m.SetFloat("_ResolveJitter", 0.12f);
        });

        if (display == null) return;

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        Camera camera = BuildCamera();
        GameObject surface = BuildSurface(display);

        BuildSimulation(crt, "RDSystem", 4);
        BuildSimulation(resolveCrt, "ResolveField", 1);

        RDController controller = BuildPipeline(surface, camera, crt);

        // Chase moves the ring away from the gaze, which would make the resolution evade the
        // viewer. That is a good idea later; it makes the mechanic illegible now.
        SetBool(controller, "enableChaseMode", false);

        RDResolveProgression progression = surface.AddComponent<RDResolveProgression>();
        SetObject(progression, "resolveTexture", resolveCrt);
        SetObject(progression, "resolveMaterial", resolveFieldMaterial);
        SetObject(progression, "displayMaterial", display);
        SetObject(progression, "controller", controller);
        SetObject(progression, "simulationTexture", crt);
        SetInt(progression, "baseCellsPerFace", 64);

        GameObject hudGo = new GameObject("HUD");
        RDProgressionHUD hud = hudGo.AddComponent<RDProgressionHUD>();
        SetObject(hud, "resolve", progression);

        Save(scene, ResolveScenePath);

        Debug.Log("[RDProgressionTestSetup] Built " + ResolveScenePath +
                  ". Press Play and sweep the mouse around the sphere - one gaze point cannot " +
                  "finish it. F3 hides the readout, R resets.");

        Selection.activeGameObject = surface;
    }

    // ==================================================================== scene 2

    [MenuItem("RD/Set Up Shell Test Scene", priority = 31)]
    public static void SetUpShellTestScene()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        CustomRenderTexture crt = AssetDatabase.LoadAssetAtPath<CustomRenderTexture>(CrtPath);
        if (crt == null)
        {
            Fail("Could not find the simulation buffer at\n" + CrtPath);
            return;
        }

        Material shell = EnsureMaterial(ShellShaderName, ShellMaterialPath, m =>
        {
            CopyInkLook(m);
            m.SetTexture("_MainTex", crt);
            m.SetFloat("_CellWorldSize", 0.12f);
            m.SetFloat("_MinCellArcDegrees", 0.2f);
            m.SetFloat("_Levels", 12f);
            m.SetFloat("_EdgeSoftness", 1f);
            m.SetFloat("_HaloAlpha", 0.42f);
            m.SetFloat("_CoreAlpha", 0.92f);
        });

        if (shell == null) return;

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        Camera camera = BuildCamera();

        // The interaction surface is invisible and stays at a fixed radius. The shells must not
        // own the collider: they are born, grow and retire, and the gaze UV would jump every time
        // one of them passed the camera.
        GameObject surface = BuildSurface(null);
        Renderer surfaceRenderer = surface.GetComponent<Renderer>();
        if (surfaceRenderer != null) surfaceRenderer.enabled = false;

        BuildSimulation(crt, "RDSystem", 4);

        RDController controller = BuildPipeline(surface, camera, crt);
        SetBool(controller, "enableChaseMode", false);

        GameObject stackGo = new GameObject("Shell Stack");
        RDShellStack stack = stackGo.AddComponent<RDShellStack>();
        SetObject(stack, "rdTexture", crt);
        SetObject(stack, "shellMaterialTemplate", shell);
        SetObject(stack, "controller", controller);
        SetFloat(stack, "cellWorldSize", 0.12f);

        GameObject hudGo = new GameObject("HUD");
        RDProgressionHUD hud = hudGo.AddComponent<RDProgressionHUD>();
        SetObject(hud, "shells", stack);

        Save(scene, ShellScenePath);

        Debug.Log("[RDProgressionTestSetup] Built " + ShellScenePath +
                  ". Press Play and hold the mouse on the sphere; the stack grows while you look. " +
                  "F3 hides the readout, R resets.");

        Selection.activeGameObject = stackGo;
    }

    // ==================================================================== scene 3

    [MenuItem("RD/Set Up Deform Test Scene", priority = 32)]
    public static void SetUpDeformTestScene()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        CustomRenderTexture crt = AssetDatabase.LoadAssetAtPath<CustomRenderTexture>(CrtPath);
        if (crt == null)
        {
            Fail("Could not find the simulation buffer at\n" + CrtPath);
            return;
        }

        Material depthFieldMaterial = EnsureMaterial(DepthFieldShaderName, DepthFieldMaterialPath, null);
        if (depthFieldMaterial == null) return;

        CustomRenderTexture depthCrt = EnsureScalarBuffer(
            DepthFieldCrtPath, depthFieldMaterial, "gaze depth");

        if (depthCrt == null) return;

        Material display = EnsureMaterial(DeformShaderName, DeformMaterialPath, m =>
        {
            CopyInkLook(m);
            m.SetTexture("_MainTex", crt);
            m.SetTexture("_DepthTex", depthCrt);
            m.SetFloat("_BaseRadius", DeformRadius);
            m.SetFloat("_DeformMetres", DeformMetres);
            m.SetFloat("_DepthGain", 1.4f);
            m.SetFloat("_DepthValueShift", 0.10f);
            m.SetFloat("_DepthRelief", 0.8f);

            // 24 cm cells on a 10 m sphere is 1.4 degrees - the same apparent cell size as the
            // other scenes, so the comparison is about the deformation and nothing else.
            m.SetFloat("_CellWorldSize", 0.24f);
            m.SetFloat("_Levels", 12f);
            m.SetFloat("_EdgeSoftness", 1f);
        });

        if (display == null) return;

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // The camera sits under a rig, so the sway component can translate it while
        // CameraController keeps rotating it. Putting both on one transform would have them
        // fighting over localPosition.
        GameObject rig = new GameObject("Camera Rig");
        Camera camera = BuildCamera();
        camera.transform.SetParent(rig.transform, false);
        camera.transform.localPosition = Vector3.zero;

        camera.gameObject.AddComponent<RDTestHeadSway>();

        GameObject surface = BuildDeformSurface(display);

        BuildSimulation(crt, "RDSystem", 4);
        BuildSimulation(depthCrt, "GazeDepthField", 1);

        RDController controller = BuildPipeline(surface, camera, crt);
        SetBool(controller, "enableChaseMode", false);

        RDGazeDepthDriver driver = surface.AddComponent<RDGazeDepthDriver>();
        SetObject(driver, "depthTexture", depthCrt);
        SetObject(driver, "depthFieldMaterial", depthFieldMaterial);
        SetObject(driver, "displayMaterial", display);
        SetObject(driver, "controller", controller);
        SetFloat(driver, "decayTau", 10f);

        GameObject hudGo = new GameObject("HUD");
        hudGo.AddComponent<RDProgressionHUD>();

        Save(scene, DeformScenePath);

        Debug.Log("[RDProgressionTestSetup] Built " + DeformScenePath +
                  ". Hold the mouse on one spot to open the space, then WASD / Q / E to move and " +
                  "see the dent parallax. F4 stops the camera sway, and it MUST be off in the " +
                  "headset. _ShowDepth on the material draws the raw field.");

        Selection.activeGameObject = surface;
    }

    /// <summary>
    /// A generated sphere, because the deformation is a vertex displacement and the room model
    /// does not have the vertex count. Saved into the scene so it survives a reload.
    /// </summary>
    private static GameObject BuildDeformSurface(Material display)
    {
        GameObject go = new GameObject("Surface");
        go.transform.position = Vector3.zero;

        // Saved as an asset rather than left to serialise inline: 33k vertices embedded in the
        // scene file is a few megabytes of YAML, and the mesh is worth reusing.
        const string meshPath = "Assets/RDSystem/RDDeformSphere.asset";

        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);

        if (mesh == null)
        {
            mesh = RDSphereMesh.Build(DeformRadius, DeformSegmentsU, DeformSegmentsV, "RD Deform Sphere");
            RDSphereMesh.ExpandBoundsForDisplacement(mesh, DeformMetres);

            EnsureFolder("Assets/RDSystem");
            AssetDatabase.CreateAsset(mesh, meshPath);
            AssetDatabase.SaveAssets();

            Debug.Log("[RDProgressionTestSetup] Created " + meshPath + " - " +
                      mesh.vertexCount + " vertices, " + (mesh.triangles.Length / 3) + " triangles.");
        }

        MeshFilter filter = go.AddComponent<MeshFilter>();
        filter.sharedMesh = mesh;

        MeshRenderer renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = display;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        // The collider stays on the UNDEFORMED mesh, and that is correct rather than a shortcut:
        // the displacement is radial, so it cannot change the direction of any point, and the
        // gaze only ever needed the direction. A collider rebuilt every frame would cost a mesh
        // bake for no change in where the ray lands.
        MeshCollider collider = go.AddComponent<MeshCollider>();
        collider.sharedMesh = mesh;
        collider.convex = false;

        return go;
    }

    // ==================================================================== shared pieces

    private static Camera BuildCamera()
    {
        GameObject go = new GameObject("Main Camera");
        go.tag = "MainCamera";

        Camera camera = go.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.nearClipPlane = 0.01f;

        // The shell stack reaches 24 m and the viewer sits inside all of it.
        camera.farClipPlane = 400f;

        go.AddComponent<CameraController>();
        return camera;
    }

    private static void BuildSimulation(CustomRenderTexture texture, string name, int steps)
    {
        GameObject go = new GameObject(name);
        var updater = go.AddComponent<global::RDSystem.RDSystemUpdater>();
        SetObject(updater, "_texture", texture);
        SetInt(updater, "_stepsPerFrame", steps);
    }

    private static RDController BuildPipeline(GameObject surface, Camera camera, CustomRenderTexture crt)
    {
        MouseInput mouse = surface.AddComponent<MouseInput>();
        SetEnum(mouse, "mode", (int)MouseInput.PointerMode.HoverAsGaze);
        SetObject(mouse, "sourceCamera", camera);
        SetInt(mouse, "interactionMask", ~0);
        SetFloat(mouse, "maxDistance", 500f);
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

        return controller;
    }

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
            Debug.LogWarning("[RDProgressionTestSetup] " + RoomModelPath + " not found; used a primitive sphere.");
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
                Debug.LogError("[RDProgressionTestSetup] Mesh '" + filter.sharedMesh.name +
                               "' is not Read/Write enabled; hit.textureCoord will return (0,0).");
            }
        }
        else
        {
            Debug.LogError("[RDProgressionTestSetup] No MeshFilter on the surface; gaze will never hit.");
        }

        Renderer renderer = surface.GetComponent<Renderer>();
        if (renderer != null && display != null)
            renderer.sharedMaterial = display;

        return surface;
    }

    private static CustomRenderTexture EnsureResolveBuffer(Material fieldMaterial) =>
        EnsureScalarBuffer(ResolveFieldCrtPath, fieldMaterial, "resolve");

    /// <summary>
    /// A single-channel equirectangular buffer at 512 x 256, on demand, double buffered. R8 is all
    /// either field needs - one scalar per texel - and it makes a readback one byte per texel.
    /// </summary>
    internal static CustomRenderTexture EnsureScalarBuffer(string path, Material fieldMaterial, string label)
    {
        CustomRenderTexture existing = AssetDatabase.LoadAssetAtPath<CustomRenderTexture>(path);

        if (existing != null)
        {
            existing.material = fieldMaterial;
            existing.initializationMaterial = fieldMaterial;
            EditorUtility.SetDirty(existing);
            return existing;
        }

        CustomRenderTexture crt = new CustomRenderTexture(
            ResolveWidth, ResolveHeight, RenderTextureFormat.R8, RenderTextureReadWrite.Linear);

        crt.filterMode = FilterMode.Bilinear;
        crt.useMipMap = false;
        crt.autoGenerateMips = false;
        crt.wrapModeU = TextureWrapMode.Repeat;
        crt.wrapModeV = TextureWrapMode.Clamp;

        crt.material = fieldMaterial;
        crt.initializationMaterial = fieldMaterial;
        crt.initializationSource = CustomRenderTextureInitializationSource.Material;
        crt.initializationMode = CustomRenderTextureUpdateMode.OnDemand;
        crt.updateMode = CustomRenderTextureUpdateMode.OnDemand;
        crt.doubleBuffered = true;
        crt.shaderPass = 1;

        EnsureFolder("Assets/RDSystem");
        AssetDatabase.CreateAsset(crt, path);
        AssetDatabase.SaveAssets();

        Debug.Log("[RDProgressionTestSetup] Created the " + label + " buffer at " + path +
                  ", " + ResolveWidth + "x" + ResolveHeight + ", R8.");
        return crt;
    }

    private static Material EnsureMaterial(string shaderName, string path, System.Action<Material> configure)
    {
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) return existing;

        Shader shader = Shader.Find(shaderName);
        if (shader == null)
        {
            Fail("Shader \"" + shaderName + "\" was not found. Check it is present and compiling.");
            return null;
        }

        Material material = new Material(shader);

        if (configure != null) configure(material);

        EnsureFolder(System.IO.Path.GetDirectoryName(path).Replace('\\', '/'));
        AssetDatabase.CreateAsset(material, path);
        AssetDatabase.SaveAssets();

        Debug.Log("[RDProgressionTestSetup] Created " + path + ".");
        return material;
    }

    /// <summary>
    /// Copies the exhibition ink look across, so the progression scenes differ from the show only
    /// in the mechanic under test rather than in the palette as well.
    /// </summary>
    private static void CopyInkLook(Material target)
    {
        Material ink = AssetDatabase.LoadAssetAtPath<Material>("Assets/Shader/RDSystem_RDDisplayInk.mat");
        if (ink == null) return;

        foreach (string name in CopiedInkFloats)
            if (ink.HasProperty(name) && target.HasProperty(name))
                target.SetFloat(name, ink.GetFloat(name));

        foreach (string name in CopiedInkColors)
            if (ink.HasProperty(name) && target.HasProperty(name))
                target.SetColor(name, ink.GetColor(name));
    }

    private static void Save(Scene scene, string path)
    {
        EnsureFolder("Assets/Scenes");
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, path);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    private static void Fail(string message)
    {
        EditorUtility.DisplayDialog("RD Progression Test", message + "\n\nNothing was created.", "OK");
    }

    // ==================================================================== helpers

    private static void EnsureModelIsReadable(string modelPath)
    {
        ModelImporter importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
        if (importer == null) return;
        if (importer.isReadable) return;

        importer.isReadable = true;
        importer.SaveAndReimport();
    }

    /// <summary>
    /// Delegates to the one shared implementation. The four private copies this replaced all
    /// returned an unverified load, which is how a broken asset became a silent null reference.
    /// </summary>
    private static T CreateConfigIfMissing<T>(string assetName) where T : ScriptableObject =>
        RDConfigAssets.Ensure<T>(ConfigFolder, assetName);

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
            Debug.LogWarning("[RDProgressionTestSetup] " + target.GetType().Name +
                             " has no field '" + field + "'.");
            return;
        }

        write(prop);
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}
