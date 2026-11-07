using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace DupeBrainBoost
{
    /// <summary>
    /// The once-per-game-second pause.
    ///
    /// ONI runs periodic updates (every 200 ms / 1 s / 4 s of game time) through StateMachineUpdater.
    /// Each update type has a "bucket"; unless the type was registered as load-balanced, ALL
    /// instances of that type (e.g. one monitor for all 100+ dupes) sit in ONE bucket that runs in a
    /// single 1/60 s slot. Several such buckets landing close together = a visible hitch every
    /// game second. Klei load-balances only some types.
    ///
    /// Spread: register every non-batch periodic update as load-balanced, so its instances are
    /// distributed over all slots of its cycle (60 slots for 1 s). Each instance still updates once
    /// per period with the same dt - only WHEN within the period changes.
    ///
    /// Profile: time every bucket; for frames where periodic updates took long, remember which
    /// bucket names were responsible, and log the top offenders once a minute.
    /// </summary>
    internal static class UpdateSpreader
    {
        internal static bool ShouldSpread(UpdateRate rate)
        {
            var cfg = BoostConfig.Instance;
            if (!cfg.UseSpread) return false;
            return rate == UpdateRate.SIM_200ms || rate == UpdateRate.SIM_1000ms || rate == UpdateRate.SIM_4000ms;
        }

        internal static int Spread;
    }

    /// <summary>
    /// KMonoBehaviour periodic updates (components implementing ISim200ms / ISim1000ms / ISim4000ms).
    /// Patched at the NON-generic entry point SimAndRenderScheduler.Add(object, bool): the generic
    /// Schedule&lt;T&gt; must not be patched (a Harmony replacement would be specialised to one T).
    /// State-machine updates are not spread (their registration path is generic); the profiler
    /// shows whether they matter.
    /// </summary>
    [HarmonyPatch(typeof(SimAndRenderScheduler), nameof(SimAndRenderScheduler.Add))]
    internal static class SimAndRenderScheduler_Add_Patch
    {
        private static readonly System.Reflection.FieldInfo TableField =
            AccessTools.Field(typeof(SimAndRenderScheduler), "bucketTable");

        private static readonly UpdateRate[] MultiSlotRates =
            { UpdateRate.SIM_33ms, UpdateRate.SIM_200ms, UpdateRate.SIM_1000ms, UpdateRate.SIM_4000ms };

        private static readonly Dictionary<Type, bool> decision = new Dictionary<Type, bool>();

        internal static bool Prepare() => BoostConfig.Instance.UseSpread;

        internal static void Prefix(SimAndRenderScheduler __instance, object obj, ref bool load_balance)
        {
            if (load_balance || obj == null) return;
            Type type = obj.GetType();
            if (!decision.TryGetValue(type, out bool spread))
            {
                spread = Decide(__instance, type);
                decision[type] = spread;
                if (spread) UpdateSpreader.Spread++;
            }
            if (spread) load_balance = true;
        }

        private static bool Decide(SimAndRenderScheduler sched, Type type)
        {
            bool periodic = typeof(ISim200ms).IsAssignableFrom(type) || typeof(ISim1000ms).IsAssignableFrom(type) ||
                            typeof(ISim4000ms).IsAssignableFrom(type);
            if (!periodic) return false;
            // If a bucket entry for this type already exists as a single bucket (batch-update types are
            // registered up front), keep it: switching would only produce warnings and change nothing.
            var table = TableField?.GetValue(sched) as System.Collections.IDictionary;
            if (table == null) return false;
            foreach (var rate in MultiSlotRates)
            {
                string id = type.Name + " " + rate.ToString();
                if (!table.Contains(id)) continue;
                object entry = table[id];
                var bucketsField = entry?.GetType().GetField("buckets");
                if (bucketsField?.GetValue(entry) is Array arr && arr.Length <= 1) return false;
            }
            return true;
        }

        internal static void ResetCache() => decision.Clear();
    }

    /// <summary>Times every bucket of periodic updates (same loop as vanilla, plus a stopwatch).</summary>
    [HarmonyPatch(typeof(StateMachineUpdater.BucketGroup), nameof(StateMachineUpdater.BucketGroup.AdvanceOneSubTick))]
    internal static class BucketGroup_AdvanceOneSubTick_Patch
    {
        private static readonly AccessTools.FieldRef<StateMachineUpdater.BucketGroup, List<List<StateMachineUpdater.BaseUpdateBucket>>> FramesRef =
            AccessTools.FieldRefAccess<StateMachineUpdater.BucketGroup, List<List<StateMachineUpdater.BaseUpdateBucket>>>("bucketFrames");

        private static readonly AccessTools.FieldRef<StateMachineUpdater.BucketGroup, int> NextRef =
            AccessTools.FieldRefAccess<StateMachineUpdater.BucketGroup, int>("nextUpdateIndex");

        internal static bool Prepare() => BoostConfig.Instance.UseProfiler;

        internal static bool Prefix(StateMachineUpdater.BucketGroup __instance, float dt)
        {
            var frames = FramesRef(__instance);
            ref int next = ref NextRef(__instance);
            var list = frames[next];
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                var bucket = list[i];
                if (bucket.count == 0) continue;
                long t0 = Stopwatch.GetTimestamp();
                bucket.Update(dt);
                UpdateProfiler.Record(bucket.name, Stopwatch.GetTimestamp() - t0);
            }
            next = (next + 1) % frames.Count;
            return false;
        }
    }

    internal static class UpdateProfiler
    {
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        private static int currentFrame = -1;
        private static double frameTotalMs;
        private static readonly Dictionary<string, double> thisFrame = new Dictionary<string, double>();

        private static int heavyFrames, frames;
        private static double heavyTotalMs, allTotalMs, worstFrameMs;
        private static readonly Dictionary<string, double> heavyByName = new Dictionary<string, double>();
        private static readonly Dictionary<string, double> allByName = new Dictionary<string, double>();

        private const double HeavyFrameMs = 20.0;

        public static void Record(string name, long ticks)
        {
            int frame = Time.frameCount;
            if (frame != currentFrame) Rollover(frame);
            double ms = ticks * TicksToMs;
            frameTotalMs += ms;
            name = name ?? "?";
            thisFrame.TryGetValue(name, out double v);
            thisFrame[name] = v + ms;
        }

        private static void Rollover(int newFrame)
        {
            if (currentFrame >= 0 && thisFrame.Count > 0)
            {
                frames++;
                allTotalMs += frameTotalMs;
                if (frameTotalMs > worstFrameMs) worstFrameMs = frameTotalMs;
                bool heavy = frameTotalMs >= HeavyFrameMs;
                if (heavy) { heavyFrames++; heavyTotalMs += frameTotalMs; }
                foreach (var kv in thisFrame)
                {
                    allByName.TryGetValue(kv.Key, out double a);
                    allByName[kv.Key] = a + kv.Value;
                    if (heavy)
                    {
                        heavyByName.TryGetValue(kv.Key, out double h);
                        heavyByName[kv.Key] = h + kv.Value;
                    }
                }
            }
            thisFrame.Clear();
            frameTotalMs = 0;
            currentFrame = newFrame;
        }

        /// <summary>Called once a minute from the stats logger.</summary>
        public static void LogAndReset()
        {
            if (frames == 0) return;
            string top(Dictionary<string, double> d, int n, double div) => string.Join(", ",
                d.OrderByDescending(kv => kv.Value).Take(n).Select(kv => $"{Short(kv.Key)} {kv.Value / div:0.0}ms"));
            Debug.LogFormat("[DupeBrainBoost] periodic updates: avg {0:0.00}ms/frame, worst frame {1:0}ms, heavy frames (>{2:0}ms) {3}/{4}, spread types so far {5}",
                allTotalMs / frames, worstFrameMs, HeavyFrameMs, heavyFrames, frames, UpdateSpreader.Spread);
            if (heavyFrames > 0)
                Debug.Log("[DupeBrainBoost]   top in heavy frames (avg per heavy frame): " + top(heavyByName, 10, heavyFrames));
            Debug.Log("[DupeBrainBoost]   top overall (ms per minute): " + top(allByName, 10, 1.0));
            heavyFrames = frames = 0;
            heavyTotalMs = allTotalMs = worstFrameMs = 0;
            heavyByName.Clear();
            allByName.Clear();
        }

        private static string Short(string name) => name.Length > 60 ? name.Substring(0, 60) : name;
    }
}
