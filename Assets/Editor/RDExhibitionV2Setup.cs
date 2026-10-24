using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

/// <summary>
/// Exhibition_v2: DeformTest's surface and feel, with the music and the permanent strategies
/// put back in.
///
/// Menu:
///   RD / Set Up Exhibition V2 Scene   - creates Assets/Scenes/Exhibition_v2.unity
///
/// What it keeps from DeformTest: the generated dense sphere, the smooth ink display with the
/// gaze bloom, the gaze depth field, and a camera drift - but on a rig parent and at the minimal
/// setting, not the desktop test values.
///
/// What it adds: the Varjo path and the input router, the synthesised score, the greedy bandit,
/// the long-term memory that outlives each visitor, the presence tracker, and the operator
/// overlay.
///
/// Decisions I made that are worth disagreeing with are listed in
/// Docs/Exhibition-V2.md, each with the one line that reverses it.
/// </summary>
public static class RDExhibitionV2Setup
{
    private const string ScenePath = "Assets/Scenes/Exhibition_v2.unity";
    private const string ConfigFolder = "Assets/Settings/RD";

    private const string CrtPath = "Assets/RDSystem/RDSystemBuffer.asset";
    private const string InkMaterialPath = "Assets/Shader/RDSystem_RDDisplayInk.mat";

    private const string DepthFieldShaderName = "RDSystem/RDGazeDepthField";
    private const string DepthFieldMaterialPath = "Assets/RDSystem/RDGazeDepthField.mat";
    private const string DepthCrtPath = "Assets/RDSystem/RDGazeDepthBuffer.asset";

    private const string DisplayShaderName = "RDSystem/RDDisplayInkDeform";
    private const string DisplayMaterialPath = "Assets/Shader/RDSystem_V2Surface.mat";

    private const string MeshPath = "Assets/RDSystem/RDV2Sphere.asset";

    // 10 m, as DeformTest. The gaze bloom's relief is computed against this, so it has to match
    // the sphere it is drawn on or the shading stretches.
    private const float Radius = 10f;
    private const int SegmentsU = 256;
    private const int SegmentsV = 128;

    // The authored look, in one place. RDRecession fades these at runtime and restores them on
    // exit; the setup resets them too, so a material that was left faded gets repaired.
    private const float AuthoredGain = 1.4f;
    private const float AuthoredRelief = 0.8f;
    private const float AuthoredBloom = 0.35f;
    private const float AuthoredValueShift = 0.10f;

    // How the room empties. The field settle is the sharp one: diffusion erases a bump in well
    // under a second, so this is a brake, not a fade, and every tenth of a second it spends
    // ramping is shape the slow contraction never gets to work on.
    private const float FieldSettleSeconds = 0.35f;
    private const float RecessionSeconds = 150f;
    private const float GraceSeconds = 4f;
    private const float BreathAmount = 0.06f;
    private const float PulseAmount = 0.10f;
    // The idle swing carries the empty room now, so the beat pump comes down to detail on top
    // of it rather than competing with it.
    private const float IdleSwing = 0.65f;
    private const float CycleSeconds = 9f;
    private const float PulseThreshold = 0.05f;
    private const float RestingDecayTau = 70f;
    private const float PulseBpmActive = 66f;
    private const float PulseBpmResting = 44f;

    // The surface terms are copied too, so the generated material IS the smooth ink display
    // rather than an approximation of it. RD/Display/Use Smooth Ink used to be the only way to
    // get the fresnel rim and the fake specular, and it bought them by swapping in a
    // built-in-pipeline shader that renders to the left eye alone in the headset.
    private static readonly string[] CopiedInkFloats =
    {
        "_CoreThreshold", "_CoreSoftness",
        "_HaloThreshold", "_HaloSoftness", "_HaloIntensity",
        "_ValuePower", "_ValueGain",
        "_FresnelPower", "_FresnelIntensity",
        "_SpecPower", "_SpecIntensity"
    };

    private static readonly string[] CopiedInkColors =
    {
        "_BackgroundColor", "_CoreColor", "_HaloColor",
        "_FresnelColor", "_SpecColor"
    };

    [MenuItem("RD/Set Up Exhibition V2 Scene", priority = 2)]
    public static void SetUpExhibitionV2()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        CustomRenderTexture crt = AssetDatabase.LoadAssetAtPath<CustomRenderTexture>(CrtPath);
        if (crt == null)
        {
            EditorUtility.DisplayDialog("RD Exhibition V2",
                "Simulation buffer not found at\n" + CrtPath + "\n\nNothing was created.", "OK");
            return;
        }

        Material depthField = EnsureDepthFieldMaterial();
        if (depthField == null) return;

        CustomRenderTexture depthCrt =
            RDProgressionTestSetup.EnsureScalarBuffer(DepthCrtPath, depthField, "gaze depth");

        if (depthCrt == null) return;

        Material display = EnsureSurfaceMaterial(crt, depthCrt);
        if (display == null) return;

        // Named before anything is built, so both the headset camera and the monitor ball can
        // use the same resolved index instead of a hardcoded 31.
        int monitorLayer = RDLayerSetup.EnsureMonitorLayer();

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // ---- assets -------------------------------------------------------

        RDStateConfig stateConfig = CreateConfigIfMissing<RDStateConfig>("RDStateConfig");
        RDMappingConfig mappingConfig = CreateConfigIfMissing<RDMappingConfig>("RDMappingConfig");
        RDOutputConfig outputConfig = CreateConfigIfMissing<RDOutputConfig>("RDOutputConfig");
        RDMusicConfig musicConfig = CreateConfigIfMissing<RDMusicConfig>("RDMusicConfig");
        RDStrategyCatalogue catalogue = CreateConfigIfMissing<RDStrategyCatalogue>("RDStrategyCatalogue");

        // Checked here rather than left for a component to discover at Awake. A scene that is
        // quietly missing a reference is worse than one that refuses to build.
        RDConfigAssets.Verify("RDExhibitionV2Setup",
            ("RDStateConfig", stateConfig),
            ("RDMappingConfig", mappingConfig),
            ("RDOutputConfig", outputConfig),
            ("RDMusicConfig", musicConfig),
            ("RDStrategyCatalogue", catalogue));

        // ---- camera, under a rig so the drift composes with the HMD -------

        GameObject rig = new GameObject("Camera Rig");

        GameObject cameraGo = new GameObject("Main Camera");
        cameraGo.tag = "MainCamera";
        cameraGo.transform.SetParent(rig.transform, false);

        Camera camera = cameraGo.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = 200f;

        // Everything except the monitor layer, so the headset never sees the little sphere.
        camera.cullingMask = monitorLayer >= 0 ? ~(1 << monitorLayer) : ~0;

        cameraGo.AddComponent<AudioListener>();
        cameraGo.AddComponent<CameraController>();

        // The headset writes the camera. The mouse-look above stands itself down whenever a
        // display is presenting, so the two never write the same rotation in the same frame.
        RDXRRigSetup.AttachHeadTracking(cameraGo);

        // The drift goes on the RIG, not the camera: mouse-look writes the camera's local
        // rotation and the HMD writes its local pose, so a drift on the same transform would be
        // overwritten every frame.
        RDAmbientDrift drift = rig.AddComponent<RDAmbientDrift>();
        SetFloat(drift, "translationAmplitude", 0.15f);
        SetVector3(drift, "translationPeriods", new Vector3(31f, 47f, 37f));
        SetFloat(drift, "rotationAmplitude", 0.4f);
        SetFloat(drift, "yawAmplitude", 0f);

        // ---- surface ------------------------------------------------------

        GameObject surface = BuildSurface(display);

        // ---- simulation ---------------------------------------------------

        global::RDSystem.RDSystemUpdater simUpdater = BuildUpdater(crt, "RDSystem", 4);
        BuildUpdater(depthCrt, "GazeDepthField", 1);

        // ---- input --------------------------------------------------------

        MouseInput mouse = surface.AddComponent<MouseInput>();
        SetEnum(mouse, "mode", (int)MouseInput.PointerMode.HoverAsGaze);
        SetObject(mouse, "sourceCamera", camera);
        SetInt(mouse, "interactionMask", ~0);
        SetFloat(mouse, "maxDistance", 200f);
        SetBool(mouse, "useScreenCentre", false);

        VarjoGazeInput varjo = surface.AddComponent<VarjoGazeInput>();
        SetObject(varjo, "xrCamera", camera);
        SetInt(varjo, "interactionMask", ~0);
        SetFloat(varjo, "maxDistance", 200f);
        SetBool(varjo, "requireThisObject", true);

        RDInputSourceRouter router = surface.AddComponent<RDInputSourceRouter>();
        SetObject(router, "preferredSource", varjo);
        SetObject(router, "fallbackSource", mouse);
        SetEnum(router, "selection", (int)RDInputSourceRouter.SourceSelection.Automatic);
        SetBool(router, "disableInactiveSource", true);

        // The headset gate sits DOWNSTREAM of the router, wrapping whatever it chose, so the real
        // input path is untouched. Press H to put the headset on and take it off; the piece sees
        // exactly what it would see if someone lifted it off the hook. Everything below reads
        // from this rather than from the router.
        RDHeadsetSimulator headset = surface.AddComponent<RDHeadsetSimulator>();
        SetObject(headset, "sourceBehaviour", router);
        SetBool(headset, "simulate", true);
        SetBool(headset, "worn", false);

        // ---- behaviour, and the memory that outlives the visitor ----------

        RDLongTermMemory memory = surface.AddComponent<RDLongTermMemory>();

        RDBehaviorStateLayer stateLayer = surface.AddComponent<RDBehaviorStateLayer>();
        SetObject(stateLayer, "config", stateConfig);
        SetObject(stateLayer, "longTermMemory", memory);

        LinearBehaviorMapper mapper = surface.AddComponent<LinearBehaviorMapper>();
        SetObject(mapper, "config", mappingConfig);
        SetObject(mapper, "uvSourceBehaviour", headset);

        RDMaterialParameterDriver driver = surface.AddComponent<RDMaterialParameterDriver>();
        SetObject(driver, "rdTexture", crt);
        SetObject(driver, "outputConfig", outputConfig);

        RDController controller = surface.AddComponent<RDController>();
        SetObject(controller, "signalSourceBehaviour", headset);
        SetObject(controller, "stateLayer", stateLayer);
        SetObject(controller, "mapper", mapper);
        SetObject(controller, "driver", driver);

        // Chase is ON here, unlike DeformTest. The bandit's evasion is meant to drive it, the
        // conductor already answers it, and the bloom follows RingDirection - so with chase on,
        // the space the viewer is opening runs away from them. That is the piece, not a bug.
        SetBool(controller, "enableChaseMode", true);

        // ---- session ------------------------------------------------------

        RDPresenceTracker presence = surface.AddComponent<RDPresenceTracker>();
        SetObject(presence, "signalSourceBehaviour", headset);
        SetObject(presence, "stateLayer", stateLayer);
        SetObject(presence, "longTermMemory", memory);

        // ---- the greedy algorithm -----------------------------------------

        RDGreedyDirector director = surface.AddComponent<RDGreedyDirector>();
        SetObject(director, "catalogue", catalogue);
        SetObject(director, "stateLayer", stateLayer);
        SetObject(director, "presenceTracker", presence);

        SetObject(mapper, "director", director);

        // Written explicitly so a scene that was edited by hand gets put back to the tuned
        // attended range.
        SetVector2(mapper, "feedBounds", new Vector2(0.021f, 0.063f));
        SetVector2(mapper, "killBounds", new Vector2(0.054f, 0.0645f));
        SetObject(controller, "director", director);

        // ---- the gaze bloom -----------------------------------------------

        RDGazeDepthDriver depth = surface.AddComponent<RDGazeDepthDriver>();
        SetObject(depth, "depthTexture", depthCrt);
        SetObject(depth, "depthFieldMaterial", depthField);
        SetObject(depth, "displayMaterial", display);
        SetObject(depth, "controller", controller);

        // ---- the room subsiding after the headset comes off ---------------

        RDRecession recession = surface.AddComponent<RDRecession>();
        SetObject(recession, "headset", headset);
        SetObject(recession, "controller", controller);
        SetObject(recession, "displayMaterial", display);
        SetObject(recession, "depthDriver", depth);
        SetObject(recession, "simulationUpdater", simUpdater);

        // ---- sound --------------------------------------------------------

        GameObject audioGo = new GameObject("Audio");
        AudioSource source = audioGo.AddComponent<AudioSource>();
        source.playOnAwake = true;
        source.loop = true;
        source.spatialBlend = 0f;
        source.clip = null;

        RDAudioEngine engine = audioGo.AddComponent<RDAudioEngine>();

        RDMusicConductor conductor = audioGo.AddComponent<RDMusicConductor>();
        SetObject(conductor, "engine", engine);
        SetObject(conductor, "controller", controller);
        SetObject(conductor, "mapper", mapper);
        SetObject(conductor, "config", musicConfig);
        SetObject(conductor, "director", director);

        // Wired here rather than with the rest of the recession, because the engine does not
        // exist until now. This is what puts the surface and the machine room on one breath.
        SetObject(recession, "audioEngine", engine);
        SetFloat(recession, "authoredGain", AuthoredGain);
        SetFloat(recession, "authoredRelief", AuthoredRelief);
        SetFloat(recession, "authoredBloom", AuthoredBloom);
        SetFloat(recession, "authoredValueShift", AuthoredValueShift);

        // Written explicitly rather than left to the component's defaults, because a scene saved
        // before these were retuned keeps whatever it was serialised with - which is how the
        // field settle stayed at 3 s, and 3 s of diffusion is the whole dent.
        // Read back off the material rather than hardcoded, because the ink thresholds are
        // copied from the source material a few lines up - so whatever was tuned there is what
        // the pulse has to return to.
        if (display.HasProperty("_CoreThreshold"))
            SetFloat(recession, "authoredCoreThreshold", display.GetFloat("_CoreThreshold"));

        if (display.HasProperty("_HaloThreshold"))
            SetFloat(recession, "authoredHaloThreshold", display.GetFloat("_HaloThreshold"));

        SetFloat(recession, "pulseThreshold", PulseThreshold);
        SetFloat(recession, "idleSwing", IdleSwing);
        SetFloat(recession, "breathSeconds", CycleSeconds);
        SetFloat(recession, "restingDecayTau", RestingDecayTau);
        SetFloat(recession, "fieldSettleSeconds", FieldSettleSeconds);
        SetFloat(recession, "recessionSeconds", RecessionSeconds);
        SetFloat(recession, "graceSeconds", GraceSeconds);
        SetFloat(recession, "breathAmount", BreathAmount);
        SetFloat(recession, "pulseAmount", PulseAmount);
        SetFloat(recession, "pulseBpmActive", PulseBpmActive);
        SetFloat(recession, "pulseBpmResting", PulseBpmResting);

        // Two sliders in front of the dozen, for the only two things judged by eye in the room.
        // Added after the recession is fully configured, so its Push does not overwrite anything
        // that still had to be set.
        RDRecessionTuner tuner = surface.AddComponent<RDRecessionTuner>();
        SetObject(tuner, "recession", recession);
        SetFloat(tuner, "idleSwing", IdleSwing);
        SetFloat(tuner, "cycleSeconds", CycleSeconds);
        SetFloat(tuner, "blobStrength", PulseThreshold);
        SetFloat(tuner, "shrinkBackSeconds", RecessionSeconds);
        SetFloat(tuner, "contractionShare", RestingDecayTau / RecessionSeconds);

        // ---- operator -----------------------------------------------------

        GameObject operatorGo = new GameObject("Operator");
        RDOperatorOverlay overlay = operatorGo.AddComponent<RDOperatorOverlay>();
        SetObject(overlay, "presenceTracker", presence);
        SetObject(overlay, "router", router);
        SetObject(overlay, "mapper", mapper);
        SetObject(overlay, "director", director);
        SetBool(overlay, "visibleOnStart", false);

        // ---- the monitor, on the second display -----------------------------

        BuildMonitor(display, crt, depthCrt, monitorLayer);

        GameObject eventSystemGo = new GameObject("EventSystem");
        eventSystemGo.AddComponent<EventSystem>();
        eventSystemGo.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();

        // ---- save ---------------------------------------------------------

        EnsureFolder("Assets/Scenes");
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log("[RDExhibitionV2Setup] Built " + ScenePath + ".\n" +
                  "Chase is ON (DeformTest had it off). Displacement is 0 - bloom only; set " +
                  "_DeformMetres on " + DisplayMaterialPath + " to 2.5 to get the dent back. " +
                  "The camera drift is on the rig at the minimal setting and MUST be tested on " +
                  "other people before the show. See Docs/Exhibition-V2.md.");

        Selection.activeGameObject = surface;
    }

    // ====================================================================

    /// <summary>
    /// A small second sphere on its own layer, with its own camera, on display 1.
    ///
    /// Why a second sphere at all: the visitor's is 10 m across with the camera at its centre, so
    /// nothing outside it can frame it without either passing through the surface or needing a
    /// 40 m room. This one is 2 m across and sits well away from the origin. It is not a copy of
    /// the pattern - it reads the same CustomRenderTexture, so it is the same pattern, seen from
    /// the other side.
    ///
    /// Layer 31 so the headset camera can exclude it. If the project is already using 31 for
    /// something, change it here and in the culling mask below together.
    /// </summary>
    private static void BuildMonitor(Material surfaceMaterial, CustomRenderTexture crt,
                                     CustomRenderTexture depthCrt, int monitorLayer)
    {
        int MonitorLayer = monitorLayer >= 0 ? monitorLayer : 0;
        const float BallRadius = 1f;

        // Far from the origin, so the visitor's 10 m sphere cannot contain it and the two can
        // never be in each other's shot.
        Vector3 home = new Vector3(0f, 0f, 400f);

        GameObject root = new GameObject("Monitor");
        root.transform.position = home;

        GameObject ball = new GameObject("Monitor Ball");
        ball.layer = MonitorLayer;
        ball.transform.SetParent(root.transform, false);

        const string meshPath = "Assets/RDSystem/RDMonitorSphere.asset";
        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);

        if (mesh == null)
        {
            mesh = RDSphereMesh.Build(BallRadius, 192, 96, "RD Monitor Sphere");

            EnsureFolder("Assets/RDSystem");
            AssetDatabase.CreateAsset(mesh, meshPath);
            AssetDatabase.SaveAssets();
        }

        ball.AddComponent<MeshFilter>().sharedMesh = mesh;

        // Its own material, because this one is seen from OUTSIDE: the relief has to be computed
        // against a 1 m radius, not the room's 10 m, or the shading is off by that factor. Same
        // textures, so the same pattern and the same bloom.
        Material monitorMaterial = EnsureMonitorMaterial(surfaceMaterial, crt, depthCrt, BallRadius);

        MeshRenderer renderer = ball.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = monitorMaterial;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        GameObject cameraGo = new GameObject("Monitor Camera");
        cameraGo.transform.SetParent(root.transform, false);
        cameraGo.transform.localPosition = new Vector3(0f, 0f, -3.2f);
        cameraGo.transform.localRotation = Quaternion.identity;

        Camera camera = cameraGo.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.031f, 0.031f, 0.039f, 1f);
        camera.cullingMask = 1 << MonitorLayer;
        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = 20f;
        camera.fieldOfView = 38f;

        RDMonitorView view = cameraGo.AddComponent<RDMonitorView>();
        SetObject(view, "ball", ball.transform);
        SetFloat(view, "secondsPerRevolution", 60f);
        SetInt(view, "targetDisplay", 1);
        SetInt(view, "renderEveryNthFrame", 3);

        Debug.Log("[RDExhibitionV2Setup] Monitor built on layer " + MonitorLayer + " ('" +
                  RDLayerSetup.MonitorLayerName + "'), display 1, one revolution a minute. " +
                  "The headset camera already excludes that layer.");
    }

    private static Material EnsureMonitorMaterial(Material source, CustomRenderTexture crt,
                                                  CustomRenderTexture depthCrt, float radius)
    {
        const string path = "Assets/Shader/RDSystem_V2Monitor.mat";

        Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        Material material = existing != null ? existing : new Material(source);

        material.SetTexture("_MainTex", crt);
        material.SetTexture("_DepthTex", depthCrt);
        material.SetFloat("_BaseRadius", radius);
        material.SetFloat("_Pixelate", 0f);
        material.SetFloat("_DeformMetres", 0f);
        material.SetFloat("_ReliefMetres", 0.25f);
        material.SetFloat("_ValueGain", AuthoredGain);
        material.SetFloat("_DepthRelief", AuthoredRelief);
        material.SetFloat("_DepthBloom", AuthoredBloom);
        material.SetFloat("_DepthValueShift", AuthoredValueShift);

        if (existing == null)
        {
            AssetDatabase.CreateAsset(material, path);
            Debug.Log("[RDExhibitionV2Setup] Created " + path + ".");
        }
        else
        {
            EditorUtility.SetDirty(material);
        }

        AssetDatabase.SaveAssets();
        return material;
    }

    private static GameObject BuildSurface(Material display)
    {
        GameObject go = new GameObject("Surface");
        go.transform.position = Vector3.zero;

        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);

        if (mesh == null)
        {
            mesh = RDSphereMesh.Build(Radius, SegmentsU, SegmentsV, "RD V2 Sphere");

            EnsureFolder("Assets/RDSystem");
            AssetDatabase.CreateAsset(mesh, MeshPath);
            AssetDatabase.SaveAssets();

            Debug.Log("[RDExhibitionV2Setup] Created " + MeshPath + " - " + mesh.vertexCount +
                      " vertices at radius " + Radius + " m.");
        }

        MeshFilter filter = go.AddComponent<MeshFilter>();
        filter.sharedMesh = mesh;

        MeshRenderer renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = display;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        // Harmless with displacement at 0, and correct the moment anyone raises it.
        Bounds bounds = mesh.bounds;
        bounds.Expand(Radius * 0.5f);
        renderer.localBounds = bounds;

        MeshCollider collider = go.AddComponent<MeshCollider>();
        collider.sharedMesh = mesh;
        collider.convex = false;

        return go;
    }

    private static global::RDSystem.RDSystemUpdater BuildUpdater(CustomRenderTexture texture, string name, int steps)
    {
        GameObject go = new GameObject(name);
        var updater = go.AddComponent<global::RDSystem.RDSystemUpdater>();

        SetObject(updater, "_texture", texture);
        SetInt(updater, "_stepsPerFrame", steps);

        return updater;
    }

    private static Material EnsureDepthFieldMaterial()
    {
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(DepthFieldMaterialPath);
        if (existing != null) return existing;

        Shader shader = Shader.Find(DepthFieldShaderName);
        if (shader == null)
        {
            Debug.LogError("[RDExhibitionV2Setup] Shader \"" + DepthFieldShaderName + "\" not found.");
            return null;
        }

        Material material = new Material(shader);
        AssetDatabase.CreateAsset(material, DepthFieldMaterialPath);
        AssetDatabase.SaveAssets();

        return material;
    }

    private static Material EnsureSurfaceMaterial(CustomRenderTexture crt, CustomRenderTexture depthCrt)
    {
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(DisplayMaterialPath);

        if (existing != null)
        {
            existing.SetTexture("_MainTex", crt);
            existing.SetTexture("_DepthTex", depthCrt);

            // Reset the four RDRecession drives. These are written at runtime, and on a shared
            // material in the editor that is permanent - so a material that has been played is
            // a material that has been faded down. Re-running the setup repairs it.
            existing.SetFloat("_ValueGain", AuthoredGain);
            existing.SetFloat("_DepthRelief", AuthoredRelief);
            existing.SetFloat("_DepthBloom", AuthoredBloom);
            existing.SetFloat("_DepthValueShift", AuthoredValueShift);

            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();

            Debug.Log("[RDExhibitionV2Setup] Reset the recession-driven values on " +
                      DisplayMaterialPath + " (gain " + AuthoredGain + ", relief " +
                      AuthoredRelief + ", bloom " + AuthoredBloom + ").");

            return existing;
        }

        Shader shader = Shader.Find(DisplayShaderName);
        if (shader == null)
        {
            Debug.LogError("[RDExhibitionV2Setup] Shader \"" + DisplayShaderName + "\" not found.");
            return null;
        }

        Material ink = AssetDatabase.LoadAssetAtPath<Material>(InkMaterialPath);
        Material material = new Material(shader);

        if (ink != null)
        {
            foreach (string name in CopiedInkFloats)
                if (ink.HasProperty(name) && material.HasProperty(name))
                    material.SetFloat(name, ink.GetFloat(name));

            foreach (string name in CopiedInkColors)
                if (ink.HasProperty(name) && material.HasProperty(name))
                    material.SetColor(name, ink.GetColor(name));
        }

        material.SetTexture("_MainTex", crt);
        material.SetTexture("_DepthTex", depthCrt);
        material.SetFloat("_BaseRadius", Radius);

        // Smooth ink, no cells.
        material.SetFloat("_Pixelate", 0f);

        // Flat: bloom without displacement, which is where this landed on 17.10. One field to
        // put the dent back.
        material.SetFloat("_DeformMetres", 0f);

        material.SetFloat("_ReliefMetres", 2.5f);
        material.SetFloat("_DepthRelief", AuthoredRelief);
        material.SetFloat("_DepthValueShift", AuthoredValueShift);
        material.SetFloat("_DepthBloom", AuthoredBloom);
        material.SetColor("_DepthBloomColor", new Color(0.62f, 0.80f, 1f, 1f));
        material.SetFloat("_DepthGain", 1.4f);

        AssetDatabase.CreateAsset(material, DisplayMaterialPath);
        AssetDatabase.SaveAssets();

        Debug.Log("[RDExhibitionV2Setup] Created " + DisplayMaterialPath + ".");
        return material;
    }

    // ==================================================================== helpers

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

    private static void SetVector2(Object target, string field, Vector2 value) =>
        Apply(target, field, p => p.vector2Value = value);

    private static void SetInt(Object target, string field, int value) =>
        Apply(target, field, p => p.intValue = value);

    private static void SetBool(Object target, string field, bool value) =>
        Apply(target, field, p => p.boolValue = value);

    private static void SetEnum(Object target, string field, int index) =>
        Apply(target, field, p => p.enumValueIndex = index);

    private static void SetVector3(Object target, string field, Vector3 value) =>
        Apply(target, field, p => p.vector3Value = value);

    private static void Apply(Object target, string field, System.Action<SerializedProperty> write)
    {
        if (target == null) return;

        SerializedObject so = new SerializedObject(target);
        SerializedProperty prop = so.FindProperty(field);

        if (prop == null)
        {
            Debug.LogWarning("[RDExhibitionV2Setup] " + target.GetType().Name +
                             " has no field '" + field + "'.");
            return;
        }

        write(prop);
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}
