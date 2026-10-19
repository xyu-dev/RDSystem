using System;
using System.IO;
using UnityEngine;

/// <summary>
/// What the machine keeps after you leave.
///
/// The short-term reading of a visitor (attention, agitation, switching) is wiped between
/// visitors by <see cref="RDPresenceTracker"/>. This does the opposite: it accumulates, all week,
/// across every visitor, and it is written to disk so a power cycle does not give the piece
/// amnesia. Familiarity and fatigue bias the behaviour mapper, so the room a visitor on Sunday
/// meets is measurably not the room that opened on Monday.
/// </summary>
public class RDLongTermMemory : MonoBehaviour
{
    [Serializable]
    private class MemoryRecord
    {
        public double totalGazeSeconds;
        public int visitorCount;
        public double fatigueCharge;
        public string lastSavedUtc;
    }

    public enum MemoryPolicy
    {
        /// <summary>Every visitor meets a naive machine. Nothing carries over.</summary>
        Forget,

        /// <summary>The machine accumulates across visitors but never writes to disk.</summary>
        RememberThisRun,

        /// <summary>The machine accumulates across visitors and across power cycles. Exhibition default.</summary>
        RememberAcrossRuns
    }

    [Header("Policy")]
    [SerializeField] private MemoryPolicy policy = MemoryPolicy.RememberAcrossRuns;
    [SerializeField] private string saveFileName = "rd_longterm_memory.json";

    [Header("Familiarity")]
    [Tooltip("Total accumulated gaze, in seconds, at which familiarity reaches ~0.63.")]
    [SerializeField] private float familiarityTimeConstant = 7200f;

    [Header("Fatigue")]
    [Tooltip("Fatigue gained per second of being looked at.")]
    [SerializeField] private float fatiguePerGazeSecond = 0.00035f;

    [Tooltip("Fatigue recovered per second while nobody is looking.")]
    [SerializeField] private float fatigueRecoveryPerSecond = 0.00012f;

    [Header("Persistence")]
    [Tooltip("How often the record is flushed to disk, in seconds.")]
    [SerializeField] private float autoSaveInterval = 30f;

    [Header("Read-only")]
    [SerializeField] private double debugTotalGazeSeconds;
    [SerializeField] private int debugVisitorCount;
    [SerializeField] private float debugFamiliarity;
    [SerializeField] private float debugFatigue;

    private MemoryRecord record = new MemoryRecord();
    private float saveTimer;
    private string savePath;

    /// <summary>0..1. How much gaze this machine has absorbed over its whole life.</summary>
    public float Familiarity { get; private set; }

    /// <summary>0..1. How worn out it is right now.</summary>
    public float Fatigue { get; private set; }

    public double TotalGazeSeconds => record.totalGazeSeconds;
    public int VisitorCount => record.visitorCount;

    private void Awake()
    {
        savePath = Path.Combine(Application.persistentDataPath, saveFileName);

        if (policy == MemoryPolicy.RememberAcrossRuns)
            Load();

        Recompute();
    }

    private void Update()
    {
        if (policy == MemoryPolicy.Forget) return;
        if (policy != MemoryPolicy.RememberAcrossRuns) return;

        saveTimer += Time.deltaTime;
        if (saveTimer >= Mathf.Max(1f, autoSaveInterval))
        {
            saveTimer = 0f;
            Save();
        }
    }

    /// <summary>Called every frame by the presence tracker with this frame's gaze time.</summary>
    public void AccumulateGaze(float seconds)
    {
        if (policy == MemoryPolicy.Forget || seconds <= 0f) return;

        record.totalGazeSeconds += seconds;
        record.fatigueCharge = Math.Min(1.0, record.fatigueCharge + seconds * fatiguePerGazeSecond);

        Recompute();
    }

    /// <summary>Called every frame by the presence tracker while the room is empty.</summary>
    public void Rest(float seconds)
    {
        if (policy == MemoryPolicy.Forget || seconds <= 0f) return;

        record.fatigueCharge = Math.Max(0.0, record.fatigueCharge - seconds * fatigueRecoveryPerSecond);
        Recompute();
    }

    public void RegisterVisitor()
    {
        if (policy == MemoryPolicy.Forget) return;

        record.visitorCount++;
        Recompute();
    }

    /// <summary>Wipes everything. For the operator, between exhibition days if desired.</summary>
    public void ClearMemory()
    {
        record = new MemoryRecord();
        Recompute();

        if (policy == MemoryPolicy.RememberAcrossRuns)
            Save();
    }

    private void Recompute()
    {
        Familiarity = 1f - Mathf.Exp(-(float)record.totalGazeSeconds / Mathf.Max(1f, familiarityTimeConstant));
        Fatigue = Mathf.Clamp01((float)record.fatigueCharge);

        debugTotalGazeSeconds = record.totalGazeSeconds;
        debugVisitorCount = record.visitorCount;
        debugFamiliarity = Familiarity;
        debugFatigue = Fatigue;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(savePath)) return;

            string json = File.ReadAllText(savePath);
            MemoryRecord loaded = JsonUtility.FromJson<MemoryRecord>(json);

            if (loaded != null)
                record = loaded;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[RDLongTermMemory] Could not load memory, starting naive. " + e.Message);
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
            Debug.LogWarning("[RDLongTermMemory] Could not save memory. " + e.Message);
        }
    }

    private void OnApplicationQuit()
    {
        if (policy == MemoryPolicy.RememberAcrossRuns)
            Save();
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused && policy == MemoryPolicy.RememberAcrossRuns)
            Save();
    }
}
