using System;
using System.Text;
using HarmonyLib;
using Klei.AI;
using UnityEngine;

namespace SafeDupes
{
    /// <summary>
    /// Writes one line to Player.log for every duplicant death: the cause the game assigns, where it
    /// happened (world, cell, gas/liquid and temperature at head height), the dupe's last breath,
    /// health, calories and stress, the errand it was doing, and whether Safe Dupes saw it as trapped.
    /// Only reads state; never changes anything.
    /// </summary>
    [HarmonyPatch(typeof(DeathMonitor.Instance), nameof(DeathMonitor.Instance.Kill))]
    internal static class DeathReport
    {
        public static int Deaths;

        internal static void Prefix(DeathMonitor.Instance __instance, Death death)
        {
            try
            {
                if (__instance == null || !__instance.IsDuplicant || __instance.IsDead()) return;
                var go = __instance.gameObject;
                if (go == null) return;
                Deaths++;
                Debug.Log("[SafeDupes] DEATH " + Describe(go, death));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SafeDupes] death report failed: " + e.Message);
            }
        }

        private static string Describe(GameObject go, Death death)
        {
            var sb = new StringBuilder(256);
            sb.Append(go.GetProperName()).Append(": cause=").Append(death != null ? death.Id : "unknown");

            int cell = Grid.PosToCell(go);
            if (Grid.IsValidCell(cell))
            {
                Grid.CellToXY(cell, out int x, out int y);
                var world = ClusterManager.Instance != null ? ClusterManager.Instance.GetWorld(Grid.WorldIdx[cell]) : null;
                sb.Append(" | at ").Append(world != null ? world.GetProperName() : "?").Append(" (").Append(x).Append(',').Append(y).Append(')');
                int head = Grid.CellAbove(cell);
                if (Grid.IsValidCell(head))
                {
                    var el = Grid.Element[head];
                    sb.Append(", head cell ").Append(el != null ? el.id.ToString() : "?")
                      .Append(' ').Append((Grid.Mass[head] * 1000f).ToString("0")).Append(" g")
                      .Append(' ').Append((Grid.Temperature[head] - 273.15f).ToString("0.0")).Append(" C");
                }
            }

            var amounts = Db.Get().Amounts;
            AppendAmount(sb, go, amounts.Breath, "breath");
            AppendAmount(sb, go, amounts.BionicOxygenTank, "O2 tank");
            AppendAmount(sb, go, amounts.HitPoints, "health");
            AppendAmount(sb, go, amounts.Calories, "calories");
            AppendAmount(sb, go, amounts.Stress, "stress");

            var driver = go.GetComponent<ChoreDriver>();
            var chore = driver != null ? driver.GetCurrentChore() : null;
            sb.Append(" | errand=").Append(chore != null && chore.choreType != null ? chore.choreType.Id : "none");

            var id = go.GetComponent<MinionIdentity>();
            var nav = go.GetComponent<Navigator>();
            bool flagged = id != null && RescueMonitor.Flagged.Contains(id);
            sb.Append(" | SafeDupes: flagged trapped=").Append(flagged ? "yes" : "no");
            if (id != null && nav != null)
            {
                bool trappedNow = RescueMonitor.LooksTrapped(id, nav, out int nodes);
                sb.Append(", trapped now=").Append(trappedNow ? "yes" : "no").Append(" (reachable positions ").Append(nodes).Append(')');
            }
            if (id != null) RescueMonitor.Flagged.Remove(id);
            return sb.ToString();
        }

        private static void AppendAmount(StringBuilder sb, GameObject go, Amount amount, string label)
        {
            if (amount == null) return;
            AmountInstance inst = amount.Lookup(go);
            if (inst == null) return;
            float max = inst.GetMax();
            sb.Append(" | ").Append(label).Append(' ');
            if (max > 0f) sb.Append((100f * inst.value / max).ToString("0")).Append('%');
            else sb.Append(inst.value.ToString("0.#"));
        }
    }
}
