using System;
using UnityEngine;

namespace DupeTraffic
{
    /// <summary>
    /// Per-cell record of how often duplicants step through each cell.
    ///
    /// Values decay exponentially (half-life in game seconds) so the map reflects
    /// recent habits, not the whole history of the colony. To keep each step O(1),
    /// decay is applied lazily: every new step is added with weight e^(k*t), so
    /// relative values are always correct and no periodic pass over the grid is needed.
    /// When the weight grows large, everything is rescaled once.
    /// </summary>
    public static class TrafficTracker
    {
        /// <summary>Half-life of the traffic memory in game seconds (3 cycles).</summary>
        public static float HalfLifeSeconds = 3f * 600f;

        private const float RescaleThreshold = 1e6f;

        private static float[] total;   // all steps (scaled units)
        private static float[] climb;   // steps made on ladders / fire poles (scaled units)
        private static int cellCount;

        private static double baseTime;   // game time the scale is relative to
        private static float maxValue;    // highest value in scaled units (for normalization fallback)

        public static bool IsReady => total != null && cellCount == Grid.CellCount;

        public static void Reset()
        {
            cellCount = Grid.CellCount;
            total = new float[cellCount];
            climb = new float[cellCount];
            baseTime = CurrentTime();
            maxValue = 0f;
        }

        private static double CurrentTime()
        {
            return GameClock.Instance != null ? GameClock.Instance.GetTime() : 0.0;
        }

        private static float DecayRate => Mathf.Log(2f) / Mathf.Max(60f, HalfLifeSeconds);

        /// <summary>Current weight for a new step, relative to the scaled units.</summary>
        private static float CurrentWeight()
        {
            double now = CurrentTime();
            float w = (float)Math.Exp(DecayRate * (now - baseTime));
            if (w > RescaleThreshold || float.IsInfinity(w))
            {
                Rescale(now);
                w = 1f;
            }
            return w;
        }

        private static void Rescale(double now)
        {
            float factor = (float)Math.Exp(-DecayRate * (now - baseTime));
            for (int i = 0; i < cellCount; i++)
            {
                total[i] *= factor;
                climb[i] *= factor;
            }
            maxValue *= factor;
            baseTime = now;
        }

        public static void RecordStep(int cell, NavType navType)
        {
            if (!IsReady)
            {
                if (Grid.CellCount <= 0) return;
                Reset();
            }
            if (cell < 0 || cell >= cellCount) return;

            float w = CurrentWeight();
            float v = total[cell] + w;
            total[cell] = v;
            if (navType == NavType.Ladder || navType == NavType.Pole)
                climb[cell] += w;
            if (v > maxValue) maxValue = v;
        }

        /// <summary>Raw (scaled) traffic value; only meaningful relative to other cells.</summary>
        public static float GetScaled(int cell)
        {
            return IsReady && cell >= 0 && cell < cellCount ? total[cell] : 0f;
        }

        /// <summary>Fraction of traffic in this cell that was climbing (ladder / pole).</summary>
        public static float GetClimbFraction(int cell)
        {
            if (!IsReady || cell < 0 || cell >= cellCount) return 0f;
            float t = total[cell];
            return t > 0f ? climb[cell] / t : 0f;
        }

        /// <summary>Approximate number of passes in the recent window (decayed count).</summary>
        public static float GetRecentPasses(int cell)
        {
            if (!IsReady || cell < 0 || cell >= cellCount) return 0f;
            return total[cell] * (float)Math.Exp(-DecayRate * (CurrentTime() - baseTime));
        }

        public static float MaxScaled => maxValue;

        /// <summary>
        /// Value at the given percentile of non-zero cells (scaled units). Used to
        /// normalize colours so a single door cell does not wash out the whole map.
        /// </summary>
        /// <summary>Upper bound on values sorted for the percentile (keeps it cheap on huge maps).</summary>
        public const int MaxSamples = 4096;

        /// <remarks>
        /// Safe to call from a worker thread: it takes a snapshot of the array reference and only reads.
        /// Values written by the main thread meanwhile just make the sample a little fresher.
        /// </remarks>
        public static float Percentile(float p, float[] scratch, out int nonZero)
        {
            nonZero = 0;
            float[] data = total;
            if (data == null) return 0f;
            int len = data.Length;
            for (int i = 0; i < len; i++)
                if (data[i] > 0f) nonZero++;
            if (nonZero == 0) return 0f;
            // Evenly spaced sample of the visited cells.
            int stride = Math.Max(1, (nonZero + scratch.Length - 1) / scratch.Length);
            int n = 0, seen = 0;
            for (int i = 0; i < len && n < scratch.Length; i++)
            {
                float v = data[i];
                if (v <= 0f) continue;
                if (seen++ % stride == 0) scratch[n++] = v;
            }
            if (n == 0) return 0f;
            Array.Sort(scratch, 0, n);
            int idx = Math.Max(0, Math.Min(n - 1, (int)Math.Round(p * (n - 1))));
            return scratch[idx];
        }
    }
}
