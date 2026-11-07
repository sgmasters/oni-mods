using System.Collections.Generic;
using UnityEngine;

namespace SafeDupes
{
    /// <summary>
    /// "If this job is finished while I stand here, can I still get out?"
    ///
    /// Uses the game's own path-finder (PathFinder.Run) on the dupe's NavGrid, with a wrapper
    /// around the dupe's movement abilities that pretends the job is already done:
    ///   - blocked cells: become solid (a tile being built) -> cannot be entered or passed through
    ///   - removed cells: lose their support (tile / ladder being deconstructed, cell being dug)
    ///     -> cannot stand on them, climb them or rely on them as a wall/floor
    /// The search stops as soon as the dupe gets EscapeRadius cells away or explores EscapeNodes
    /// positions ("escaped"). If it runs out of positions first, the spot is a trap.
    /// Main thread only (PathFinder uses static scratch data).
    /// </summary>
    internal static class TrapSim
    {
        private sealed class WhatIfAbilities : PathFinderAbilities
        {
            public PathFinderAbilities inner;
            public NavGrid grid;
            public readonly HashSet<int> blocked = new HashSet<int>();
            public readonly HashSet<int> removed = new HashSet<int>();

            public WhatIfAbilities() : base(null) { }

            protected override void Refresh(Navigator navigator) { }
            public override PathFinderAbilities Clone() => this;
            public override void RecycleClone() { }

            public override int GetSubmergedPathCostPenalty(PathFinder.PotentialPath path, NavGrid.Link link)
                => inner.GetSubmergedPathCostPenalty(path, link);

            public override bool TraversePath(ref PathFinder.PotentialPath path, int from_cell, NavType from_nav_type,
                int cost, int transition_id, bool submerged)
            {
                int to = path.cell;
                if (blocked.Count > 0 || removed.Count > 0)
                {
                    if (blocked.Contains(to)) return false;
                    if (!StandOk(to, path.navType, removed)) return false;
                    var tr = grid.transitions[transition_id];
                    if (blocked.Count > 0 && tr.voidOffsets != null)
                        foreach (var o in tr.voidOffsets)
                            if (blocked.Contains(Grid.OffsetCell(from_cell, o))) return false;
                    if (removed.Count > 0 && tr.solidOffsets != null)
                        foreach (var o in tr.solidOffsets)
                            if (removed.Contains(Grid.OffsetCell(from_cell, o))) return false;
                }
                return inner.TraversePath(ref path, from_cell, from_nav_type, cost, transition_id, submerged);
            }
        }

        private sealed class EscapeQuery : PathFinderQuery
        {
            public int origin, nodes, maxNodes, radius;

            public void Reset(int originCell, int maxN, int r)
            {
                origin = originCell;
                nodes = 0;
                maxNodes = maxN;
                radius = r;
            }

            public override bool IsMatch(int cell, int parent_cell, int cost)
            {
                nodes++;
                return nodes >= maxNodes || Grid.GetCellDistance(origin, cell) >= radius;
            }
        }

        private static readonly WhatIfAbilities whatIf = new WhatIfAbilities();
        private static readonly EscapeQuery query = new EscapeQuery();

        private struct Verdict { public bool safe; public int nodes; public float until; }
        private static readonly Dictionary<long, Verdict> cache = new Dictionary<long, Verdict>();
        private static readonly Dictionary<int, Verdict> baselineCache = new Dictionary<int, Verdict>();
        private static int budgetFrame = -1, budgetUsed;

        public static void Reset()
        {
            cache.Clear();
            baselineCache.Clear();
        }

        private static float Now => GameClock.Instance != null ? GameClock.Instance.GetTime() : Time.time;

        public static bool StandOk(int cell, NavType nav, HashSet<int> removed)
        {
            if (removed.Count == 0) return true;
            switch (nav)
            {
                case NavType.Floor: return !removed.Contains(Grid.CellBelow(cell));
                case NavType.Ladder:
                case NavType.Pole: return !removed.Contains(cell);
                default: return true;
            }
        }

        // ------------------------------------------------------------------ job analysis

        /// <summary>Is this a job that can change the terrain around the worker?</summary>
        public static bool IsRelevant(KMonoBehaviour target)
        {
            return target is Constructable || target is Deconstructable || target is Diggable;
        }

        /// <summary>Fills blocked/removed for the job. Returns false if the job cannot trap anybody.</summary>
        private static bool Describe(KMonoBehaviour target, int standCell, HashSet<int> blocked, HashSet<int> removed)
        {
            blocked.Clear();
            removed.Clear();
            switch (target)
            {
                case Constructable c:
                {
                    var b = c.GetComponent<Building>();
                    if (b == null || b.Def == null || !b.Def.IsFoundation) return false; // only tiles make cells solid
                    foreach (int cell in b.PlacementCells)
                        if (!Grid.Solid[cell]) blocked.Add(cell);
                    return blocked.Count > 0;
                }
                case Deconstructable d:
                {
                    var b = d.GetComponent<Building>();
                    if (b == null || b.Def == null) return false;
                    bool support = b.Def.IsFoundation || d.GetComponent<Ladder>() != null;
                    if (!support) return false;
                    foreach (int cell in b.PlacementCells) removed.Add(cell);
                    return true;
                }
                case Diggable g:
                {
                    // Digging only opens space. It is only a risk if the worker stands on the dug cell and falls.
                    int cell = Grid.PosToCell(g);
                    if (Grid.CellBelow(standCell) != cell) return false;
                    removed.Add(cell);
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// Would standing at <paramref name="standCell"/> to do <paramref name="target"/> leave the
        /// dupe trapped once the job is done? Cached per (job, cell) and shared between dupes.
        /// </summary>
        /// <param name="mustDecide">
        /// true when the dupe is committing to a spot (always simulate). false while merely ranking
        /// errands: then only a few new simulations are allowed per frame, and an unknown spot is
        /// treated as safe for now (the final check when the dupe picks its spot still applies).
        /// </param>
        public static bool IsSafeSpot(KMonoBehaviour target, int standCell, Navigator nav, bool mustDecide = true)
        {
            if (target == null || nav == null || !Grid.IsValidCell(standCell)) return true;
            var cfg = SafeConfig.Instance;
            long key = ((long)target.GetInstanceID() << 32) ^ (uint)standCell;
            float now = Now;
            if (cache.TryGetValue(key, out Verdict v) && v.until > now) return v.safe;

            if (!mustDecide)
            {
                int frame = Time.frameCount;
                if (frame != budgetFrame) { budgetFrame = frame; budgetUsed = 0; }
                if (budgetUsed >= cfg.EffectiveTrapChecksPerFrame) return true;
                budgetUsed++;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            bool safe = Evaluate(target, standCell, nav);
            SafeStats.Simulations++;
            SafeStats.SimulationMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (cache.Count > 50000) cache.Clear();
            cache[key] = new Verdict { safe = safe, until = now + cfg.CacheSeconds };
            return safe;
        }

        private static bool Evaluate(KMonoBehaviour target, int standCell, Navigator nav)
        {
            if (!Describe(target, standCell, whatIf.blocked, whatIf.removed)) return true;

            var cfg = SafeConfig.Instance;
            NavType standNav = StartNavType(nav.NavGrid, standCell);
            if (standNav == NavType.NumNavTypes) return true; // unknown way of standing: don't interfere
            int start = standCell;
            NavType startNav = standNav;

            // Losing the floor under your feet: fall to where you would land, continue from there.
            if (!StandOk(start, startNav, whatIf.removed))
            {
                start = FallTarget(standCell, whatIf.removed, nav.NavGrid, out startNav);
                if (start == Grid.InvalidCell) return false; // falls into the void or a very deep pit
            }

            bool escaped = Search(nav, start, startNav, true, out int nodes);
            if (escaped) return true;

            // Could the dupe get out from here even WITHOUT the job? (tiny early-game bases,
            // deliberately closed rooms). Then only object if the job makes it much worse.
            bool baseEscaped = BaselineEscapes(nav, standCell, standNav, out int baseNodes);
            return !baseEscaped && nodes >= baseNodes * 0.6f;
        }

        private static bool BaselineEscapes(Navigator nav, int cell, NavType navType, out int nodes)
        {
            float now = Now;
            if (baselineCache.TryGetValue(cell, out Verdict v) && v.until > now)
            {
                nodes = v.nodes;
                return v.safe;
            }
            bool ok = Search(nav, cell, navType, false, out nodes);
            if (baselineCache.Count > 50000) baselineCache.Clear();
            baselineCache[cell] = new Verdict { safe = ok, nodes = nodes, until = now + SafeConfig.Instance.CacheSeconds };
            return ok;
        }

        /// <summary>True if the dupe can get far away from <paramref name="cell"/> (optionally with the what-if changes).</summary>
        private static bool Search(Navigator nav, int cell, NavType navType, bool withChanges, out int nodes)
        {
            var cfg = SafeConfig.Instance;
            var inner = nav.GetCurrentAbilities();
            PathFinderAbilities abilities = inner;
            if (withChanges)
            {
                whatIf.inner = inner;
                whatIf.grid = nav.NavGrid;
                abilities = whatIf;
            }
            query.Reset(cell, cfg.EscapeNodes, cfg.EscapeRadius);
            PathFinder.Run(nav.NavGrid, abilities, new PathFinder.PotentialPath(cell, navType, nav.flags), query);
            nodes = query.nodes;
            return query.GetResultCell() != PathFinder.InvalidCell;
        }

        /// <summary>
        /// Can this dupe, from where it stands now, get far away? (used by the rescue monitor)
        /// </summary>
        public static bool CanEscapeFromHere(Navigator nav, out int nodes)
        {
            int cell = Grid.PosToCell(nav);
            return Search(nav, cell, nav.CurrentNavType, false, out nodes);
        }

        public static NavType StartNavType(NavGrid grid, int cell)
        {
            var table = grid.NavTable;
            if (table.IsValid(cell, NavType.Floor)) return NavType.Floor;
            if (table.IsValid(cell, NavType.Ladder)) return NavType.Ladder;
            if (table.IsValid(cell, NavType.Pole)) return NavType.Pole;
            return NavType.NumNavTypes;
        }

        /// <summary>Where a dupe ends up after its floor disappears (lands on solid ground or a ladder/pole).</summary>
        private static int FallTarget(int cell, HashSet<int> removed, NavGrid grid, out NavType landNav)
        {
            landNav = NavType.Floor;
            int c = cell;
            for (int i = 0; i < 64; i++)
            {
                int below = Grid.CellBelow(c);
                if (!Grid.IsValidCell(below)) return Grid.InvalidCell;
                if (Grid.Solid[below] && !removed.Contains(below)) return c;
                c = below;
                if (removed.Contains(c)) continue;
                if (grid.NavTable.IsValid(c, NavType.Ladder)) { landNav = NavType.Ladder; return c; }
                if (grid.NavTable.IsValid(c, NavType.Pole)) { landNav = NavType.Pole; return c; }
            }
            return Grid.InvalidCell;
        }
    }
}
