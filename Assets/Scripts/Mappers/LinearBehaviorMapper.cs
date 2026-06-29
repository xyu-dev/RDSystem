using UnityEngine;

public class LinearBehaviorMapper : MonoBehaviour, IRDParameterMapper
{
    [System.Serializable]
    private struct PearsonClassDef
    {
        public string label;
        public Vector2 fk;
        public float idleWeight;
        public float gazeWeight;
        public float brownRadiusMul;
    }

    [SerializeField] private RDMappingConfig config;
    [SerializeField] private MouseInput mouseInput;

    [Header("Pearson Safe Classes")]
    [SerializeField] private PearsonClassDef[] classes = new PearsonClassDef[]
    {
        new PearsonClassDef { label = "epsilon", fk = new Vector2(0.022f, 0.059f), idleWeight = 0.9f,  gazeWeight = 1.1f, brownRadiusMul = 1.00f },
        new PearsonClassDef { label = "zeta",    fk = new Vector2(0.026f, 0.059f), idleWeight = 1.1f,  gazeWeight = 1.0f, brownRadiusMul = 0.95f },
        new PearsonClassDef { label = "lambda",  fk = new Vector2(0.026f, 0.061f), idleWeight = 1.3f,  gazeWeight = 1.0f, brownRadiusMul = 0.90f },
        new PearsonClassDef { label = "delta",   fk = new Vector2(0.030f, 0.055f), idleWeight = 1.15f, gazeWeight = 1.0f, brownRadiusMul = 0.95f },
        new PearsonClassDef { label = "theta",   fk = new Vector2(0.030f, 0.057f), idleWeight = 1.25f, gazeWeight = 1.05f, brownRadiusMul = 1.00f },
        new PearsonClassDef { label = "kappa",   fk = new Vector2(0.050f, 0.063f), idleWeight = 0.75f, gazeWeight = 1.25f, brownRadiusMul = 1.15f },
        new PearsonClassDef { label = "pi",      fk = new Vector2(0.062f, 0.061f), idleWeight = 0.60f, gazeWeight = 1.35f, brownRadiusMul = 1.20f },
    };

    [Header("Inter-Class Motion")]
    [SerializeField] private float idleClassInterval = 8f;
    [SerializeField] private float gazeClassInterval = 2.8f;
    [SerializeField] private float classBlendSpeed = 1.15f;
    [SerializeField] private float switchImpulseSeconds = 0.8f;

    [Header("Brownian Motion")]
    [SerializeField] private float idleBrownRadius = 0.00025f;
    [SerializeField] private float gazeBrownRadius = 0.0018f;
    [SerializeField] private float brownAccel = 0.8f;
    [SerializeField] private float brownDamping = 0.92f;
    [SerializeField] private float headMotionBoost = 1.1f;
    [SerializeField] private float switchBoost = 0.7f;
    [SerializeField] private float eyeJumpBoost = 1.8f;

    [Header("Idle Micro Drift")]
    [SerializeField] private float idleMicroAmp = 0.00008f;
    [SerializeField] private float idleMicroSpeed = 0.16f;
    [SerializeField] private float gazeMicroAmp = 0.00022f;
    [SerializeField] private float gazeMicroSpeed = 0.34f;

    [Header("Behavior Bias")]
    [SerializeField] private float agitationFeedBias = 0.0016f;
    [SerializeField] private float agitationKillBias = 0.0010f;
    [SerializeField] private float noveltyFeedBias = 0.0014f;
    [SerializeField] private float noveltyKillBias = 0.0012f;
    [SerializeField] private float eyeJumpFeedKick = 0.0020f;
    [SerializeField] private float eyeJumpKillKick = 0.0016f;
    [SerializeField] private float headMotionFeedBias = 0.0010f;
    [SerializeField] private float headMotionKillBias = 0.0008f;

    [Header("Safety Bounds")]
    [SerializeField] private Vector2 feedBounds = new Vector2(0.021f, 0.063f);
    [SerializeField] private Vector2 killBounds = new Vector2(0.054f, 0.0645f);
    [SerializeField] private float boundRepelMargin = 0.0012f;
    [SerializeField] private float boundRepelStrength = 0.35f;

    [Header("Output Smoothing")]
    [SerializeField] private float outputLerpSpeed = 3.0f;

    private int fromClass = 2;
    private int toClass = 4;
    private float classBlend = 0f;
    private float classTimer = 0f;

    private Vector2 brownOffset;
    private Vector2 brownVelocity;

    private float smoothFeed;
    private float smoothKill;
    private float localTime;
    private bool initialized;

    private int lastSwitchCount;
    private float eyeJumpImpulse;

    void Reset()
    {
        if (mouseInput == null)
            mouseInput = GetComponent<MouseInput>();
    }

    void Awake()
    {
        InitIfNeeded();
    }

    private void InitIfNeeded()
    {
        if (initialized) return;

        Vector2 start = (classes != null && classes.Length > 0)
            ? classes[Mathf.Clamp(fromClass, 0, classes.Length - 1)].fk
            : new Vector2(config.idleFeed, config.idleKill);

        smoothFeed = start.x;
        smoothKill = start.y;
        initialized = true;
    }

    public RDTarget Evaluate(RDBehaviorState state)
    {
        InitIfNeeded();

        float dt = Mathf.Max(Time.deltaTime, 0.000001f);
        localTime += dt;

        TickBehaviorImpulses(state);
        TickClassTransition(state, dt);
        TickBrownian(state, dt);

        RDTarget t = new RDTarget();

        t.clickPos = mouseInput != null ? mouseInput.CurrentUV : Vector2.zero;
        t.clickRadius = config != null ? config.idleRadius : 0.05f;

        if (state.isGazing)
        {
            t.clickState = 1f;
            t.resetBlend = 0f;
        }
        else
        {
            t.clickState = 0f;

            float delayedTime = Mathf.Max(0f, state.noGazeDuration - config.resetEnterDelay);
            float reset01 = Mathf.Clamp01(delayedTime / Mathf.Max(0.0001f, config.resetReturnTime));
            t.resetBlend = config.resetTarget * reset01;
        }

        Vector2 fk = ComposeFeedKill(state, dt);

        t.feed = Mathf.Clamp(fk.x, feedBounds.x, feedBounds.y);
        t.kill = Mathf.Clamp(fk.y, killBounds.x, killBounds.y);

        return t;
    }

    private void TickBehaviorImpulses(RDBehaviorState state)
    {
        if (state.switchCount != lastSwitchCount)
        {
            int delta = Mathf.Abs(state.switchCount - lastSwitchCount);
            classTimer += switchImpulseSeconds * Mathf.Clamp(delta, 1, 3);
            lastSwitchCount = state.switchCount;
        }

        if (state.eyeJump)
            eyeJumpImpulse = 1f;

        eyeJumpImpulse = Mathf.MoveTowards(eyeJumpImpulse, 0f, Time.deltaTime * 2.4f);
    }

    private void TickClassTransition(RDBehaviorState state, float dt)
    {
        float switch01 = Mathf.Clamp01(state.switchCount / 6f);

        float interval = state.isGazing
            ? Mathf.Lerp(gazeClassInterval, gazeClassInterval * 0.45f, state.attention)
            : Mathf.Lerp(idleClassInterval * 0.70f, idleClassInterval, Mathf.Clamp01(state.noGazeDuration * 0.25f));

        interval *= Mathf.Lerp(1f, 0.7f, switch01);

        classTimer += dt;
        classBlend += dt * classBlendSpeed / Mathf.Max(0.1f, interval);
        classBlend = Mathf.Clamp01(classBlend);

        if (classTimer >= interval)
        {
            classTimer = 0f;
            fromClass = toClass;
            toClass = PickNextClass(fromClass, state);
            classBlend = 0f;
        }
    }

    private int PickNextClass(int from, RDBehaviorState state)
    {
        if (classes == null || classes.Length <= 1)
            return from;

        float total = 0f;
        float[] weights = new float[classes.Length];
        float switch01 = Mathf.Clamp01(state.switchCount / 6f);

        for (int i = 0; i < classes.Length; i++)
        {
            if (i == from) continue;

            float dist = Vector2.Distance(classes[from].fk, classes[i].fk);
            float w = 1f / (dist * 80f + 0.5f);

            w *= state.isGazing ? classes[i].gazeWeight : classes[i].idleWeight;

            if (state.isGazing)
            {
                w *= 1f + state.attention * 0.5f;
                w *= 1f + state.agitation * 0.35f;
                w *= 1f + state.novelty * 0.25f;
            }

            w *= 1f + switch01 * (i >= 4 ? 0.45f : 0.15f);
            w *= 1f + Mathf.Clamp01(state.headMotion) * (i >= 5 ? 0.35f : 0.12f);

            if (state.eyeJump)
                w *= (i >= 5 ? 1.35f : 1.08f);

            w += Random.Range(0f, 0.06f);

            weights[i] = w;
            total += w;
        }

        float r = Random.Range(0f, total);
        float acc = 0f;
        for (int i = 0; i < classes.Length; i++)
        {
            acc += weights[i];
            if (r <= acc) return i;
        }

        return (from + 1) % classes.Length;
    }

    private void TickBrownian(RDBehaviorState state, float dt)
    {
        float switch01 = Mathf.Clamp01(state.switchCount / 6f);
        float motion01 = Mathf.Clamp01(state.headMotion);

        float radius = state.isGazing
            ? Mathf.Lerp(idleBrownRadius, gazeBrownRadius, state.attention)
            : idleBrownRadius;

        float classRadiusMul = (classes != null && classes.Length > 0)
            ? classes[Mathf.Clamp(toClass, 0, classes.Length - 1)].brownRadiusMul
            : 1f;

        radius *= classRadiusMul;
        radius *= 1f + motion01 * 0.45f;
        radius *= 1f + switch01 * 0.25f;
        radius *= 1f + eyeJumpImpulse * 0.35f;

        float accelScale = brownAccel;

        if (state.isGazing)
        {
            accelScale *= 1f + state.agitation * 0.8f;
            accelScale *= 1f + state.novelty * 0.5f;
        }
        else
        {
            accelScale *= 0.22f;
        }

        accelScale *= 1f + motion01 * headMotionBoost;
        accelScale *= 1f + switch01 * switchBoost;
        accelScale *= 1f + eyeJumpImpulse * eyeJumpBoost;

        brownVelocity += Random.insideUnitCircle * accelScale * dt;
        brownVelocity *= Mathf.Pow(brownDamping, dt * 60f);
        brownOffset += brownVelocity * dt;

        if (brownOffset.magnitude > radius)
        {
            float over = brownOffset.magnitude - radius;
            Vector2 n = brownOffset.normalized;
            brownOffset -= n * over * 0.65f;
            brownVelocity -= Vector2.Dot(brownVelocity, n) * n * 1.15f;
        }
    }

    private Vector2 ComposeFeedKill(RDBehaviorState state, float dt)
    {
        Vector2 center = Vector2.Lerp(
            classes[fromClass].fk,
            classes[toClass].fk,
            Smooth01(classBlend)
        );

        Vector2 target = center + brownOffset;

        float microAmp = state.isGazing ? gazeMicroAmp : idleMicroAmp;
        float microSpeed = state.isGazing ? gazeMicroSpeed : idleMicroSpeed;

        target.x += Mathf.Sin(localTime * microSpeed * 1.7f + 1.2f) * microAmp;
        target.y += Mathf.Cos(localTime * microSpeed * 1.3f + 0.8f) * microAmp * 0.85f;

        float motion01 = Mathf.Clamp01(state.headMotion);
        float switch01 = Mathf.Clamp01(state.switchCount / 6f);

        if (state.isGazing)
        {
            target.x += state.agitation * agitationFeedBias;
            target.y += state.agitation * agitationKillBias;

            target.x += (state.novelty - 0.5f) * noveltyFeedBias;
            target.y += (state.novelty - 0.5f) * noveltyKillBias;
        }

        target.x += motion01 * headMotionFeedBias * (state.isGazing ? 1f : 0.4f);
        target.y += motion01 * headMotionKillBias * (state.isGazing ? 1f : 0.4f);

        target.x += switch01 * 0.0009f;
        target.y += switch01 * 0.0007f;

        if (eyeJumpImpulse > 0f)
        {
            float signX = Mathf.Sign(Mathf.Sin(localTime * 8.7f + 0.3f));
            float signY = Mathf.Sign(Mathf.Cos(localTime * 7.9f + 1.1f));

            target.x += signX * eyeJumpFeedKick * eyeJumpImpulse;
            target.y += signY * eyeJumpKillKick * eyeJumpImpulse;
        }

        target = RepelFromBounds(target);

        float k = 1f - Mathf.Exp(-outputLerpSpeed * dt);
        smoothFeed = Mathf.Lerp(smoothFeed, target.x, k);
        smoothKill = Mathf.Lerp(smoothKill, target.y, k);

        return new Vector2(smoothFeed, smoothKill);
    }

    private Vector2 RepelFromBounds(Vector2 fk)
    {
        float left = fk.x - feedBounds.x;
        float right = feedBounds.y - fk.x;
        float bottom = fk.y - killBounds.x;
        float top = killBounds.y - fk.y;

        if (left < boundRepelMargin) fk.x += (boundRepelMargin - left) * boundRepelStrength;
        if (right < boundRepelMargin) fk.x -= (boundRepelMargin - right) * boundRepelStrength;
        if (bottom < boundRepelMargin) fk.y += (boundRepelMargin - bottom) * boundRepelStrength;
        if (top < boundRepelMargin) fk.y -= (boundRepelMargin - top) * boundRepelStrength;

        fk.x = Mathf.Clamp(fk.x, feedBounds.x, feedBounds.y);
        fk.y = Mathf.Clamp(fk.y, killBounds.x, killBounds.y);

        return fk;
    }

    private float Smooth01(float t)
    {
        return t * t * (3f - 2f * t);
    }
}