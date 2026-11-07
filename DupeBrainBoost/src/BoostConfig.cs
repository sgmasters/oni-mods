using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using PeterHan.PLib.Options;

namespace DupeBrainBoost
{
    /// <summary>Settings, stored as config.json next to the mod DLL.</summary>
    public enum CpuProfileSetting
    {
        [Option("Auto", "Pick from the number of CPU cores")] Auto,
        [Option("Single (1 core)", "Stay close to the base game")] Single,
        [Option("Dual (2 cores)", "")] Dual,
        [Option("Quad (3-4 cores)", "")] Quad,
        [Option("Hexa (5-8 cores)", "")] Hexa,
        [Option("Many (9+ cores)", "")] Many,
        [Option("Custom", "Use the numbers set in the other options")] Custom,
    }

    public enum FastTrackSetting
    {
        [Option("Auto", "Detect whether Fast Track is enabled")] Auto,
        [Option("Assume on", "Behave as if Fast Track is enabled")] Assume,
        [Option("Ignore", "Never adjust for Fast Track")] Ignore,
    }

    [JsonObject(MemberSerialization.OptIn)]
    [ConfigFile("config.json", true)]
    [RestartRequired]
    public sealed class BoostConfig
    {
        public static BoostConfig Instance { get; private set; } = new BoostConfig();

        // ---- CPU profile ----

        /// <summary>
        /// How hard to use the CPU. "Auto" picks from the number of logical cores:
        ///   Single (1 core) / Dual (2) / Quad (3-4) / Hexa (5-8) / Many (9+).
        /// You can also name a profile directly, or use "Custom" to use the raw numbers below
        /// (PathProberThreads, PathProbesPerFrame, MaxMillisecondsPerFrame, MaxBrainsPerFrame,
        /// AsyncCritterPathProbes, FastTrackMaxDecisionsPerFrame) exactly as written.
        /// </summary>
        [Option("CPU profile", "How hard to use the CPU. Auto picks from your core count; Custom uses the numbers set below.", "1. CPU")]
        [JsonProperty, JsonConverter(typeof(StringEnumConverter))] public CpuProfileSetting CpuProfile { get; set; } = CpuProfileSetting.Auto;

        // ---- Errand decisions (BrainScheduler) ----

        [Option("Smart errand scheduler", "Lets many dupes re-think errands per frame within a time budget; dupes in danger go first.", "2. Errand decisions")]
        [JsonProperty] public bool EnableBrainScheduler { get; set; } = true;

        /// <summary>Aim: every dupe re-thinks its errand at least this often (game seconds).</summary>
        [Option("Re-think interval (game s)", "Every dupe re-thinks its errand at least this often.", "2. Errand decisions", Format = "F2")]
        [Limit(0.1, 30)]
        [JsonProperty] public float TargetIntervalSeconds { get; set; } = 1.0f;

        /// <summary>Dupes in danger (low breath, scalding) re-think at least this often (game seconds).</summary>
        [Option("Danger re-think interval (game s)", "Dupes with low breath or scalding re-think at least this often.", "2. Errand decisions", Format = "F2")]
        [Limit(0.05, 5)]
        [JsonProperty] public float UrgentIntervalSeconds { get; set; } = 0.25f;

        /// <summary>Share of the frame budget dupes in danger may use (they always get at least one decision).</summary>
        [Option("Danger budget share", "Share of the frame budget dupes in danger may use (0.4 = 40 %).", "2. Errand decisions", Format = "F2")]
        [Limit(0.05, 1)]
        [JsonProperty] public float UrgentBudgetShare { get; set; } = 0.4f;

        /// <summary>Hard CPU budget for dupe decisions per rendered frame (milliseconds).</summary>
        [Option("Budget per frame (ms)", "CPU time for dupe decisions per frame. Only used with CPU profile Custom.", "2. Errand decisions", Format = "F1")]
        [Limit(0.5, 50)]
        [JsonProperty] public float MaxMillisecondsPerFrame { get; set; } = 4f;

        /// <summary>Decisions per frame are clamped to [Min, Max]. Vanilla = 1.</summary>
        [Option("Min decisions per frame", "At least this many dupes decide per frame. Base game = 1.", "2. Errand decisions")]
        [Limit(1, 64)]
        [JsonProperty] public int MinBrainsPerFrame { get; set; } = 1;
        [Option("Max decisions per frame", "At most this many dupes decide per frame. Only used with CPU profile Custom.", "2. Errand decisions")]
        [Limit(1, 256)]
        [JsonProperty] public int MaxBrainsPerFrame { get; set; } = 48;

        /// <summary>A single dupe decision slower than this (ms) counts as expensive...</summary>
        [Option("Slow decision (ms)", "A single decision slower than this counts as expensive...", "2. Errand decisions", Format = "F1")]
        [Limit(1, 100)]
        [JsonProperty] public float ExpensiveDecisionMs { get; set; } = 8f;

        /// <summary>...and that dupe then waits at least this many game seconds before deciding again.</summary>
        [Option("Slow decision cooldown (game s)", "...and that dupe then waits at least this long before deciding again.", "2. Errand decisions", Format = "F1")]
        [Limit(0, 30)]
        [JsonProperty] public float ExpensiveCooldownSeconds { get; set; } = 4f;

        /// <summary>
        /// Idle dupes search the whole reachable map for a "safe spot" on every decision; if none exists
        /// that search is very slow. Re-use the previous answer for this many game seconds while the
        /// dupe stays on the same cell (0 = base game behaviour).
        /// </summary>
        [Option("Safe-spot search reuse (game s)", "Idle dupes on the same cell reuse their last whole-map safe-spot search for this long. 0 = base game.", "2. Errand decisions", Format = "F1")]
        [Limit(0, 30)]
        [JsonProperty] public float SafeSpotSearchThrottleSeconds { get; set; } = 3f;

        /// <summary>
        /// When a dupe decides again from the same cell within this many game seconds, its sensors
        /// (fetch lists, closest food, idle/mingle cell, toilet...) are re-used instead of recomputed.
        /// 0 = base game behaviour. Works with and without Fast Track.
        /// </summary>
        [Option("Sensor reuse (game s)", "Idle / mingle / toilet lookups are reused for this long while the dupe stays on the same cell. 0 = base game.", "2. Errand decisions", Format = "F2")]
        [Limit(0, 10)]
        [JsonProperty] public float SensorThrottleSeconds { get; set; } = 0.75f;

        /// <summary>Breath below this fraction counts as danger.</summary>
        [Option("Low breath", "Breath below this share counts as danger (0.6 = 60 %).", "2. Errand decisions", Format = "F2")]
        [Limit(0, 1)]
        [JsonProperty] public float LowBreathFraction { get; set; } = 0.6f;

        // ---- Path-finding (AsyncPathProber) ----

        [Option("Path-finding boost", "More background path-finding threads and path updates per frame.", "3. Path-finding")]
        [JsonProperty] public bool EnablePathProberBoost { get; set; } = true;

        /// <summary>Background path-finding threads. Vanilla = 1. 0 = auto (CPU cores / 2, max 4).</summary>
        [Option("Path-finding threads", "Background path-finding threads. 0 = auto. Only used with CPU profile Custom.", "3. Path-finding")]
        [Limit(0, 16)]
        [JsonProperty] public int PathProberThreads { get; set; } = 0;

        /// <summary>
        /// Critters normally recompute their whole reachable area ON THE MAIN THREAD every time their
        /// brain updates (5 critters per frame) - flying and swimming critters make this very expensive.
        /// true = critters use the background path-finding threads like dupes and rovers do.
        /// </summary>
        [Option("Critters on background threads", "Critters compute where they can go on background threads instead of the main thread.", "3. Path-finding")]
        [JsonProperty] public bool AsyncCritterPathProbes { get; set; } = true;

        /// <summary>Path grids refreshed per frame. Vanilla = 4.</summary>
        [Option("Path updates per frame", "Path grids refreshed per frame. Base game = 4. Only used with CPU profile Custom.", "3. Path-finding")]
        [Limit(1, 32)]
        [JsonProperty] public int PathProbesPerFrame { get; set; } = 8;

        // ---- Periodic game updates (the once-per-game-second pause) ----

        /// <summary>Spread 200 ms / 1 s / 4 s game updates evenly over their period instead of bursts.</summary>
        [Option("Spread periodic updates", "Spread 200 ms / 1 s / 4 s game updates over their period instead of bursts.", "5. Diagnostics")]
        [JsonProperty] public bool SpreadPeriodicUpdates { get; set; } = false;

        /// <summary>Time periodic updates and log the heaviest ones once a minute.</summary>
        [Option("Profile periodic updates", "Time periodic updates and log the heaviest ones once a minute (costs a little every frame).", "5. Diagnostics")]
        [JsonProperty] public bool ProfilePeriodicUpdates { get; set; } = false;

        // ---- Fast Track compatibility ----

        /// <summary>
        /// "Auto"   = detect if Fast Track is enabled and switch off the parts it already covers,
        /// "Assume" = behave as if Fast Track is enabled, "Ignore" = never adjust.
        /// </summary>
        [Option("Fast Track detection", "Auto: when Fast Track is on, switch off the parts it already covers.", "4. Fast Track")]
        [JsonProperty, JsonConverter(typeof(StringEnumConverter))] public FastTrackSetting FastTrackDetection { get; set; } = FastTrackSetting.Auto;

        /// <summary>
        /// Parts to keep ON even with Fast Track: any of "Scheduler", "PathProber", "CritterAsync",
        /// "Profiler", "Spread". Use at your own risk - these overlap with Fast Track's own patches.
        /// </summary>
        [JsonProperty] public List<string> KeepWithFastTrack { get; set; } = new List<string>();

        [Option("Keep scheduler with Fast Track", "Use this mod's scheduler even with Fast Track (overlaps Fast Track; not recommended).", "4. Fast Track")]
        [JsonProperty] public bool KeepSchedulerWithFastTrack { get; set; }

        [Option("Keep path-finding boost with Fast Track", "Use this mod's path-finding threads even with Fast Track (overlaps Fast Track's path cache).", "4. Fast Track")]
        [JsonProperty] public bool KeepPathProberWithFastTrack { get; set; }

        [Option("Keep critter threads with Fast Track", "Critters on background threads even with Fast Track.", "4. Fast Track")]
        [JsonProperty] public bool KeepCritterAsyncWithFastTrack { get; set; }

        /// <summary>
        /// With Fast Track: raise how many dupes may decide per frame (Fast Track reads the game's own
        /// setting, which is 1). Fast Track prepares each decision on worker threads, so more decisions
        /// per frame also means more CPU cores in use.
        /// </summary>
        [Option("More decisions with Fast Track", "With Fast Track: let more dupes decide per frame (backs off when frames get slow).", "4. Fast Track")]
        [JsonProperty] public bool FastTrackDecisionBoost { get; set; } = true;

        /// <summary>Upper limit for decisions per frame in Fast Track mode.</summary>
        [Option("Max decisions per frame (Fast Track)", "Upper limit in Fast Track mode. Only used with CPU profile Custom.", "4. Fast Track")]
        [Limit(1, 64)]
        [JsonProperty] public int FastTrackMaxDecisionsPerFrame { get; set; } = 8;

        /// <summary>Fast Track mode backs off automatically when frames get slower than this (ms).</summary>
        [Option("Back off above frame time (ms)", "Fast Track mode lowers decisions per frame when frames get slower than this.", "4. Fast Track", Format = "F0")]
        [Limit(16, 500)]
        [JsonProperty] public float FastTrackFrameTimeLimitMs { get; set; } = 45f;

        // ---- Diagnostics ----

        /// <summary>Write a stats line to Player.log every N real seconds (0 = off).</summary>
        [Option("Stats to log every (real s)", "Write a stats line to Player.log this often. 0 = off.", "5. Diagnostics", Format = "F0")]
        [Limit(0, 600)]
        [JsonProperty] public float LogStatsEverySeconds { get; set; } = 60f;

        // ---- Effective switches (computed at load, not saved) ----

        public bool FastTrackActive { get; private set; }

        private bool Keep(string part)
        {
            if (part == "Scheduler" && KeepSchedulerWithFastTrack) return true;
            if (part == "PathProber" && KeepPathProberWithFastTrack) return true;
            if (part == "CritterAsync" && KeepCritterAsyncWithFastTrack) return true;
            return KeepWithFastTrack != null && KeepWithFastTrack.Exists(p => string.Equals(p, part, StringComparison.OrdinalIgnoreCase));
        }

        private bool Allowed(bool setting, string part) => setting && (!FastTrackActive || Keep(part));

        public bool UseScheduler => Allowed(EnableBrainScheduler, "Scheduler");
        public bool UsePathProber => Allowed(EnablePathProberBoost, "PathProber");
        // Critters on background threads only helps when there is a spare core.
        public bool UseCritterAsync => AsyncCritterPathProbes && Allowed(EffectiveCritterAsync, "CritterAsync");
        public bool UseProfiler => Allowed(ProfilePeriodicUpdates, "Profiler");
        public bool UseSpread => Allowed(SpreadPeriodicUpdates, "Spread");
        public bool UseFastTrackDecisionBoost => FastTrackActive && FastTrackDecisionBoost;

        /// <summary>Decide once at load whether Fast Track is present.</summary>
        public void ResolveFastTrack(bool detected)
        {
            switch (FastTrackDetection)
            {
                case FastTrackSetting.Assume: FastTrackActive = true; break;
                case FastTrackSetting.Ignore: FastTrackActive = false; break;
                default: FastTrackActive = detected; break;
            }
        }

        // ---- CPU profile resolution (computed at load, not saved) ----

        private sealed class Profile
        {
            public string name;
            public int proberThreads, probesPerFrame, maxBrains, ftMaxDecisions;
            public float budgetMs;
            public bool critterAsync;
        }

        // 1 core: nothing to offload to, stay close to vanilla. 2 cores: main + simulation thread
        // already use both, so only light extra work. More cores: more background path-finding.
        private static readonly Profile[] Profiles =
        {
            new Profile { name = "Single", proberThreads = 1, probesPerFrame = 4,  budgetMs = 2f, maxBrains = 8,  ftMaxDecisions = 2,  critterAsync = false },
            new Profile { name = "Dual",   proberThreads = 1, probesPerFrame = 4,  budgetMs = 3f, maxBrains = 16, ftMaxDecisions = 3,  critterAsync = true },
            new Profile { name = "Quad",   proberThreads = 2, probesPerFrame = 6,  budgetMs = 4f, maxBrains = 32, ftMaxDecisions = 6,  critterAsync = true },
            new Profile { name = "Hexa",   proberThreads = 3, probesPerFrame = 8,  budgetMs = 4f, maxBrains = 48, ftMaxDecisions = 8,  critterAsync = true },
            new Profile { name = "Many",   proberThreads = 6, probesPerFrame = 12, budgetMs = 5f, maxBrains = 64, ftMaxDecisions = 12, critterAsync = true },
        };

        private Profile active;

        public int LogicalCores => Environment.ProcessorCount;

        /// <summary>Name of the profile in use ("Custom" when the raw numbers are used).</summary>
        public string ActiveProfileName => active?.name ?? "Custom";

        private void ResolveProfile()
        {
            string want = CpuProfile.ToString();
            active = null;
            if (CpuProfile == CpuProfileSetting.Custom) return;
            foreach (var p in Profiles)
                if (p.name.Equals(want, StringComparison.OrdinalIgnoreCase)) { active = p; return; }
            int cores = LogicalCores;
            active = cores <= 1 ? Profiles[0] : cores == 2 ? Profiles[1] : cores <= 4 ? Profiles[2] : cores <= 8 ? Profiles[3] : Profiles[4];
        }

        public int EffectivePathProbesPerFrame => active?.probesPerFrame ?? PathProbesPerFrame;
        public float EffectiveBudgetMs => active?.budgetMs ?? MaxMillisecondsPerFrame;
        public int EffectiveMaxBrainsPerFrame => Math.Max(MinBrainsPerFrame, active?.maxBrains ?? MaxBrainsPerFrame);
        public int EffectiveFastTrackMaxDecisions => active?.ftMaxDecisions ?? FastTrackMaxDecisionsPerFrame;
        private bool EffectiveCritterAsync => active?.critterAsync ?? AsyncCritterPathProbes;

        public int ResolvedProberThreads()
        {
            if (active != null) return active.proberThreads;
            if (PathProberThreads > 0) return Math.Min(PathProberThreads, 16);
            return Math.Max(1, Math.Min(6, Environment.ProcessorCount / 2));
        }

        public static void Load(string modPath)
        {
            string file = Path.Combine(modPath ?? ".", "config.json");
            try
            {
                if (File.Exists(file))
                {
                    var loaded = JsonConvert.DeserializeObject<BoostConfig>(File.ReadAllText(file));
                    if (loaded != null) Instance = loaded;
                }
                Instance.Sanitize();
                Instance.ResolveProfile();
                // Write back so new options appear in the file with their defaults.
                File.WriteAllText(file, JsonConvert.SerializeObject(Instance, Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DupeBrainBoost] config.json could not be read, using defaults: " + e.Message);
                Instance = new BoostConfig();
                Instance.ResolveProfile();
            }
        }

        private void Sanitize()
        {
            TargetIntervalSeconds = Clamp(TargetIntervalSeconds, 0.1f, 30f);
            UrgentIntervalSeconds = Clamp(UrgentIntervalSeconds, 0.05f, TargetIntervalSeconds);
            MaxMillisecondsPerFrame = Clamp(MaxMillisecondsPerFrame, 0.5f, 50f);
            MinBrainsPerFrame = Math.Max(1, MinBrainsPerFrame);
            MaxBrainsPerFrame = Math.Max(MinBrainsPerFrame, MaxBrainsPerFrame);
            LowBreathFraction = Clamp(LowBreathFraction, 0f, 1f);
            UrgentBudgetShare = Clamp(UrgentBudgetShare, 0.05f, 1f);
            SensorThrottleSeconds = Clamp(SensorThrottleSeconds, 0f, 10f);
            SafeSpotSearchThrottleSeconds = Clamp(SafeSpotSearchThrottleSeconds, 0f, 30f);
            PathProbesPerFrame = Math.Max(1, Math.Min(32, PathProbesPerFrame));
            FastTrackMaxDecisionsPerFrame = Math.Max(1, Math.Min(64, FastTrackMaxDecisionsPerFrame));
            FastTrackFrameTimeLimitMs = Clamp(FastTrackFrameTimeLimitMs, 16f, 500f);
            if (KeepWithFastTrack == null) KeepWithFastTrack = new System.Collections.Generic.List<string>();
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
