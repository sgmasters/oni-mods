using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KMod;
using UnityEngine;

namespace DupeBrainBoost
{
    public sealed class DupeBrainBoostMod : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            BoostConfig.Load(path);
            var c = BoostConfig.Instance;
            c.ResolveFastTrack(DetectFastTrack());
            base.OnLoad(harmony); // PatchAll - every patch's Prepare() reads the effective switches
            // "Options" button for this mod in the Mods menu (PLib).
            PeterHan.PLib.Core.PUtil.InitLibrary(false);
            new PeterHan.PLib.Options.POptions().RegisterOptions(this, typeof(BoostConfig));
            Debug.LogFormat("[DupeBrainBoost] loaded. CPU: {0} logical cores -> profile {1} (setting {2}) | Fast Track {3} (detection {4})",
                c.LogicalCores, c.ActiveProfileName, c.CpuProfile, c.FastTrackActive ? "ON" : "off", c.FastTrackDetection);
            Debug.LogFormat("[DupeBrainBoost]   scheduler={0} (budget {1}ms, max {2}/frame) | pathProber={3} (threads {4}, probes/frame {5}) | " +
                            "critterAsync={6} | sensorThrottle={7}s safeSpotThrottle={8}s | decisionsPerFrameBoost={9} (max {10}) | profiler={11}",
                c.UseScheduler, c.EffectiveBudgetMs, c.EffectiveMaxBrainsPerFrame, c.UsePathProber, c.ResolvedProberThreads(),
                AsyncPathProber_TickFrame_Patch.ProbesPerFrame(), c.UseCritterAsync, c.SensorThrottleSeconds,
                c.SafeSpotSearchThrottleSeconds, c.FastTrackDecisionBoost && !c.UseScheduler, c.EffectiveFastTrackMaxDecisions, c.UseProfiler);
            if (c.FastTrackActive)
                Debug.Log("[DupeBrainBoost] Fast Track mode: Fast Track's scheduler, path cache and update patches are used; " +
                          "Dupe Brain Boost only raises decisions per frame and throttles sensors. " +
                          "Parts can be forced back on in the mod options.");
        }

        /// <summary>Is Fast Track enabled? (mod list first, loaded assemblies as a fallback)</summary>
        private static bool DetectFastTrack()
        {
            try
            {
                var mods = Global.Instance?.modManager?.mods;
                if (mods != null)
                    foreach (var m in mods)
                        if (m != null && m.staticID == "PeterHan.FastTrack" && m.IsEnabledForActiveDlc())
                            return true;
            }
            catch (System.Exception) { }
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetName().Name == "FastTrack") return true;
            return false;
        }

        public override void OnAllModsLoaded(Harmony harmony, IReadOnlyList<Mod> mods)
        {
            base.OnAllModsLoaded(harmony, mods);
            // Fast Track replaces BrainScheduler.RenderEveryTick entirely, which would bypass us.
            var target = AccessTools.Method(typeof(BrainScheduler), "RenderEveryTick");
            var info = target != null ? Harmony.GetPatchInfo(target) : null;
            if (info != null && info.Prefixes.Any(p => p.owner != harmony.Id))
            {
                Debug.LogWarning("[DupeBrainBoost] Another mod (" +
                    string.Join(", ", info.Prefixes.Select(p => p.owner).Distinct()) +
                    ") replaces BrainScheduler.RenderEveryTick; the errand scheduler part of Dupe Brain Boost may be bypassed.");
            }
        }
    }

    /// <summary>Swap in the adaptive scheduler for the duplicant brain group only.</summary>
    [HarmonyPatch(typeof(BrainScheduler.BrainGroup), nameof(BrainScheduler.BrainGroup.RenderEveryTick))]
    internal static class BrainGroup_RenderEveryTick_Patch
    {
        internal static bool Prepare() => BoostConfig.Instance.UseScheduler;

        internal static bool Prefix(BrainScheduler.BrainGroup __instance, out long __state)
        {
            __state = System.Diagnostics.Stopwatch.GetTimestamp();
            if (__instance.tag != GameTags.DupeBrain) return true; // critters: vanilla (timed in Postfix)
            DupeScheduler.Update(__instance);
            return false;
        }

        internal static void Postfix(BrainScheduler.BrainGroup __instance, long __state)
        {
            if (__instance.tag == GameTags.DupeBrain) return;
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - __state) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            CritterTiming.Add(ms);
        }
    }

    internal static class CritterTiming
    {
        public static double LastMs, TotalMs, MaxMs;
        public static int Frames, LastFrame = -1;

        public static void Add(double ms)
        {
            int f = Time.frameCount;
            if (f != LastFrame) { LastFrame = f; LastMs = 0; Frames++; }
            LastMs += ms;
            TotalMs += ms;
            if (LastMs > MaxMs) MaxMs = LastMs;
        }

        public static void Reset() { TotalMs = MaxMs = 0; Frames = 0; }
    }

    /// <summary>
    /// Critters: compute reachability on the background path-finding threads (like dupes, rovers and
    /// Flydos already do) instead of synchronously on the main thread at every brain update.
    /// Set before Navigator.OnSpawn, which registers async navigators with AsyncPathProber.
    /// </summary>
    [HarmonyPatch(typeof(Navigator), "OnSpawn")]
    internal static class Navigator_OnSpawn_Patch
    {
        internal static bool Prepare() => BoostConfig.Instance.UseCritterAsync;

        internal static void Prefix(Navigator __instance)
        {
            if (__instance.executePathProbeTaskAsync || AsyncPathProber.Instance == null) return;
            if (__instance.GetComponent<CreatureBrain>() == null) return;
            __instance.executePathProbeTaskAsync = true;
            CritterAsync.Count++;
        }
    }

    internal static class CritterAsync
    {
        public static int Count;
    }

    [HarmonyPatch(typeof(Game), "OnSpawn")]
    internal static class Game_OnSpawn_Patch
    {
        internal static void Postfix()
        {
            DupeScheduler.Reset();
            SafeCellThrottle.Clear();
            SensorThrottle.Clear();
            PathGridPool_Patch.Reset();
        }
    }

    internal static class ProberTiming
    {
        public static double LastMs, TotalMs;
        public static int Calls;
    }

    /// <summary>More background path-finding threads (vanilla: 1).</summary>
    [HarmonyPatch(typeof(AsyncPathProber), nameof(AsyncPathProber.CreateInstance))]
    internal static class AsyncPathProber_CreateInstance_Patch
    {
        internal static bool Prepare() => BoostConfig.Instance.UsePathProber;

        internal static void Prefix(ref int count)
        {
            int wanted = BoostConfig.Instance.ResolvedProberThreads();
            if (wanted > count) count = wanted;
        }
    }

    /// <summary>
    /// More path grids refreshed per frame (vanilla: hard-coded 4 in Manager.TickFrame).
    /// Replaces the single "ldc.i4.4" in that method; if the method changed shape in a game
    /// update, nothing is modified.
    /// </summary>
    [HarmonyPatch(typeof(AsyncPathProber.Manager), nameof(AsyncPathProber.Manager.TickFrame))]
    internal static class AsyncPathProber_TickFrame_Patch
    {
        internal static bool Prepare() => BoostConfig.Instance.UsePathProber;

        // Each probe borrows a full-size path grid from a pool that only holds (4 + threads) grids.
        // Asking for more per frame makes the game allocate and discard multi-MB grids -> garbage
        // collection pauses. So never exceed the pool size.
        internal static int ProbesPerFrame()
        {
            var c = BoostConfig.Instance;
            // With the enlarged pool (PathGridPool_Patch) the pool holds enough grids for this many.
            int poolLimit = c.UsePathProber ? PathGridPool_Patch.PoolSize - c.ResolvedProberThreads() : 4;
            return System.Math.Max(1, System.Math.Min(c.EffectivePathProbesPerFrame, poolLimit));
        }

        // Main-thread cost of TickFrame (applying finished path probes, queueing new ones).
        internal static void Prefix(out long __state) => __state = System.Diagnostics.Stopwatch.GetTimestamp();

        internal static void Postfix(long __state)
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - __state) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            ProberTiming.LastMs = ms;
            ProberTiming.TotalMs += ms;
            ProberTiming.Calls++;
        }

        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var hits = new List<int>();
            for (int i = 0; i < list.Count; i++)
                if (list[i].opcode == OpCodes.Ldc_I4_4) hits.Add(i);
            if (hits.Count != 1)
            {
                Debug.LogWarning("[DupeBrainBoost] AsyncPathProber.TickFrame has " + hits.Count +
                                 " constant-4 loads (expected 1); probes-per-frame left at vanilla.");
                return list;
            }
            var getter = AccessTools.Method(typeof(AsyncPathProber_TickFrame_Patch), nameof(ProbesPerFrame));
            var old = list[hits[0]];
            var repl = new CodeInstruction(OpCodes.Call, getter) { labels = old.labels, blocks = old.blocks };
            list[hits[0]] = repl;
            return list;
        }
    }
}
