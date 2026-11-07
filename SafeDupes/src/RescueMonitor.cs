using System.Collections.Generic;
using UnityEngine;

namespace SafeDupes
{
    /// <summary>
    /// Finds dupes that are already trapped and orders the cheapest way out:
    /// a top-priority dig (natural tile) or deconstruct (built tile) of the thinnest wall
    /// between the dupe's pocket and space other dupes can reach. Also posts a notification.
    /// </summary>
    public sealed class RescueMonitor : KMonoBehaviour
    {
        private sealed class State
        {
            public int trappedChecks;
            public float lastRescueAt = float.NegativeInfinity;
            public bool notifiedNoFix;
        }

        private readonly Dictionary<MinionIdentity, State> states = new Dictionary<MinionIdentity, State>();

        /// <summary>Dupes the monitor currently considers trapped (confirmed). Read by the death report.</summary>
        internal static readonly HashSet<MinionIdentity> Flagged = new HashSet<MinionIdentity>();

        /// <summary>Same test the monitor uses: small pocket AND cut off from the colony.</summary>
        internal static bool LooksTrapped(MinionIdentity id, Navigator nav, out int nodes)
        {
            nodes = 0;
            int here = Grid.PosToCell(nav);
            if (!Grid.IsValidCell(here)) return false;
            return !TrapSim.CanEscapeFromHere(nav, out nodes) && !CanReachColony(id, nav, here);
        }
        private float nextCheckAt;
        private int cursor;

        private static float Now => GameClock.Instance != null ? GameClock.Instance.GetTime() : Time.time;

        private void Update()
        {
            SafeStats.MaybeLog();
            var cfg = SafeConfig.Instance;
            if (!cfg.AutoRescueTrapped || SpeedControlScreen.Instance == null || SpeedControlScreen.Instance.IsPaused) return;
            float now = Now;
            if (now < nextCheckAt) return;

            // Spread the work: check a slice of the colony each time, the full colony every RescueCheckSeconds.
            var all = Components.LiveMinionIdentities.Items;
            int count = all.Count;
            if (count == 0) { nextCheckAt = now + cfg.RescueCheckSeconds; return; }
            int slice = Mathf.Max(1, Mathf.CeilToInt(count / 10f));
            for (int i = 0; i < slice; i++)
            {
                if (cursor >= count) cursor = 0;
                var id = all[cursor++];
                if (id != null) Check(id, now, cfg);
            }
            nextCheckAt = now + cfg.RescueCheckSeconds / 10f;
        }

        private void Check(MinionIdentity id, float now, SafeConfig cfg)
        {
            var nav = id.GetComponent<Navigator>();
            if (nav == null || nav.IsMoving()) return;
            int here = Grid.PosToCell(nav);
            if (!Grid.IsValidCell(here)) return;
            if (nav.CurrentNavType == NavType.Tube || nav.CurrentNavType == NavType.Teleport) return;
            var world = ClusterManager.Instance != null ? ClusterManager.Instance.GetWorld(Grid.WorldIdx[here]) : null;
            if (world != null && world.IsModuleInterior) return; // rocket interiors are small on purpose
            if (!states.TryGetValue(id, out State st)) states[id] = st = new State();

            // Trapped = stuck in a small pocket AND cut off from the rest of the colony.
            // (A small pocket alone is normal for a brand-new colony.)
            if (TrapSim.CanEscapeFromHere(nav, out _) || CanReachColony(id, nav, here))
            {
                st.trappedChecks = 0;
                st.notifiedNoFix = false;
                Flagged.Remove(id);
                return;
            }
            if (++st.trappedChecks < cfg.RescueConfirmChecks) return;
            Flagged.Add(id);
            if (now - st.lastRescueAt < cfg.RescueCooldownSeconds) return;

            bool fixedIt = TryOrderEscape(nav, cfg, out string what);
            // Either way, wait for the cooldown before searching again (the search is not free).
            st.lastRescueAt = now;
            st.trappedChecks = 0;
            if (fixedIt)
            {
                SafeStats.Rescues++;
                Notify(id, id.GetProperName() + " was trapped", what);
                Debug.Log("[SafeDupes] " + id.GetProperName() + " trapped: " + what);
            }
            else if (!st.notifiedNoFix)
            {
                st.notifiedNoFix = true;
                SafeStats.TrappedNoFix++;
                Notify(id, id.GetProperName() + " is trapped",
                    "Safe Dupes could not find a wall to dig through. They may need a ladder or a fire pole to climb out.");
            }
        }

        /// <summary>
        /// Can the dupe reach the Printing Pod of its world, or (no pod) any other dupe on its world?
        /// Uses the dupe's own path grid, so this is just a lookup.
        /// </summary>
        private static bool CanReachColony(MinionIdentity self, Navigator nav, int here)
        {
            int world = Grid.WorldIdx[here];
            foreach (var pod in Components.Telepads.Items)
            {
                if (pod == null) continue;
                int c = Grid.PosToCell(pod);
                if (Grid.IsValidCell(c) && Grid.WorldIdx[c] == world)
                    return nav.CanReach(c) || nav.CanReach(Grid.CellAbove(c));
            }
            int others = 0;
            foreach (var other in Components.LiveMinionIdentities.Items)
            {
                if (other == null || other == self) continue;
                int c = Grid.PosToCell(other);
                if (!Grid.IsValidCell(c) || Grid.WorldIdx[c] != world) continue;
                others++;
                if (nav.CanReach(c)) return true;
                if (others >= 8) break;
            }
            return others == 0; // alone on this world: nothing to compare against, don't act
        }

        // ------------------------------------------------------------------ escape planning

        private static readonly CellOffset[] Dirs =
        {
            new CellOffset(1, 0), new CellOffset(-1, 0), new CellOffset(0, 1), new CellOffset(0, -1)
        };

        // Reused between calls (main thread only) to avoid garbage.
        private static readonly HashSet<int> pocketScratch = new HashSet<int>();
        private static readonly Queue<int> queueScratch = new Queue<int>();
        private static readonly int[] wallScratch = new int[8];
        private static readonly int[] bestScratch = new int[8];

        private static bool TryOrderEscape(Navigator nav, SafeConfig cfg, out string what)
        {
            what = null;
            int origin = Grid.PosToCell(nav);

            // 1. The pocket: open cells connected to the dupe (4-neighbour, ignoring gravity).
            var pocket = pocketScratch;
            var queue = queueScratch;
            pocket.Clear();
            queue.Clear();
            pocket.Add(origin);
            queue.Enqueue(origin);
            const int maxPocket = 600;
            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                foreach (var d in Dirs)
                {
                    int n = Grid.OffsetCell(c, d);
                    if (!Grid.IsValidCell(n) || pocket.Contains(n) || Grid.Solid[n]) continue;
                    if (!Grid.IsWorldValidCell(n) || Grid.WorldIdx[n] != Grid.WorldIdx[origin]) continue;
                    pocket.Add(n);
                    if (pocket.Count > maxPocket)
                        return false; // big open area: the problem is climbing (pit), not a wall
                    queue.Enqueue(n);
                }
            }

            // 2. Candidate walls: from a pocket cell, go through 1..N solid cells and reach open
            //    space outside the pocket that some dupe can reach.
            int bestScore = int.MaxValue;
            int bestCount = 0;
            int originRow = Grid.CellRow(origin);
            var prober = MinionGroupProber.Get();
            foreach (int p in pocket)
            {
                foreach (var d in Dirs)
                {
                    int wallCount = 0;
                    int c = p;
                    bool ok = false;
                    for (int k = 0; k <= cfg.RescueMaxWallThickness; k++)
                    {
                        c = Grid.OffsetCell(c, d);
                        if (!Grid.IsValidCell(c) || Grid.WorldIdx[c] != Grid.WorldIdx[origin]) break;
                        if (Grid.Solid[c])
                        {
                            if (k == cfg.RescueMaxWallThickness || !CanRemove(c)) break;
                            wallScratch[wallCount++] = c;
                            continue;
                        }
                        ok = wallCount > 0 && !pocket.Contains(c) && prober.IsReachable(c) && SafeToOpen(c);
                        break;
                    }
                    if (!ok) continue;
                    // Prefer: thin walls, close to the dupe, at foot level, sideways (walkable).
                    int score = wallCount * 100
                                + Grid.GetCellDistance(origin, p) * 4
                                + Mathf.Abs(Grid.CellRow(p) - originRow) * 6
                                + (d.y != 0 ? 20 : 0);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestCount = wallCount;
                        System.Array.Copy(wallScratch, bestScratch, wallCount);
                    }
                }
            }
            if (bestCount == 0) return false;

            int digs = 0, decons = 0;
            for (int bi = 0; bi < bestCount; bi++)
            {
                int cell = bestScratch[bi];
                if (OrderOpen(cell, out bool dig))
                {
                    if (dig) digs++; else decons++;
                }
                else return false;
            }
            what = "Ordered " + (digs > 0 ? digs + " dig" : "") + (digs > 0 && decons > 0 ? " + " : "") +
                   (decons > 0 ? decons + " deconstruct" : "") + " at top priority to open a way out.";
            return true;
        }

        /// <summary>Can this solid cell be removed by dig or deconstruct?</summary>
        private static bool CanRemove(int cell)
        {
            if (Grid.Foundation[cell])
                return Grid.Objects[cell, (int)ObjectLayer.FoundationTile] is GameObject go && go != null &&
                       go.GetComponent<Deconstructable>() != null;
            // Closed doors and other buildings also read as solid: never dig those.
            if (Grid.Objects[cell, (int)ObjectLayer.Building] != null) return false;
            var e = Grid.Element[cell];
            return e != null && e.hardness < 255;
        }

        /// <summary>Don't open into liquid, vacuum or scalding gas.</summary>
        private static bool SafeToOpen(int cell)
        {
            var e = Grid.Element[cell];
            if (e == null || e.IsLiquid) return false;
            if (e.id == SimHashes.Vacuum || Grid.Mass[cell] < 0.05f) return false;
            return Grid.Temperature[cell] < 330f && Grid.Temperature[cell] > 255f;
        }

        private static bool OrderOpen(int cell, out bool dig)
        {
            var top = new PrioritySetting(PriorityScreen.PriorityClass.topPriority, 1);
            dig = !Grid.Foundation[cell];
            if (!dig)
            {
                var go = Grid.Objects[cell, (int)ObjectLayer.FoundationTile];
                var d = go != null ? go.GetComponent<Deconstructable>() : null;
                if (d == null) return false;
                if (!d.IsMarkedForDeconstruction()) d.QueueDeconstruction(true);
                go.GetComponent<Prioritizable>()?.SetMasterPriority(top);
                return true;
            }
            var existing = Grid.Objects[cell, (int)ObjectLayer.DigPlacer];
            if (existing == null)
            {
                existing = Util.KInstantiate(Assets.GetPrefab(new Tag("DigPlacer")));
                var digComp = existing.GetComponent<global::Diggable>();
                if (digComp != null) digComp.digTypeFlags = 1;
                existing.SetActive(true);
                Grid.Objects[cell, (int)ObjectLayer.DigPlacer] = existing;
                existing.transform.SetPosition(Grid.CellToPosCBC(cell, Grid.SceneLayer.FXFront));
            }
            existing.GetComponent<Prioritizable>()?.SetMasterPriority(top);
            return true;
        }

        private static void Notify(MinionIdentity id, string title, string body)
        {
            var notifier = id.gameObject.AddOrGet<Notifier>();
            notifier.Add(new Notification(title, NotificationType.BadMinor,
                (list, data) => body, null, expires: true, delay: 0f,
                custom_click_callback: null, custom_click_data: null, click_focus: id.transform));
        }
    }
}
