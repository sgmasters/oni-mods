using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Pool;

namespace DupeBrainBoost
{
    /// <summary>
    /// Fast Track mode: Fast Track replaces the brain scheduler but still asks the game how many
    /// dupes may decide per frame (DupeBrainGroup.InitialProbeCount, vanilla = 1). We answer with an
    /// adaptive number: enough for every dupe to re-think about once per TargetIntervalSeconds at the
    /// current game speed, scaled down automatically whenever frames get slower than
    /// FastTrackFrameTimeLimitMs. Fast Track prepares each queued decision on worker threads, so a
    /// higher number also spreads more work over more CPU cores.
    /// Also works without Fast Track if our own scheduler is switched off (vanilla reads the same value).
    /// </summary>
    [HarmonyPatch]
    internal static class DupeBrainGroup_InitialProbeCount_Patch
    {
        private static float scale = 1f;
        private static float smoothFrameMs = 33f;
        private static int lastFrame = -1;

        // stats
        private static int statFrames;
        private static long statDecisions;
        private static float nextLogAt = -1f;

        // Only needed when our own scheduler is not running (Fast Track mode, or scheduler switched off):
        // our scheduler never calls InitialProbeCount for dupes.
        internal static bool Prepare()
        {
            var c = BoostConfig.Instance;
            if (!c.FastTrackDecisionBoost || c.UseScheduler) return false;
            if (TargetMethod() == null)
            {
                Debug.LogWarning("[DupeBrainBoost] BrainScheduler.DupeBrainGroup.InitialProbeCount not found (game update?); decisions-per-frame boost disabled.");
                return false;
            }
            return true;
        }

        internal static MethodBase TargetMethod()
        {
            var t = AccessTools.Inner(typeof(BrainScheduler), "DupeBrainGroup");
            return t != null ? AccessTools.Method(t, "InitialProbeCount") : null;
        }

        internal static void Postfix(BrainScheduler.BrainGroup __instance, ref int __result)
        {
            var c = BoostConfig.Instance;
            int frame = Time.frameCount;
            if (frame != lastFrame)
            {
                lastFrame = frame;
                float frameMs = Time.unscaledDeltaTime * 1000f;
                smoothFrameMs = smoothFrameMs * 0.9f + frameMs * 0.1f;
                // Back off fast when frames are slow, recover slowly.
                if (smoothFrameMs > c.FastTrackFrameTimeLimitMs) scale = Mathf.Max(0.05f, scale * 0.9f);
                else scale = Mathf.Min(1f, scale + 0.01f);
            }
            int count = __instance.BrainCount;
            float gameDt = Mathf.Min(Time.deltaTime, 0.2f);
            int want = Mathf.CeilToInt(count * gameDt / c.TargetIntervalSeconds * scale);
            int n = Mathf.Clamp(want, __result, c.EffectiveFastTrackMaxDecisions);
            if (n > __result) __result = n;

            statFrames++;
            statDecisions += __result;
            float now = Time.unscaledTime;
            if (nextLogAt < 0f) nextLogAt = now + c.LogStatsEverySeconds;
            else if (c.LogStatsEverySeconds > 0f && now >= nextLogAt)
            {
                nextLogAt = now + c.LogStatsEverySeconds;
                Debug.LogFormat("[DupeBrainBoost] Fast Track mode: dupes={0} speed={1:0.#}x frame={2:0}ms | decisions/frame avg={3:0.0} " +
                                "(now {4}, limit {5}, scale {6:0.00}) | safe-spot searches={7} ({8:0}ms, skipped {9}) | sensors run={11} skipped={12} | GC runs={10}",
                    count, Time.timeScale, smoothFrameMs, statFrames > 0 ? (double)statDecisions / statFrames : 0, __result,
                    c.EffectiveFastTrackMaxDecisions, scale, SafeCellThrottle.Runs, SafeCellThrottle.Ms, SafeCellThrottle.Skipped,
                    System.GC.CollectionCount(0) - lastGc, SensorThrottle.Runs, SensorThrottle.Skipped);
                SensorThrottle.Runs = SensorThrottle.Skipped = 0;
                lastGc = System.GC.CollectionCount(0);
                SafeCellThrottle.ResetStats();
                statFrames = 0;
                statDecisions = 0;
            }
        }

        private static int lastGc;
    }

    /// <summary>
    /// Path-finding on more threads WITHOUT extra garbage: every probe borrows a path grid from a
    /// pool that vanilla sizes for 1 thread + 4 probes. With more threads / probes per frame, grids
    /// overflow the pool, get thrown away and re-allocated (multi-MB each) -> GC pauses.
    /// After a pool is created we swap in one large enough for our settings.
    /// </summary>
    [HarmonyPatch(typeof(AsyncPathProber.Manager), nameof(AsyncPathProber.Manager.Register))]
    internal static class PathGridPool_Patch
    {
        private static readonly AccessTools.FieldRef<AsyncPathProber.Manager, ConcurrentDictionary<ulong, ObjectPool<PathGrid>>> PoolRef =
            AccessTools.FieldRefAccess<AsyncPathProber.Manager, ConcurrentDictionary<ulong, ObjectPool<PathGrid>>>("gridPool");

        private static readonly HashSet<ulong> enlarged = new HashSet<ulong>();
        private static AsyncPathProber.Manager owner; // a new manager is created for every loaded save

        public static bool Enlarged;

        public static int PoolSize
        {
            get
            {
                var c = BoostConfig.Instance;
                return 4 + c.ResolvedProberThreads() + 2 * c.EffectivePathProbesPerFrame;
            }
        }

        internal static bool Prepare() => BoostConfig.Instance.UsePathProber;

        internal static void Postfix(AsyncPathProber.Manager __instance, Navigator nav)
        {
            if (nav == null || nav.PathGrid == null) return;
            try
            {
                lock (__instance)
                {
                    if (!ReferenceEquals(owner, __instance)) { owner = __instance; enlarged.Clear(); }
                    var pools = PoolRef(__instance);
                    ulong cls = nav.PathGrid.AllocatedClassification;
                    if (pools == null || enlarged.Contains(cls)) return;
                    var pg = nav.PathGrid;
                    int width = pg.widthInCells, height = pg.heightInCells;
                    bool applyOffset = pg.applyOffset;
                    var navTypes = new NavType[pg.ValidNavTypes.Length];
                    pg.ValidNavTypes.CopyTo(navTypes, 0);
                    int size = PoolSize;
                    pools[cls] = new ObjectPool<PathGrid>(() => new PathGrid(width, height, applyOffset, navTypes),
                        null, null, null, collectionCheck: false, size, size);
                    enlarged.Add(cls);
                    Enlarged = true;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[DupeBrainBoost] could not enlarge path grid pool: " + e.Message);
            }
        }

        /// <summary>New AsyncPathProber per loaded save: forget which pools we replaced.</summary>
        internal static void Reset() { }
    }
}
