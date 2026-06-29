using UnityEngine;

/// <summary>
/// 最小调试版：只负责让 RDShader 圆环出现，
/// 不做行为映射、不做追逐模式。
/// </summary>
public class RDRingDebugController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private CustomRenderTexture rdTexture;
    [SerializeField] private MonoBehaviour signalSourceBehaviour; // MouseInput

    [Header("Ring Parameters")]
    [SerializeField] private float idleClickRadius = 0.40f;   // 弧度，0.4 ~ 0.8 都比较明显
    [SerializeField] private float gazeClickRadius = 0.40f;

    [Header("Direction Alignment (Shader Sphere)")]
    [SerializeField] private bool swapXZ = true;
    [SerializeField] private bool invertY = true;

    private Material rdMaterial;
    private IInteractionSignalSource signalSource;

    private void Awake()
    {
        if (rdTexture != null)
            rdMaterial = rdTexture.material;

        if (signalSourceBehaviour == null)
            signalSourceBehaviour = GetComponent<MouseInput>();

        signalSource = signalSourceBehaviour as IInteractionSignalSource;

        if (signalSource == null)
            Debug.LogError("[RDRingDebugController] signalSourceBehaviour must implement IInteractionSignalSource.");
        if (rdMaterial == null)
            Debug.LogError("[RDRingDebugController] rdTexture/material is null.");
    }

    private void Update()
    {
        if (rdMaterial == null || signalSource == null)
            return;

        // 1. ClickState：只要按着鼠标，就设为 1
        float clickState = signalSource.IsGazing ? 1f : 0f;
        rdMaterial.SetFloat("_ClickState", clickState);

        // 2. 半径：固定一个你能看见的参数（先不做复杂逻辑）
        float radius = signalSource.IsGazing ? gazeClickRadius : idleClickRadius;
        rdMaterial.SetFloat("_ClickRadius", radius);

        // 3. 简单方向：用 local 命中方向，按 shader 球面坐标做一点修正
        Vector3 localDir = signalSource.InteractionDirection;
        if (localDir.sqrMagnitude < 0.000001f)
            localDir = Vector3.forward;

        Vector3 d = localDir.normalized;

        if (swapXZ)
            d = new Vector3(d.z, d.y, d.x);
        if (invertY)
            d.y *= -1f;

        rdMaterial.SetVector("_ClickDir", d);

        // 4. 为了避免被 ResetBlend 清掉，先锁死为 0
        rdMaterial.SetFloat("_ResetBlend", 0f);
    }
}