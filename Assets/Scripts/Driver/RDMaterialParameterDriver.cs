using UnityEngine;

/// <summary>
/// Writes RDTarget values into the RD material.
/// </summary>
public class RDMaterialParameterDriver : MonoBehaviour
{
    [SerializeField] private CustomRenderTexture rdTexture;
    [SerializeField] private RDOutputConfig outputConfig;

    private Material rdMaterial;

    private void Awake()
    {
        if (rdTexture != null)
            rdMaterial = rdTexture.material;
    }

    public void Apply(RDTarget target)
    {
        if (rdMaterial == null) return;

        bool writeAll = outputConfig == null;

        if (writeAll || outputConfig.writeFeed)
            rdMaterial.SetFloat("_Feed", target.feed);

        if (writeAll || outputConfig.writeKill)
            rdMaterial.SetFloat("_Kill", target.kill);

        if (writeAll || outputConfig.writeClickState)
            rdMaterial.SetFloat("_ClickState", target.clickState);

        if (writeAll || outputConfig.writeClickRadius)
            rdMaterial.SetFloat("_ClickRadius", target.clickRadius);

        if (writeAll || outputConfig.writeResetBlend)
            rdMaterial.SetFloat("_ResetBlend", target.resetBlend);

        if (writeAll || outputConfig.writeClickPos)
            rdMaterial.SetVector("_ClickPos", target.clickPos);

        rdMaterial.SetVector("_ClickDir", target.clickDir);
    }

    public void SetDebugShowRingMask(bool enabled)
    {
        if (rdMaterial == null) return;
        rdMaterial.SetFloat("_DebugShowRingMask", enabled ? 1f : 0f);
    }
}