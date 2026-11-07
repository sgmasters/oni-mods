using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DupeBrainBoost
{
    /// <summary>
    /// Every dupe decision first runs all of that dupe's sensors (Sensors.UpdateSensors):
    /// pickupable/fetch lists, closest food, idle wander cell, mingle cell, toilet, ...
    /// Several of them are path-finder searches. When a dupe re-decides again shortly after
    /// (danger re-checks, just-finished chores) from the SAME cell, their answers have not changed
    /// meaningfully, so they are re-used for SensorThrottleSeconds of game time.
    ///
    /// Only cell/building sensors are throttled (idle, mingle, balloon stand, toilet, assignable
    /// reachability). Item sensors, PathProberSensor and SafeCellSensor (own throttle) always run.
    /// A dupe that moved to another cell always gets fresh sensors.
    /// </summary>
    [HarmonyPatch(typeof(Sensors), nameof(Sensors.UpdateSensors))]
    internal static class SensorThrottle
    {
        private sealed class State
        {
            public int cell = -1;
            public readonly Dictionary<Sensor, float> lastRun = new Dictionary<Sensor, float>(8);
        }

        private static readonly Dictionary<Sensors, State> states = new Dictionary<Sensors, State>();
        private static readonly Dictionary<Type, bool> throttleable = new Dictionary<Type, bool>();

        public static int Runs, Skipped;

        internal static bool Prepare() => BoostConfig.Instance.SensorThrottleSeconds > 0f;

        public static void Clear()
        {
            states.Clear();
            Runs = Skipped = 0;
        }

        private static bool Throttleable(Sensor s)
        {
            Type t = s.GetType();
            if (!throttleable.TryGetValue(t, out bool ok))
            {
                // Whitelist: only sensors that pick a cell or a building. Sensors that pick an
                // item (ClosestEdible, Pickupable, bionic Closest* canisters/batteries) must stay
                // fresh: a stale item may already be reserved or eaten by another dupe, which
                // makes the chore start with nothing to take (ToggleReserve error in EatChore).
                ok = s is IdleCellSensor || s is MingleCellSensor || s is BalloonStandCellSensor
                     || s is ToiletSensor || s is AssignableReachabilitySensor;
                throttleable[t] = ok;
            }
            return ok;
        }

        internal static bool Prefix(Sensors __instance)
        {
            var list = __instance.sensors;
            if (list == null) return true;
            float window = BoostConfig.Instance.SensorThrottleSeconds;
            float now = GameClock.Instance != null ? GameClock.Instance.GetTime() : Time.time;
            int cell = Grid.PosToCell(__instance);

            if (!states.TryGetValue(__instance, out State st))
            {
                if (states.Count > 4000) states.Clear();
                st = new State();
                states[__instance] = st;
            }
            bool sameCell = cell == st.cell;
            st.cell = cell;

            for (int i = 0; i < list.Count; i++)
            {
                Sensor sensor = list[i];
                if (sensor == null || !sensor.IsEnabled) continue;
                if (sameCell && Throttleable(sensor) && st.lastRun.TryGetValue(sensor, out float last) && now - last < window)
                {
                    Skipped++;
                    continue;
                }
                sensor.Update();
                st.lastRun[sensor] = now;
                Runs++;
            }
            return false; // replaces the vanilla loop (same order, same calls)
        }
    }
}
