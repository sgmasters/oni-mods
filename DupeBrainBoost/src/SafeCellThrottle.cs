using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace DupeBrainBoost
{
    /// <summary>
    /// SafeCellSensor.Update runs on every decision of an IDLE dupe and does a path-finder search for
    /// the nearest "safe" cell. When the colony has no safe cell reachable (bad air, heat...), the
    /// search walks the dupe's entire reachable area: 50-150 ms on big maps. While the dupe stays on
    /// the same cell, the answer barely changes, so it is reused for SafeSpotSearchThrottleSeconds.
    /// Non-idle dupes are untouched (vanilla clears the result instantly for them).
    /// </summary>
    [HarmonyPatch(typeof(SafeCellSensor), nameof(SafeCellSensor.Update))]
    internal static class SafeCellThrottle
    {
        private sealed class State { public float lastRun = float.NegativeInfinity; public int lastCell = -1; }

        private static readonly Dictionary<SafeCellSensor, State> states = new Dictionary<SafeCellSensor, State>();
        private static readonly AccessTools.FieldRef<SafeCellSensor, Navigator> NavRef =
            AccessTools.FieldRefAccess<SafeCellSensor, Navigator>("navigator");
        private static readonly AccessTools.FieldRef<SafeCellSensor, KPrefabID> PrefabRef =
            AccessTools.FieldRefAccess<SafeCellSensor, KPrefabID>("prefabid");

        public static int Runs, Skipped;
        public static double Ms;

        public static void ResetStats() { Runs = Skipped = 0; Ms = 0; }

        public static void Clear() => states.Clear();

        internal static bool Prefix(SafeCellSensor __instance, out long __state)
        {
            __state = 0;
            float throttle = BoostConfig.Instance.SafeSpotSearchThrottleSeconds;
            var prefab = PrefabRef(__instance);
            if (prefab == null || !prefab.HasTag(GameTags.Idle)) return true; // vanilla fast path
            var nav = NavRef(__instance);
            int cell = nav != null ? Grid.PosToCell(nav) : -1;
            float now = GameClock.Instance != null ? GameClock.Instance.GetTime() : Time.time;
            if (!states.TryGetValue(__instance, out State st))
            {
                if (states.Count > 2000) states.Clear();
                states[__instance] = st = new State();
            }
            if (throttle > 0f && cell == st.lastCell && now - st.lastRun < throttle)
            {
                Skipped++;
                return false; // keep the previous answer
            }
            st.lastCell = cell;
            st.lastRun = now;
            __state = Stopwatch.GetTimestamp();
            return true;
        }

        internal static void Postfix(long __state)
        {
            if (__state == 0) return;
            Runs++;
            Ms += (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
        }
    }
}
