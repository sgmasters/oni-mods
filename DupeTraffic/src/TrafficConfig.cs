using System;
using System.IO;
using Newtonsoft.Json;
using PeterHan.PLib.Options;

namespace DupeTraffic
{
    /// <summary>Settings, stored as config.json next to the mod DLL.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    [ConfigFile("config.json", true)]
    [RestartRequired]
    public sealed class TrafficConfig
    {
        public static TrafficConfig Instance { get; private set; } = new TrafficConfig();

        /// <summary>Opacity of the faintest traffic colour (0 = invisible, 1 = solid).</summary>
        [Option("Faintest colour opacity", "Opacity of rarely used cells (0 = invisible, 1 = solid).", "Heatmap", Format = "F2")]
        [Limit(0, 1)]
        [JsonProperty] public float MinOpacity { get; set; } = 0.20f;

        /// <summary>Opacity of the busiest cells.</summary>
        [Option("Busiest colour opacity", "Opacity of the busiest cells (0 = invisible, 1 = solid).", "Heatmap", Format = "F2")]
        [Limit(0, 1)]
        [JsonProperty] public float MaxOpacity { get; set; } = 0.55f;

        /// <summary>Traffic memory half-life in cycles (older steps fade out).</summary>
        [Option("Traffic memory (cycles)", "Half-life of recorded steps. Smaller = the map reacts faster to new routes.", "Heatmap", Format = "F1")]
        [Limit(0.1, 20)]
        [JsonProperty] public float HalfLifeCycles { get; set; } = 3f;

        public static void Load(string modPath)
        {
            string file = Path.Combine(modPath ?? ".", "config.json");
            try
            {
                if (File.Exists(file))
                {
                    var loaded = JsonConvert.DeserializeObject<TrafficConfig>(File.ReadAllText(file));
                    if (loaded != null) Instance = loaded;
                }
                Instance.MinOpacity = Clamp01(Instance.MinOpacity);
                Instance.MaxOpacity = Math.Max(Instance.MinOpacity, Clamp01(Instance.MaxOpacity));
                Instance.HalfLifeCycles = Math.Max(0.1f, Instance.HalfLifeCycles);
                File.WriteAllText(file, JsonConvert.SerializeObject(Instance, Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DupeTraffic] config.json could not be read, using defaults: " + e.Message);
                Instance = new TrafficConfig();
            }
            TrafficTracker.HalfLifeSeconds = Instance.HalfLifeCycles * 600f;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
