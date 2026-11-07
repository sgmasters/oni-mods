using System;
using HarmonyLib;
using KMod;
using UnityEngine;

namespace SafeDupes
{
    public sealed class SafeDupesMod : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            SafeConfig.Load(path);
            base.OnLoad(harmony);
            // "Options" button for this mod in the Mods menu (PLib).
            PeterHan.PLib.Core.PUtil.InitLibrary(false);
            new PeterHan.PLib.Options.POptions().RegisterOptions(this, typeof(SafeConfig));
            var c = SafeConfig.Instance;
            Debug.LogFormat("[SafeDupes] loaded. trapCheck={0} rescue={1} healthGate={2} (low<{3:P0}, critical<{4:P0})",
                c.PreventSelfTrapping, c.AutoRescueTrapped, c.HealthGate, c.LowHealthFraction, c.CriticalHealthFraction);
        }
    }

    /// <summary>
    /// Wrap the "CanMoveTo" errand precondition. Every work errand (dig, build, deconstruct,
    /// supply, operate...) has it, it runs on the main thread, and it receives the work target.
    ///  - Trap check: only work spots that stay escapable count; the errand cost uses the best of those.
    ///    If all reachable spots are traps, this dupe skips the errand (someone on the other side will do it).
    ///  - Health gate: hurt dupes skip errands at dangerous spots.
    /// Chores copy the precondition when they are created, so we wrap it right after the
    /// ChorePreconditions singleton is built.
    /// </summary>
    [HarmonyPatch(typeof(ChorePreconditions), MethodType.Constructor)]
    internal static class ChorePreconditions_Ctor_Patch
    {
        internal static void Postfix(ChorePreconditions __instance)
        {
            var original = __instance.CanMoveTo.fn;
            if (original == null) return;
            __instance.CanMoveTo.fn = (ref Chore.Precondition.Context context, object data) =>
            {
                if (!original(ref context, data)) return false;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    var target = data as KMonoBehaviour;
                    if (target == null) return true;
                    if (SafeConfig.Instance.PreventSelfTrapping && TrapSim.IsRelevant(target))
                    {
                        var nav = context.consumerState.navigator;
                        if (nav != null && !SafeSpots.AdjustCost(ref context, target, nav))
                        {
                            SafeStats.ErrandsSkippedTrap++;
                            return false;
                        }
                    }
                    if (HealthGate.Refuse(ref context, target))
                    {
                        SafeStats.HealthRefusals++;
                        return false;
                    }
                }
                catch (Exception e)
                {
                    SafeSpots.ReportOnce(e);
                }
                finally
                {
                    SafeStats.CheckCalls++;
                    SafeStats.CheckTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                }
                return true;
            };
        }
    }

    /// <summary>Counters written to Player.log once a minute so you can see the mod working.</summary>
    internal static class SafeStats
    {
        public static int ErrandsSkippedTrap, HealthRefusals, SpotsAvoided, CommitsBlocked, Simulations, Rescues, TrappedNoFix;
        public static double SimulationMs;
        public static long CheckTicks;
        public static int CheckCalls;
        private static float nextLogAt = -1f;

        public static void MaybeLog()
        {
            float now = Time.unscaledTime;
            if (nextLogAt < 0f) { nextLogAt = now + 60f; return; }
            if (now < nextLogAt) return;
            nextLogAt = now + 60f;
            Debug.LogFormat("[SafeDupes] last minute: trap spots avoided={0}, errand checks refused as traps={1}, " +
                            "walks cancelled (only trap spots left)={2}, health refusals={3}, rescues ordered={4}, " +
                            "trapped without fix={5} | trap simulations={6} ({7:0.00}ms avg) | errand checks={8}, total cost {9:0.0}ms/min",
                SpotsAvoided, ErrandsSkippedTrap, CommitsBlocked, HealthRefusals, Rescues, TrappedNoFix,
                Simulations, Simulations > 0 ? SimulationMs / Simulations : 0.0,
                CheckCalls, CheckTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            ErrandsSkippedTrap = HealthRefusals = SpotsAvoided = CommitsBlocked = Simulations = Rescues = TrappedNoFix = 0;
            SimulationMs = 0;
            CheckTicks = 0;
            CheckCalls = 0;
        }
    }

    internal static class SafeSpots
    {
        private static bool reported;

        public static void ReportOnce(Exception e)
        {
            if (reported) return;
            reported = true;
            Debug.LogWarning("[SafeDupes] error in safety check (further errors hidden): " + e);
        }

        /// <summary>
        /// Recompute the move cost using only safe work spots. Returns false if no reachable spot is safe.
        /// </summary>
        public static bool AdjustCost(ref Chore.Precondition.Context context, KMonoBehaviour target, Navigator nav)
        {
            var approachable = target as IApproachable;
            if (approachable == null) return true;
            int root = approachable.GetCell();
            CellOffset[] offsets = approachable.GetOffsets();
            int bestAll = -1, bestSafe = -1;
            for (int i = 0; i < offsets.Length; i++)
            {
                int cell = Grid.OffsetCell(root, offsets[i]);
                int cost = nav.GetNavigationCost(cell);
                if (cost == -1) continue;
                if (bestAll == -1 || cost < bestAll) bestAll = cost;
                if ((bestSafe == -1 || cost < bestSafe) && TrapSim.IsSafeSpot(target, cell, nav, mustDecide: false)) bestSafe = cost;
            }
            if (bestAll == -1) return true; // vanilla already decided; nothing to adjust
            if (bestSafe == -1) return false;
            context.cost += bestSafe - bestAll;
            return true;
        }
    }

    /// <summary>When the dupe actually picks where to stand, skip spots that would trap it.</summary>
    [HarmonyPatch(typeof(NavTactic), nameof(NavTactic.GetCellPreferences))]
    internal static class NavTactic_GetCellPreferences_Patch
    {
        private static bool[] keepBuf = new bool[16];
        private static readonly System.Collections.Generic.Dictionary<int, CellOffset[]> filteredBySize =
            new System.Collections.Generic.Dictionary<int, CellOffset[]>();

        internal static void Prefix(int root, ref CellOffset[] offsets, Navigator navigator)
        {
            if (!SafeConfig.Instance.PreventSelfTrapping || navigator == null || offsets == null) return;
            try
            {
                var target = navigator.target;
                if (target == null || !TrapSim.IsRelevant(target)) return;
                int safeCount = 0;
                if (keepBuf.Length < offsets.Length) keepBuf = new bool[offsets.Length * 2];
                var keep = keepBuf;
                for (int i = 0; i < offsets.Length; i++)
                {
                    int cell = Grid.OffsetCell(root, offsets[i]);
                    keep[i] = !navigator.CanReach(cell) || TrapSim.IsSafeSpot(target, cell, navigator);
                    if (keep[i]) safeCount++;
                }
                if (safeCount == offsets.Length) return;
                SafeStats.SpotsAvoided += offsets.Length - safeCount;
                if (safeCount == 0) SafeStats.CommitsBlocked++;
                // The array is only read during this call, so one cached array per size is enough.
                if (!filteredBySize.TryGetValue(safeCount, out var filtered))
                    filteredBySize[safeCount] = filtered = new CellOffset[safeCount];
                for (int i = 0, j = 0; i < offsets.Length; i++)
                    if (keep[i]) filtered[j++] = offsets[i];
                offsets = filtered; // may be empty -> no reservation -> the errand fails and is re-evaluated
            }
            catch (Exception e)
            {
                SafeSpots.ReportOnce(e);
            }
        }
    }

    [HarmonyPatch(typeof(Game), "OnSpawn")]
    internal static class Game_OnSpawn_Patch
    {
        internal static void Postfix(Game __instance)
        {
            TrapSim.Reset();
            HealthGate.Reset();
            __instance.gameObject.AddOrGet<RescueMonitor>();
        }
    }
}
