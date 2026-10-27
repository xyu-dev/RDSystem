using System;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// The greedy algorithm. This is the part of the piece that behaves like a feed.
///
/// It runs an epsilon-greedy multi-armed bandit over <see cref="RDStrategyCatalogue"/>. That is not
/// a metaphor for what platforms do, it is the same family of algorithm used to allocate content
/// and rank feeds: try an option, measure how much attention it captured, update that option's
/// estimated value, mostly pick the best-known option, occasionally gamble on another.
///
/// Three properties follow, and they are the argument of the work:
///
///   ACTIVE      - it does not wait to be configured. It acts on the room to find out what works,
///                 then acts again on what it learned.
///   RELATIONAL  - its behaviour is not a property of the code but of the encounter. The same
///                 build in two galleries becomes two different machines.
///   GENERATIVE  - the strategy sequence is never authored and never repeats.
///
/// And one more, which is the uncomfortable one:
///
///   IT DOES NOT START OVER FOR YOU. Value estimates, visit counts and the exploration rate are
///   accumulated across every visitor and written to disk. A visitor on the last day of the show
///   walks into a machine that has already decided what people like, built from a thousand other
///   people's eyes. Exploration has decayed by then, so it is less willing to find out whether
///   they are different. They have to fit the system, rather than the system fitting them.
///
/// That is the prediction imperative, staged at gallery scale. The machine becomes intelligent in
/// the active, relational, generative sense - and simultaneously demonstrates how impoverished
/// that intelligence is when it optimises a single scalar.
/// </summary>
public class RDGreedyDirector : MonoBehaviour
{
    [Serializable]
    private class BanditRecord
    {
        public float[] values = Array.Empty<float>();
        public int[] plays = Array.Empty<int>();
        public int totalImpressions;
        public string lastSavedUtc;
    }

    [Header("References")]
    [SerializeField] private RDStrategyCatalogue catalogue;
    [SerializeField] private RDBehaviorStateLayer stateLayer;

    [Tooltip("Optional. When assigned, impressions only accrue while a visitor is present, so an " +
             "empty room does not teach the machine that everything is boring.")]
    [SerializeField] private RDPresenceTracker presenceTracker;

    [Header("Impression Window")]
    [Tooltip("Seconds a strategy is held before it is scored, when the machine is still naive.")]
    [SerializeField] private float startWindowSeconds = 45f;

    [Tooltip("Seconds a strategy is held once the machine is confident. This is the acceleration: " +
             "a practised feed changes what it shows you faster, not slower.")]
    [SerializeField] private float endWindowSeconds = 9f;

    [Header("Exploration")]
    [Tooltip("Probability of ignoring the best-known strategy and gambling, at the very start.")]
    [Range(0f, 1f)] [SerializeField] private float startEpsilon = 0.85f;

    [Tooltip("Exploration never falls below this, so the machine is never completely closed.")]
    [Range(0f, 1f)] [SerializeField] private float floorEpsilon = 0.04f;

    [Tooltip("Impressions over which exploration decays to roughly a third. Lower = converges " +
             "faster = becomes set in its ways sooner.")]
    [SerializeField] private float epsilonDecayImpressions = 120f;

    [Header("Learning")]
    [Tooltip("How strongly one impression moves a strategy's estimated value.")]
    [Range(0.01f, 1f)] [SerializeField] private float learningRate = 0.25f;

    [Header("Reward - what counts as success")]
    [Tooltip("Weight of raw dwell: what fraction of the window the visitor spent looking at all.")]
    [Range(0f, 1f)] [SerializeField] private float dwellWeight = 0.5f;

    [Tooltip("Weight of sustained attention, not merely pointing at it.")]
    [Range(0f, 1f)] [SerializeField] private float attentionWeight = 0.35f;

    [Tooltip("Weight of coming back after looking away - the return, which platforms prize most.")]
    [Range(0f, 1f)] [SerializeField] private float returnWeight = 0.15f;

    [SerializeField] private int returnsForFullCredit = 3;

    [Header("Persistence")]
    [SerializeField] private bool rememberAcrossRuns = true;
    [SerializeField] private string saveFileName = "rd_strategy_memory.json";
    [SerializeField] private float autoSaveInterval = 30f;

    [Header("Read-only")]
    [SerializeField] private string debugCurrent = "";
    [SerializeField] private string debugLastChoiceWasExploration = "";
    [SerializeField] private float debugEpsilon;
    [SerializeField] private float debugWindowSeconds;
    [SerializeField] private float debugWindowProgress;
    [SerializeField] private int debugTotalImpressions;
    [SerializeField] private float debugLastReward;

    /// <summary>The strategy currently being imposed on the room.</summary>
    public RDStrategy Current { get; private set; }

    public int CurrentIndex { get; private set; }
    public string CurrentLabel => catalogue != null ? catalogue.LabelOf(CurrentIndex) : "";
    public float Epsilon => debugEpsilon;
    public int TotalImpressions => record.totalImpressions;
    public float LastReward => debugLastReward;
    public bool HasCatalogue => catalogue != null && catalogue.Count > 0;

    private BanditRecord record = new BanditRecord();
    private System.Random rng;
    private string savePath;
    private float saveTimer;

    // current impression
    private float windowElapsed;
    private float gazeSeconds;
    private float attentionIntegral;
    private int returnsThisWindow;
    private bool wasGazingLastFrame;

    private void Awake()
    {
        savePath = Path.Combine(Application.persistentDataPath, saveFileName);
        rng = new System.Random(Environment.TickCount);

        if (catalogue == null || catalogue.Count == 0)
        {
            Debug.LogError("[RDGreedyDirector] No strategy catalogue assigned; the director will do nothing.");
            enabled = false;
            return;
        }

        if (rememberAcrossRuns) Load();
        EnsureRecordSize();

        CurrentIndex = PickStrategy(out bool explored);
        Current = catalogue.Get(CurrentIndex);
        debugLastChoiceWasExploration = explored ? "explored" : "exploited";

        BeginImpression();
    }

    private void Update()
    {
        if (stateLayer == null || catalogue == null) return;

        float dt = Time.deltaTime;
        RDBehaviorState state = stateLayer.Current;

        // An empty room is not evidence that the current strategy is bad, so it buys no impression.
        if (!SomeoneIsHere(state))
        {
            wasGazingLastFrame = state.isGazing;
            RefreshDebug();
            return;
        }

        windowElapsed += dt;

        if (state.isGazing)
        {
            gazeSeconds += dt;
            if (!wasGazingLastFrame) returnsThisWindow++;
        }

        attentionIntegral += state.attention * dt;
        wasGazingLastFrame = state.isGazing;

        float window = CurrentWindowSeconds();

        if (windowElapsed >= window)
            CloseImpression(window);

        RefreshDebug();

        if (rememberAcrossRuns)
        {
            saveTimer += dt;
            if (saveTimer >= Mathf.Max(5f, autoSaveInterval))
            {
                saveTimer = 0f;
                Save();
            }
        }
    }

    private bool SomeoneIsHere(RDBehaviorState state)
    {
        if (presenceTracker != null)
            return presenceTracker.State == RDPresenceTracker.PresenceState.Present;

        return state.isGazing || state.noGazeDuration < 5f;
    }

    // ------------------------------------------------------------------ the bandit

    /// <summary>
    /// Score the impression that just ended, fold it into the strategy's value, and choose again.
    /// </summary>
    private void CloseImpression(float window)
    {
        float reward = ScoreImpression(window);

        // Incremental update with a fixed rate: recent evidence outweighs old evidence, which is
        // what a production system does too - taste is assumed to drift.
        record.values[CurrentIndex] += learningRate * (reward - record.values[CurrentIndex]);
        record.plays[CurrentIndex]++;
        record.totalImpressions++;

        debugLastReward = reward;

        CurrentIndex = PickStrategy(out bool explored);
        Current = catalogue.Get(CurrentIndex);
        debugLastChoiceWasExploration = explored ? "explored" : "exploited";

        BeginImpression();
    }

    /// <summary>
    /// The engagement metric. Everything the machine values about a person is in these three terms,
    /// and nothing else about them survives.
    /// </summary>
    private float ScoreImpression(float window)
    {
        float safeWindow = Mathf.Max(0.1f, window);

        float dwell = Mathf.Clamp01(gazeSeconds / safeWindow);
        float attention = Mathf.Clamp01(attentionIntegral / safeWindow);
        float returns = Mathf.Clamp01(returnsThisWindow / (float)Mathf.Max(1, returnsForFullCredit));

        float total = dwellWeight + attentionWeight + returnWeight;
        if (total <= 0.0001f) return 0f;

        return Mathf.Clamp01(
            (dwell * dwellWeight + attention * attentionWeight + returns * returnWeight) / total);
    }

    private void BeginImpression()
    {
        windowElapsed = 0f;
        gazeSeconds = 0f;
        attentionIntegral = 0f;
        returnsThisWindow = 0;
    }

    /// <summary>
    /// Epsilon-greedy: mostly take the best-known option, occasionally gamble. Any strategy that
    /// has never been tried is taken first, so the machine at least meets every option once.
    /// </summary>
    private int PickStrategy(out bool explored)
    {
        int count = catalogue.Count;

        for (int i = 0; i < count; i++)
        {
            if (record.plays[i] == 0)
            {
                explored = true;
                return i;
            }
        }

        if (rng.NextDouble() < CurrentEpsilon())
        {
            explored = true;

            // Gamble on something other than the incumbent.
            int pick = rng.Next(count - 1);
            if (pick >= CurrentIndex) pick++;
            return pick;
        }

        explored = false;

        int best = 0;
        float bestValue = float.MinValue;

        for (int i = 0; i < count; i++)
        {
            if (record.values[i] > bestValue)
            {
                bestValue = record.values[i];
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Exploration decays with every impression the machine has ever run, across all visitors and
    /// across power cycles. This is the mechanism by which it stops being curious about you.
    /// </summary>
    private float CurrentEpsilon()
    {
        float decay = Mathf.Exp(-record.totalImpressions / Mathf.Max(1f, epsilonDecayImpressions));
        return Mathf.Max(floorEpsilon, startEpsilon * decay);
    }

    /// <summary>
    /// And the window shortens on the same clock. A practised feed does not slow down.
    /// </summary>
    private float CurrentWindowSeconds()
    {
        float t = 1f - Mathf.Exp(-record.totalImpressions / Mathf.Max(1f, epsilonDecayImpressions));
        return Mathf.Lerp(startWindowSeconds, endWindowSeconds, Mathf.Clamp01(t));
    }

    // ------------------------------------------------------------------ operator

    /// <summary>Forgets everything it learned. The machine meets the next visitor naive.</summary>
    public void ClearMemory()
    {
        record = new BanditRecord();
        EnsureRecordSize();

        CurrentIndex = PickStrategy(out _);
        Current = catalogue.Get(CurrentIndex);
        BeginImpression();

        if (rememberAcrossRuns) Save();

        Debug.Log("[RDGreedyDirector] Strategy memory cleared.");
    }

    /// <summary>One line per strategy, for the operator overlay and the daily log.</summary>
    public string DescribeValues()
    {
        if (catalogue == null || catalogue.Count == 0) return "";

        StringBuilder sb = new StringBuilder();

        for (int i = 0; i < catalogue.Count; i++)
        {
            sb.Append(i == CurrentIndex ? "> " : "  ");
            sb.Append(catalogue.LabelOf(i).PadRight(12));
            sb.Append(record.values[i].ToString("0.000"));
            sb.Append("  x");
            sb.Append(record.plays[i]);
            if (i < catalogue.Count - 1) sb.Append('\n');
        }

        return sb.ToString();
    }

    private void RefreshDebug()
    {
        debugCurrent = CurrentLabel;
        debugEpsilon = CurrentEpsilon();
        debugWindowSeconds = CurrentWindowSeconds();
        debugWindowProgress = Mathf.Clamp01(windowElapsed / Mathf.Max(0.1f, debugWindowSeconds));
        debugTotalImpressions = record.totalImpressions;
    }

    /// <summary>
    /// Forgets everything learned, in memory and on disk.
    ///
    /// Needed because the values are not just stale when the sessions that produced them were
    /// broken - they are wrong in a way that cannot be learned out quickly. Epsilon decays with
    /// impressions, so a run that accumulated thousands of false ones is left barely exploring
    /// at all: the machine stops trying alternatives precisely when its estimates are worst, and
    /// sits on whichever strategy happened to win during the noise. Clearing is the only cheap
    /// way back.
    /// </summary>
    public void ResetMemory()
    {
        record = new BanditRecord();

        if (catalogue != null) EnsureRecordSize();

        debugTotalImpressions = 0;
        debugLastReward = 0f;

        try
        {
            if (!string.IsNullOrEmpty(savePath) && File.Exists(savePath))
                File.Delete(savePath);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[RDGreedyDirector] Could not delete " + savePath + ": " + e.Message);
        }

        Debug.Log("[RDGreedyDirector] Strategy memory cleared; exploration starts again from " +
                  "startEpsilon.");
    }

    // ------------------------------------------------------------------ persistence

    private void EnsureRecordSize()
    {
        int count = catalogue.Count;

        if (record.values == null || record.values.Length != count)
        {
            float[] resized = new float[count];
            if (record.values != null)
                Array.Copy(record.values, resized, Mathf.Min(record.values.Length, count));
            record.values = resized;
        }

        if (record.plays == null || record.plays.Length != count)
        {
            int[] resized = new int[count];
            if (record.plays != null)
                Array.Copy(record.plays, resized, Mathf.Min(record.plays.Length, count));
            record.plays = resized;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(savePath)) return;

            BanditRecord loaded = JsonUtility.FromJson<BanditRecord>(File.ReadAllText(savePath));
            if (loaded != null) record = loaded;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[RDGreedyDirector] Could not load strategy memory, starting naive. " + e.Message);
        }
    }

    private void Save()
    {
        try
        {
            record.lastSavedUtc = DateTime.UtcNow.ToString("o");
            File.WriteAllText(savePath, JsonUtility.ToJson(record));
        }
        catch (Exception e)
        {
            Debug.LogWarning("[RDGreedyDirector] Could not save strategy memory. " + e.Message);
        }
    }

    private void OnApplicationQuit()
    {
        if (rememberAcrossRuns) Save();
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused && rememberAcrossRuns) Save();
    }
}
