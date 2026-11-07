using System.Collections.Generic;
using UnityEngine;

namespace DupeTraffic
{
    /// <summary>
    /// The "Dupe Traffic" overlay. Cell colours are produced by <see cref="GetCellColour"/>,
    /// which SimDebugView calls on worker threads for every visible cell.
    /// </summary>
    public sealed class TrafficOverlay : OverlayModes.Mode
    {
        public static readonly HashedString ID = "DupeTraffic";

        public const string FILTER_CLIMB = "DUPETRAFFIC_CLIMB";
        public const string FILTER_WALK = "DUPETRAFFIC_WALK";

        public enum View { All, Climbing, Walking }

        /// <summary>Read from worker threads; only written on the main thread.</summary>
        public static volatile int CurrentView = (int)View.All;

        // Normalization: value that maps to full intensity (scaled units). Updated ~1x/sec.
        private static volatile float normalizer = 1f;
        private float nextNormalizeAt;
        private float[] scratch;

        // Colours
        private static readonly Color Cold = new Color(0.10f, 0.25f, 0.85f);
        private static readonly Color Mid1 = new Color(0.05f, 0.80f, 0.85f);
        private static readonly Color Mid2 = new Color(0.20f, 0.85f, 0.20f);
        private static readonly Color Mid3 = new Color(0.95f, 0.85f, 0.10f);
        private static readonly Color Hot = new Color(0.95f, 0.15f, 0.10f);
        private static readonly Color ClimbLow = new Color(0.35f, 0.15f, 0.55f);
        private static readonly Color ClimbHigh = new Color(1.00f, 0.25f, 0.95f);

        /// <summary>Below this fraction of the normalizer a cell is not drawn.</summary>
        private const float MinVisible = 0.02f;

        public TrafficOverlay()
        {
            legendFilters = CreateDefaultFilters();
        }

        public override HashedString ViewMode() => ID;

        public override string GetSoundName() => "Rooms";

        public override ToolParameterMenu.ToggleData[] CreateDefaultFilters()
        {
            return new[]
            {
                new ToolParameterMenu.ToggleData(ToolParameterMenu.FILTERLAYERS.ALL, ToolParameterMenu.ToggleState.On),
                new ToolParameterMenu.ToggleData(FILTER_CLIMB, ToolParameterMenu.ToggleState.Off),
                new ToolParameterMenu.ToggleData(FILTER_WALK, ToolParameterMenu.ToggleState.Off),
            };
        }

        public override void OnFiltersChanged()
        {
            var view = View.All;
            if (legendFilters != null)
            {
                foreach (var f in legendFilters)
                {
                    if (!f.IsOn) continue;
                    if (f.name == FILTER_CLIMB) view = View.Climbing;
                    else if (f.name == FILTER_WALK) view = View.Walking;
                }
            }
            CurrentView = (int)view;
            nextNormalizeAt = 0f; // recompute right away
        }

        public override void Enable()
        {
            base.Enable();
            nextNormalizeAt = 0f;
        }

        public override void Update()
        {
            base.Update();
            float now = Time.unscaledTime;
            if (now < nextNormalizeAt) return;
            nextNormalizeAt = now + 2f;
            if (!TrafficTracker.IsReady) return;
            if (scratch == null) scratch = new float[TrafficTracker.MaxSamples];
            // 95th percentile of visited cells -> full intensity. Keeps doors / bottlenecks
            // from washing out everything else, while they still show as max red.
            // On multi-core CPUs this runs on a thread-pool thread; on a single core inline.
            if (System.Environment.ProcessorCount > 1)
            {
                if (System.Threading.Interlocked.CompareExchange(ref normalizeRunning, 1, 0) != 0) return;
                var buf = scratch;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Normalize(buf); }
                    finally { System.Threading.Volatile.Write(ref normalizeRunning, 0); }
                });
            }
            else
            {
                Normalize(scratch);
            }
        }

        private static int normalizeRunning;

        private static void Normalize(float[] buf)
        {
            float p = TrafficTracker.Percentile(0.95f, buf, out _);
            normalizer = p > 0f ? p : 1f;
        }

        public override List<LegendEntry> GetCustomLegendData()
        {
            return new List<LegendEntry>
            {
                new LegendEntry("Light traffic", "Cells dupes rarely pass through.", Cold),
                new LegendEntry("Moderate traffic", null, Mid2),
                new LegendEntry("Heavy traffic", "Main corridors. Consider better floor tiles\n(metal/plastic tile give a walking speed bonus)\nor a transit tube for long, busy routes.", Hot),
                new LegendEntry("Busy ladder / pole", "Most traffic here is climbing.\nPlastic ladders are faster than normal ladders;\nfire poles are much faster going down.", ClimbHigh),
            };
        }

        /// <summary>Colour callback for SimDebugView. Runs on worker threads.</summary>
        public static Color GetCellColour(SimDebugView instance, int cell)
        {
            float raw = TrafficTracker.GetScaled(cell);
            if (raw <= 0f) return Color.black;

            float climbFrac = TrafficTracker.GetClimbFraction(cell);
            var view = (View)CurrentView;
            float value;
            switch (view)
            {
                case View.Climbing: value = raw * climbFrac; break;
                case View.Walking: value = raw * (1f - climbFrac); break;
                default: value = raw; break;
            }

            float norm = normalizer;
            float rel = value / norm;
            if (rel < MinVisible) return Color.black;

            // Log scale: traffic spans orders of magnitude; linear would show only the corridors.
            float t = Mathf.Clamp01(Mathf.Log(1f + 9f * Mathf.Min(rel, 1f)) / Mathf.Log(10f));

            bool climbing = view == View.Climbing || (view == View.All && climbFrac > 0.5f);
            Color col = climbing ? Color.Lerp(ClimbLow, ClimbHigh, t) : Gradient(t);
            // Translucent so the base stays visible; busier cells are a bit more opaque.
            var cfg = TrafficConfig.Instance;
            col.a = Mathf.Lerp(cfg.MinOpacity, cfg.MaxOpacity, t);
            return col;
        }

        private static Color Gradient(float t)
        {
            if (t < 0.25f) return Color.Lerp(Cold, Mid1, t / 0.25f);
            if (t < 0.50f) return Color.Lerp(Mid1, Mid2, (t - 0.25f) / 0.25f);
            if (t < 0.75f) return Color.Lerp(Mid2, Mid3, (t - 0.50f) / 0.25f);
            return Color.Lerp(Mid3, Hot, (t - 0.75f) / 0.25f);
        }

        public static void RegisterStrings()
        {
            Strings.Add("STRINGS.UI.TOOLS.FILTERLAYERS." + FILTER_CLIMB + ".NAME", "Climbing");
            Strings.Add("STRINGS.UI.TOOLS.FILTERLAYERS." + FILTER_CLIMB + ".TOOLTIP", "Show only ladder and fire pole traffic");
            Strings.Add("STRINGS.UI.TOOLS.FILTERLAYERS." + FILTER_WALK + ".NAME", "Walking");
            Strings.Add("STRINGS.UI.TOOLS.FILTERLAYERS." + FILTER_WALK + ".TOOLTIP", "Show only floor / tube traffic (no climbing)");
        }
    }
}
