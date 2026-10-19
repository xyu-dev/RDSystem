using UnityEditor;
using UnityEngine;

/// <summary>
/// Swaps the exhibition surface between the smooth ink display and the pixelated one, so the two
/// can be compared in a second rather than by hand-reassigning materials.
///
/// Menu:
///   RD / Display / Use Pixel Ink    - creates the material if needed, assigns it
///   RD / Display / Use Smooth Ink   - puts the original back
///
/// The pixel material is created by copying every colour and contrast value out of
/// RDSystem_RDDisplayInk.mat, so the two differ only in the pixelation. If you retune the ink
/// material later, delete the pixel material and run the menu item again to re-copy.
/// </summary>
public static class RDPixelInkSetup
{
    private const string InkMaterialPath = "Assets/Shader/RDSystem_RDDisplayInk.mat";
    private const string PixelMaterialPath = "Assets/Shader/RDSystem_RDDisplayInkPixel.mat";
    private const string PixelShaderName = "RDSystem/RDDisplayInkPixel";

    // 256 cells around the sphere. The equator crosses four cube faces, so that is 64 per face
    // edge - roughly 23 px per cell in the Aero.
    private const float CellsPerFace = 64f;
    private const float ColourLevels = 12f;

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

    [MenuItem("RD/Display/Use Pixel Ink", priority = 40)]
    public static void UsePixelInk()
    {
        Material pixel = EnsurePixelMaterial();
        if (pixel == null) return;

        if (Assign(pixel))
            Debug.Log("[RDPixelInkSetup] Surface is now using " + PixelMaterialPath +
                      ". Toggle _ShowCellGrid on the material to see the cell boundaries.");
    }

    [MenuItem("RD/Display/Use Smooth Ink", priority = 41)]
    public static void UseSmoothInk()
    {
        Material ink = AssetDatabase.LoadAssetAtPath<Material>(InkMaterialPath);
        if (ink == null)
        {
            Debug.LogError("[RDPixelInkSetup] " + InkMaterialPath + " not found.");
            return;
        }

        if (Assign(ink))
            Debug.Log("[RDPixelInkSetup] Surface is back on " + InkMaterialPath + ".");
    }

    // ====================================================================

    private static Material EnsurePixelMaterial()
    {
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(PixelMaterialPath);
        if (existing != null)
            return existing;

        Shader shader = Shader.Find(PixelShaderName);
        if (shader == null)
        {
            Debug.LogError("[RDPixelInkSetup] Shader \"" + PixelShaderName + "\" not found. " +
                           "Expected Assets/Shader/RDDisplayInkPixel.shader to be compiling.");
            return null;
        }

        Material ink = AssetDatabase.LoadAssetAtPath<Material>(InkMaterialPath);
        if (ink == null)
        {
            Debug.LogError("[RDPixelInkSetup] " + InkMaterialPath + " not found; cannot copy the " +
                           "exhibition look across.");
            return null;
        }

        Material pixel = new Material(shader);

        if (ink.HasProperty("_MainTex") && pixel.HasProperty("_MainTex"))
            pixel.SetTexture("_MainTex", ink.GetTexture("_MainTex"));

        foreach (string name in CopiedFloats)
        {
            if (ink.HasProperty(name) && pixel.HasProperty(name))
                pixel.SetFloat(name, ink.GetFloat(name));
        }

        foreach (string name in CopiedColors)
        {
            if (ink.HasProperty(name) && pixel.HasProperty(name))
                pixel.SetColor(name, ink.GetColor(name));
        }

        pixel.SetFloat("_CellsPerFace", CellsPerFace);
        pixel.SetFloat("_Levels", ColourLevels);
        pixel.SetFloat("_EdgeSoftness", 1f);

        AssetDatabase.CreateAsset(pixel, PixelMaterialPath);
        AssetDatabase.SaveAssets();

        Debug.Log("[RDPixelInkSetup] Created " + PixelMaterialPath + ", copying the colour and " +
                  "contrast values from " + InkMaterialPath + ".");
        return pixel;
    }

    /// <summary>
    /// Finds the surface by its RDController rather than by name, since that is the object the
    /// whole pipeline is wired to and it is the one that owns the collider the gaze hits.
    /// </summary>
    private static bool Assign(Material material)
    {
        RDController controller = Object.FindFirstObjectByType<RDController>();

        Renderer renderer = controller != null ? controller.GetComponent<Renderer>() : null;

        if (renderer == null)
        {
            GameObject named = GameObject.Find("Surface");
            if (named != null) renderer = named.GetComponent<Renderer>();
        }

        if (renderer == null)
        {
            Debug.LogError("[RDPixelInkSetup] No surface renderer found in the open scene. " +
                           "Open Exhibition.unity first.");
            return false;
        }

        Undo.RecordObject(renderer, "Swap RD display material");
        renderer.sharedMaterial = material;
        EditorUtility.SetDirty(renderer);

        Selection.activeGameObject = renderer.gameObject;
        return true;
    }
}
