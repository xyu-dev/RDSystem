using UnityEngine;

/// <summary>
/// Coordinator: SignalSource -> StateLayer -> Mapper -> Driver
/// Final version:
/// - Normal mode: ring center follows gaze point exactly
/// - Chase mode: ring center randomly flips around gaze point in shader sphere space
/// Works with any input source implementing:
/// - IInteractionSignalSource
/// - IInteractionUVSource
/// </summary>
public class RDController : MonoBehaviour
{
    private enum RingMode
    {
        Normal,
        Chase
    }

    [Header("References")]
    [SerializeField] private MonoBehaviour signalSourceBehaviour;
    [SerializeField] private RDBehaviorStateLayer stateLayer;
    [SerializeField] private LinearBehaviorMapper mapper;
    [SerializeField] private RDMaterialParameterDriver driver;

    [Header("Interaction Defaults")]
    [SerializeField] private float idleClickRadius = 0.25f;
    [SerializeField] private float gazeClickRadius = 0.25f;

    [Header("Ring Modes")]
    [SerializeField] private bool enableChaseMode = true;
    [SerializeField] private float chaseTriggerChancePerSecond = 0.18f;
    [SerializeField] private Vector2 chaseDurationRange = new Vector2(0.5f, 1.2f);

    [Header("Chase Flip Options")]
    [SerializeField] private bool allowFlipHorizontal = true;
    [SerializeField] private bool allowFlipVertical = true;
    [SerializeField] private bool allowFlipBoth = true;

    [Header("Direction Motion")]
    [SerializeField] private float directionSmoothSpeed = 16f;

    [Header("Greedy Strategy")]
    [Tooltip("Optional. When assigned, the bandit's current strategy scales the ring size and how " +
             "readily the ring evades the gaze.")]
    [SerializeField] private RDGreedyDirector director;

    [Tooltip("Ring radius at strategy ringWidth = 0 and = 1, in radians.")]
    [SerializeField] private Vector2 strategyRadiusRange = new Vector2(0.12f, 0.55f);

    [Tooltip("Chase trigger chance per second at strategy evasion = 1.")]
    [SerializeField] private float strategyMaxChaseChance = 0.6f;

    [Header("Debug")]
    [SerializeField] private bool debugShowRingMask = false;

    /// <summary>Latest behaviour reading. Consumed by the music conductor.</summary>
    public RDBehaviorState CurrentState { get; private set; }

    /// <summary>Latest RD parameters written to the material. Consumed by the music conductor.</summary>
    public RDTarget CurrentTarget { get; private set; }

    /// <summary>True while the ring is evading the viewer's gaze.</summary>
    public bool ChaseActive => currentMode == RingMode.Chase;

    /// <summary>Smoothed ring direction in shader sphere space.</summary>
    public Vector3 RingDirection => smoothRingDir;

    private IInteractionSignalSource signalSource;
    private IInteractionUVSource uvSource;

    private RingMode currentMode = RingMode.Normal;
    private float chaseTimer = 0f;
    private bool chaseFlipHorizontal = false;
    private bool chaseFlipVertical = false;

    private Vector3 smoothRingDir = Vector3.up;
    private bool initializedDir = false;

    private void Awake()
    {
        if (signalSourceBehaviour == null)
            signalSourceBehaviour = GetComponent<MonoBehaviour>();

        if (stateLayer == null)
            stateLayer = GetComponent<RDBehaviorStateLayer>();

        if (mapper == null)
            mapper = GetComponent<LinearBehaviorMapper>();

        if (driver == null)
            driver = GetComponent<RDMaterialParameterDriver>();

        signalSource = signalSourceBehaviour as IInteractionSignalSource;
        uvSource = signalSourceBehaviour as IInteractionUVSource;

        if (signalSource == null)
            Debug.LogError("[RDController] signalSourceBehaviour must implement IInteractionSignalSource.");

        if (uvSource == null)
            Debug.LogError("[RDController] signalSourceBehaviour must implement IInteractionUVSource.");
    }

    private void Update()
    {
        if (signalSource == null || uvSource == null || stateLayer == null || mapper == null || driver == null)
            return;

        stateLayer.Tick(signalSource);

        RDBehaviorState state = stateLayer.Current;
        RDTarget target = mapper.Evaluate(state);

        target.clickState = signalSource.IsGazing ? 1f : 0f;

        // The greedy algorithm decides how big the reactive ring is; the gaze decides where.
        float activeRadius = gazeClickRadius;

        if (director != null && director.HasCatalogue)
        {
            activeRadius = Mathf.Lerp(
                strategyRadiusRange.x, strategyRadiusRange.y, Mathf.Clamp01(director.Current.ringWidth));
        }

        target.clickRadius = signalSource.IsGazing ? activeRadius : idleClickRadius;

        Vector3 gazeDir = SphereUVToDir_CSharp(uvSource.CurrentUV);
        Vector3 finalDir = ResolveRingDirection(gazeDir, signalSource.IsGazing);

        target.clickDir = finalDir;

        driver.Apply(target);
        driver.SetDebugShowRingMask(debugShowRingMask);

        CurrentState = state;
        CurrentTarget = target;
    }

    private Vector3 SphereUVToDir_CSharp(Vector2 uv)
    {
        const float PI = 3.14159265359f;

        float phi = uv.x * 2.0f * PI;
        float theta = uv.y * PI;

        float x = Mathf.Sin(theta) * Mathf.Cos(phi);
        float y = Mathf.Cos(theta);
        float z = Mathf.Sin(theta) * Mathf.Sin(phi);

        return new Vector3(x, y, z).normalized;
    }

    private Vector3 ResolveRingDirection(Vector3 gazeDir, bool isGazing)
    {
        if (gazeDir.sqrMagnitude < 0.000001f)
            gazeDir = Vector3.up;

        if (!initializedDir)
        {
            smoothRingDir = gazeDir.normalized;
            initializedDir = true;
        }

        TickModeState(isGazing);

        Vector3 targetDir = gazeDir;

        if (currentMode == RingMode.Chase)
            targetDir = BuildChaseDirection(gazeDir);

        float t = 1f - Mathf.Exp(-directionSmoothSpeed * Time.deltaTime);
        smoothRingDir = Vector3.Slerp(smoothRingDir, targetDir.normalized, t).normalized;

        return smoothRingDir;
    }

    private void TickModeState(bool isGazing)
    {
        if (!enableChaseMode || !isGazing)
        {
            currentMode = RingMode.Normal;
            chaseTimer = 0f;
            chaseFlipHorizontal = false;
            chaseFlipVertical = false;
            return;
        }

        if (currentMode == RingMode.Chase)
        {
            chaseTimer -= Time.deltaTime;

            if (chaseTimer <= 0f)
            {
                currentMode = RingMode.Normal;
                chaseFlipHorizontal = false;
                chaseFlipVertical = false;
            }

            return;
        }

        float chancePerSecond = chaseTriggerChancePerSecond;

        if (director != null && director.HasCatalogue)
            chancePerSecond = strategyMaxChaseChance * Mathf.Clamp01(director.Current.evasion);

        float trigger = chancePerSecond * Time.deltaTime;
        if (Random.value < trigger)
            EnterChaseMode();
    }

    private void EnterChaseMode()
    {
        currentMode = RingMode.Chase;
        chaseTimer = Random.Range(chaseDurationRange.x, chaseDurationRange.y);

        int pattern = PickFlipPattern();
        chaseFlipHorizontal = (pattern == 0 || pattern == 2);
        chaseFlipVertical = (pattern == 1 || pattern == 2);
    }

    private int PickFlipPattern()
    {
        int[] patterns = new int[3];
        int count = 0;

        if (allowFlipHorizontal) patterns[count++] = 0;
        if (allowFlipVertical) patterns[count++] = 1;
        if (allowFlipBoth) patterns[count++] = 2;

        if (count == 0)
            return 0;

        return patterns[Random.Range(0, count)];
    }

    private Vector3 BuildChaseDirection(Vector3 gazeDir)
    {
        Vector3 d = gazeDir.normalized;

        if (chaseFlipHorizontal)
        {
            d.x *= -1f;
            d.z *= -1f;
        }

        if (chaseFlipVertical)
        {
            d.y *= -1f;
        }

        if (d.sqrMagnitude < 0.000001f)
            d = Vector3.up;

        return d.normalized;
    }
}