using UnityEditor;
using UnityEngine;

/// <summary>
/// Adds the gaze bloom to the exhibition surface: the soft wash that spreads outward from where
/// the viewer is looking, with the surface held completely FLAT.
///
/// Menu:
///   RD / Display / Use Smooth Ink + Gaze Bloom
///
/// That bloom is what DeformTest actually shows most of the time - it is the gaze depth field
/// diffusing outward, read as shading and colour rather than as geometry. It has nothing to do
/// with the greedy algorithm or the accumulated session data.
///
/// Because _DeformMetres is 0, none of the deformation machinery applies: no vertex displacement,
/// so no dense mesh is needed and the room model is used untouched, and no expanded bounds, so
/// culling is unchanged. This is far less invasive than the deform integration it replaces.
/// </summary>
public static class RDGazeBloomSetup
{
    private const string CrtPath = "Assets/RDSystem/RDSystemBuffer.asset";
    private const string InkMaterialPath = "Assets/Shader/RDSystem_RDDisplayInk.mat";

    private const string DepthFieldShaderName = "RDSystem/RDGazeDepthField";
    private const string DepthFieldMaterialPath = "Assets/RDSystem/RDGazeDepthField.mat";
    private const string DepthCrtPath = "Assets/RDSystem/RDGazeDepthBuffer.asset";

    private const string BloomShaderName = "RDSystem/RDDisplayInkDeform";
    private const string BloomMaterialPath = "Assets/Shader/RDSystem_RDGazeBloom.mat";

    private static readonly string[] CopiedFloats =
    {
        "_CoreThreshold", "_CoreSoftness",
        "_HaloThreshold", "_HaloSoftness", "_HaloIntensity",
        "_ValuePower", "_ValueGain"
    };

    private static readonly string[] CopiedColors =
    {
        "_BackgroundColor", "_CoreColor", "_HaloColor"
    };

    [MenuItem("RD/Display/Use Smooth Ink + Gaze Bloom", priority = 43)]
    public static void UseGazeBloom()
    {
        RDController controller = Object.FindFirstObjectByType<RDController>();

        if (controller == null)
        {
            Debug.LogError("[RDGazeBloomSetup] No RDController in the open scene. " +
                           "Open Exhibition.unity first.");
            return;
        }

        Renderer renderer = controller.GetComponent<Renderer>();
        MeshFilter filter = controller.GetComponent<MeshFilter>();

        if (renderer == null || filter == null || filter.sharedMesh == null)
        {
            Debug.LogError("[RDGazeBloomSetup] The controller's object has no mesh renderer.");
            return;
        }

        CustomRenderTexture crt = AssetDatabase.LoadAssetAtPath<CustomRenderTexture>(CrtPath);
        if (crt == null)
        {
            Debug.LogError("[RDGazeBloomSetup] Simulation buffer not found at " + CrtPath + ".");
            return;
        }

        Material depthField = EnsureDepthFieldMaterial();
        if (depthField == null) return;

        CustomRenderTexture depthCrt =
            RDProgressionTestSetup.EnsureScalarBuffer(DepthCrtPath, depthField, "gaze depth");

        if (depthCrt == null) return;

        float radius = MeasureRadius(filter.sharedMesh, controller.transform);

        Material display = EnsureBloomMaterial(crt, depthCrt, radius);
        if (display == null) return;

        Undo.RecordObject(renderer, "Use gaze bloom");
        renderer.sharedMaterial = display;
        EditorUtility.SetDirty(renderer);

        RDGazeDepthDriver driver = controller.GetComponent<RDGazeDepthDriver>();
        if (driver == null) driver = controller.gameObject.AddComponent<RDGazeDepthDriver>();

        SetObject(driver, "depthTexture", depthCrt);
        SetObject(driver, "depthFieldMaterial", depthField);
        SetObject(driver, "displayMaterial", display);
        SetObject(driver, "controller", controller);

        EnsureDepthFieldUpdater(depthCrt);

        Debug.Log("[RDGazeBloomSetup] Surface is now using " + BloomMaterialPath +
                  ". Displacement is 0 - the surface stays flat and only the bloom moves. " +
                  "_DepthBloom and _DepthRelief are the two strengths to tune; _ShowDepth draws " +
                  "the raw field.");

        Selection.activeGameObject = controller.gameObject;
    }

    private static Material EnsureBloomMaterial(CustomRenderTexture crt, CustomRenderTexture depthCrt, float radius)
    {
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(BloomMaterialPath);

        if (existing != null)
        {
            existing.SetTexture("_MainTex", crt);
            existing.SetTexture("_DepthTex", depthCrt);
            existing.SetFloat("_BaseRadius", radius);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        Shader shader = Shader.Find(BloomShaderName);
        if (shader == null)
        {
            Debug.LogError("[RDGazeBloomSetup] Shader \"" + BloomShaderName + "\" not found.");
            return null;
        }

        Material ink = AssetDatabase.LoadAssetAtPath<Material>(InkMaterialPath);
        Material material = new Material(shader);

        if (ink != null)
        {
            foreach (string name in CopiedFloats)
                if (ink.HasProperty(name) && material.HasProperty(name))
                    material.SetFloat(name, ink.GetFloat(name));

            foreach (string name in CopiedColors)
                if (ink.HasProperty(name) && material.HasProperty(name))
                    material.SetColor(name, ink.GetColor(name));
        }

        material.SetTexture("_MainTex", crt);
        material.SetTexture("_DepthTex", depthCrt);
        material.SetFloat("_BaseRadius", radius);

        // Smooth ink, no cells.
        material.SetFloat("_Pixelate", 0f);

        // Flat. This is the whole point of this variant.
        material.SetFloat("_DeformMetres", 0f);

        // The relief and the bloom read the field directly, so they still work with the surface
        // flat. _ReliefMetres is the virtual height the shading is computed from.
        material.SetFloat("_ReliefMetres", 2.5f);
        material.SetFloat("_DepthRelief", 0.8f);
        material.SetFloat("_DepthBloom", 0.35f);
        material.SetColor("_DepthBloomColor", new Color(0.62f, 0.80f, 1f, 1f));
        material.SetFloat("_DepthGain", 1.4f);
        material.SetFloat("_DepthValueShift", 0.10f);

        AssetDatabase.CreateAsset(material, BloomMaterialPath);
        AssetDatabase.SaveAssets();

        Debug.Log("[RDGazeBloomSetup] Created " + BloomMaterialPath + " for a " +
                  radius.ToString("0.0") + " m room, with displacement at 0.");

        return material;
    }

    private static Material EnsureDepthFieldMaterial()
    {
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(DepthFieldMaterialPath);
        if (existing != null) return existing;

        Shader shader = Shader.Find(DepthFieldShaderName);
        if (shader == null)
        {
            Debug.LogError("[RDGazeBloomSetup] Shader \"" + DepthFieldShaderName + "\" not found.");
            return null;
        }

        Material material = new Material(shader);
        AssetDatabase.CreateAsset(material, DepthFieldMaterialPath);
        AssetDatabase.SaveAssets();

        return material;
    }

    private static float MeasureRadius(Mesh mesh, Transform transform)
    {
        Vector3 extents = mesh.bounds.extents;
        Vector3 scale = transform.lossyScale;

        float r = Mathf.Max(
            Mathf.Abs(extents.x * scale.x),
            Mathf.Max(Mathf.Abs(extents.y * scale.y), Mathf.Abs(extents.z * scale.z)));

        return Mathf.Max(0.5f, r);
    }

    private static void EnsureDepthFieldUpdater(CustomRenderTexture depthCrt)
    {
        foreach (var existing in Object.FindObjectsByType<global::RDSystem.RDSystemUpdater>(FindObjectsSortMode.None))
        {
            SerializedObject so = new SerializedObject(existing);
            SerializedProperty prop = so.FindProperty("_texture");

            if (prop != null && prop.objectReferenceValue == depthCrt)
                return;
        }

        GameObject go = new GameObject("GazeDepthField");
        var updater = go.AddComponent<global::RDSystem.RDSystemUpdater>();

        SetObject(updater, "_texture", depthCrt);

        SerializedObject updaterSo = new SerializedObject(updater);
        SerializedProperty steps = updaterSo.FindProperty("_stepsPerFrame");

        if (steps != null)
        {
            steps.intValue = 1;
            updaterSo.ApplyModifiedPropertiesWithoutUndo();
        }
    }

    private static void SetObject(Object target, string field, Object value)
    {
        if (target == null) return;

        SerializedObject so = new SerializedObject(target);
        SerializedProperty prop = so.FindProperty(field);

        if (prop == null)
        {
            Debug.LogWarning("[RDGazeBloomSetup] " + target.GetType().Name +
                             " has no field '" + field + "'.");
            return;
        }

        prop.objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}
