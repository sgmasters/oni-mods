using System.Collections.Generic;
using Klei.AI;
using UnityEngine;

namespace SafeDupes
{
    /// <summary>
    /// Hurt dupes refuse player errands (dig, build, supply, ...) at dangerous spots, so they pick a
    /// safe errand instead, or go heal / rest. Self-care chores (eat, sleep, heal, recover breath,
    /// flee) have no errand group and are never blocked.
    /// </summary>
    internal static class HealthGate
    {
        private sealed class DupeInfo
        {
            public AmountInstance hp;
            public ScaldingMonitor.Instance temp;
            public AttributeInstance cold;
            public int frame = -1;
            public float hpFrac = 1f;
        }

        private struct CellDanger { public float until; public float maxT, minT, rads; public bool airless; }

        private static readonly Dictionary<GameObject, DupeInfo> dupes = new Dictionary<GameObject, DupeInfo>();
        private static readonly Dictionary<int, CellDanger> cells = new Dictionary<int, CellDanger>();

        public static void Reset()
        {
            dupes.Clear();
            cells.Clear();
        }

        /// <summary>True if this dupe should NOT take this errand at this target.</summary>
        public static bool Refuse(ref Chore.Precondition.Context context, KMonoBehaviour target)
        {
            var cfg = SafeConfig.Instance;
            if (!cfg.HealthGate || target == null) return false;
            var chore = context.chore;
            var groups = chore?.choreType?.groups;
            if (groups == null || groups.Length == 0) return false; // self-care: never block

            GameObject go = context.consumerState.gameObject;
            if (go == null) return false;
            DupeInfo d = GetDupe(go);
            if (d.hpFrac >= cfg.LowHealthFraction) return false;
            if (go.HasTag(GameTags.HasSuitTank)) return false; // atmo / lead suits protect

            int cell = Grid.PosToCell(target);
            if (!Grid.IsValidCell(cell)) return false;
            CellDanger c = GetCell(cell);

            if (d.temp != null && !d.temp.IsNullOrStopped())
            {
                float hot = d.temp.GetScaldingThreshold();
                float cold = d.cold != null ? d.cold.GetTotalValue() : 0f;
                if (c.maxT > hot - cfg.TemperatureMargin) return true;
                if (cold > 0f && c.minT < cold + cfg.TemperatureMargin) return true;
            }
            if (c.rads > cfg.RadiationDanger) return true;
            if (d.hpFrac < cfg.CriticalHealthFraction && c.airless) return true;
            return false;
        }

        private static DupeInfo GetDupe(GameObject go)
        {
            if (!dupes.TryGetValue(go, out DupeInfo d))
            {
                d = new DupeInfo
                {
                    hp = Db.Get().Amounts.HitPoints.Lookup(go),
                    temp = go.GetSMI<ScaldingMonitor.Instance>(),
                    cold = Db.Get().Attributes.ScoldingThreshold.Lookup(go),
                };
                if (dupes.Count > 1000) dupes.Clear();
                dupes[go] = d;
            }
            int frame = Time.frameCount;
            if (d.frame != frame)
            {
                d.frame = frame;
                float max = d.hp != null ? d.hp.GetMax() : 0f;
                d.hpFrac = max > 0f ? d.hp.value / max : 1f;
            }
            return d;
        }

        /// <summary>Conditions in the 3x3 open cells around the work target (cached ~2 game seconds).</summary>
        private static CellDanger GetCell(int cell)
        {
            float now = GameClock.Instance != null ? GameClock.Instance.GetTime() : Time.time;
            if (cells.TryGetValue(cell, out CellDanger c) && c.until > now) return c;

            c = new CellDanger { until = now + 2f, maxT = float.MinValue, minT = float.MaxValue, airless = true };
            bool radiation = Sim.IsRadiationEnabled();
            int open = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int n = Grid.OffsetCell(cell, new CellOffset(dx, dy));
                    if (!Grid.IsValidCell(n)) continue;
                    if (radiation) c.rads = Mathf.Max(c.rads, Grid.Radiation[n]);
                    if (Grid.Solid[n] || Grid.Mass[n] <= 0f) continue;
                    open++;
                    float t = Grid.Temperature[n];
                    if (t > c.maxT) c.maxT = t;
                    if (t < c.minT) c.minT = t;
                    var e = Grid.Element[n];
                    if (e.IsGas && e.HasTag(GameTags.Breathable) && Grid.Mass[n] > 0.05f) c.airless = false;
                }
            if (open == 0)
            {
                // Fully enclosed target (e.g. a tile being dug): touching a hot tile does not scald,
                // the gas/liquid around the worker does - and that is checked at the work spot itself.
                c.maxT = float.MinValue;
                c.minT = float.MaxValue;
                c.airless = false;
            }
            if (cells.Count > 20000) cells.Clear();
            cells[cell] = c;
            return c;
        }
    }
}
