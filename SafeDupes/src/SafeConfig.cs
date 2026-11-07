using System;
using System.IO;
using Newtonsoft.Json;
using PeterHan.PLib.Options;

namespace SafeDupes
{
    /// <summary>Settings, stored as config.json next to the mod DLL.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    [ConfigFile("config.json", true)]
    [RestartRequired]
    public sealed class SafeConfig
    {
        public static SafeConfig Instance { get; private set; } = new SafeConfig();

        // ---- 1. Don't trap yourself (build / deconstruct / dig) ----
        [Option("Prevent self-trapping", "Skip build / dig / deconstruct spots that would leave the dupe with no way out.", "1. Self-trapping")]
        [JsonProperty] public bool PreventSelfTrapping { get; set; } = true;

        /// <summary>From the work spot, the dupe must still be able to walk this many cells away...</summary>
        [Option("Escape distance (cells)", "From the work spot the dupe must still be able to walk this far away...", "1. Self-trapping")]
        [Limit(3, 64)]
        [JsonProperty] public int EscapeRadius { get; set; } = 12;

        /// <summary>...or reach this many distinct positions. Otherwise the spot counts as a trap.</summary>
        [Option("Escape area (positions)", "...or reach this many different positions. Otherwise the spot counts as a trap.", "1. Self-trapping")]
        [Limit(20, 5000)]
        [JsonProperty] public int EscapeNodes { get; set; } = 160;

        /// <summary>How long a safety verdict for (job, work spot) is reused, in game seconds.</summary>
        [Option("Verdict cache (game s)", "How long a safety verdict for an errand spot is reused.", "1. Self-trapping", Format = "F1")]
        [Limit(0.5, 60)]
        [JsonProperty] public float CacheSeconds { get; set; } = 8f;

        /// <summary>
        /// New trap simulations allowed per frame while dupes rank errands (each is ~0.01-1 ms).
        /// 0 = auto from CPU cores (1-2 cores: 2, 3-4 cores: 4, more: 6).
        /// </summary>
        [Option("Trap checks per frame", "New trap simulations per frame. 0 = auto from CPU cores (1-2: 2, 3-4: 4, more: 6).", "1. Self-trapping")]
        [Limit(0, 32)]
        [JsonProperty] public int MaxTrapChecksPerFrame { get; set; } = 0;

        public int EffectiveTrapChecksPerFrame
        {
            get
            {
                if (MaxTrapChecksPerFrame > 0) return MaxTrapChecksPerFrame;
                int cores = Environment.ProcessorCount;
                return cores <= 2 ? 2 : cores <= 4 ? 4 : 6;
            }
        }

        // ---- 2. Rescue trapped dupes ----
        [Option("Rescue trapped dupes", "Order digging / deconstruction to free dupes that are stuck.", "2. Rescue")]
        [JsonProperty] public bool AutoRescueTrapped { get; set; } = true;

        /// <summary>How often trapped-ness is checked, in game seconds.</summary>
        [Option("Check every (game s)", "How often trapped dupes are looked for.", "2. Rescue", Format = "F0")]
        [Limit(1, 120)]
        [JsonProperty] public float RescueCheckSeconds { get; set; } = 10f;

        /// <summary>Consecutive trapped checks before acting (avoids reacting to a door closing for a moment).</summary>
        [Option("Confirm checks", "Consecutive trapped checks before acting (ignores a door closing for a moment).", "2. Rescue")]
        [Limit(1, 20)]
        [JsonProperty] public int RescueConfirmChecks { get; set; } = 3;

        /// <summary>Do not place another rescue order for the same dupe within this many game seconds.</summary>
        [Option("Cooldown (game s)", "No new rescue order for the same dupe within this time.", "2. Rescue", Format = "F0")]
        [Limit(10, 3000)]
        [JsonProperty] public float RescueCooldownSeconds { get; set; } = 300f;

        /// <summary>Thickest wall (in tiles) the rescue will dig through.</summary>
        [Option("Max wall to dig (tiles)", "Thickest wall the rescue will dig through.", "2. Rescue")]
        [Limit(1, 4)]
        [JsonProperty] public int RescueMaxWallThickness { get; set; } = 2;

        // ---- 3. Health gate ----
        [Option("Health gate", "Hurt dupes skip dangerous errands and prefer medical care or safe work.", "3. Health")]
        [JsonProperty] public bool HealthGate { get; set; } = true;

        /// <summary>Below this health fraction: no errands in scalding / freezing / radioactive spots.</summary>
        [Option("Low health", "Below this share of max health (0.6 = 60 %): no errands in scalding, freezing or radioactive spots.", "3. Health", Format = "F2")]
        [Limit(0, 1)]
        [JsonProperty] public float LowHealthFraction { get; set; } = 0.6f;

        /// <summary>Below this health fraction: also no errands in spots without breathable air.</summary>
        [Option("Critical health", "Below this share of max health (0.3 = 30 %): also no errands without breathable air.", "3. Health", Format = "F2")]
        [Limit(0, 1)]
        [JsonProperty] public float CriticalHealthFraction { get; set; } = 0.3f;

        /// <summary>Radiation (rads/cycle) at the work spot that counts as dangerous when hurt.</summary>
        [Option("Dangerous radiation (rads/cycle)", "Radiation at the work spot that counts as dangerous for hurt dupes.", "3. Health", Format = "F0")]
        [Limit(0, 5000)]
        [JsonProperty] public float RadiationDanger { get; set; } = 250f;

        /// <summary>Safety margin added to the dupe's own scalding / freezing threshold (Kelvin).</summary>
        [Option("Temperature margin (K)", "Safety margin added to the dupe's own scalding / freezing limits.", "3. Health", Format = "F1")]
        [Limit(0, 50)]
        [JsonProperty] public float TemperatureMargin { get; set; } = 2f;

        public static void Load(string modPath)
        {
            string file = Path.Combine(modPath ?? ".", "config.json");
            try
            {
                if (File.Exists(file))
                {
                    var loaded = JsonConvert.DeserializeObject<SafeConfig>(File.ReadAllText(file));
                    if (loaded != null) Instance = loaded;
                }
                Instance.Sanitize();
                File.WriteAllText(file, JsonConvert.SerializeObject(Instance, Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SafeDupes] config.json could not be read, using defaults: " + e.Message);
                Instance = new SafeConfig();
            }
        }

        private void Sanitize()
        {
            EscapeRadius = Math.Max(3, Math.Min(64, EscapeRadius));
            EscapeNodes = Math.Max(20, Math.Min(5000, EscapeNodes));
            CacheSeconds = Math.Max(0.5f, CacheSeconds);
            RescueCheckSeconds = Math.Max(1f, RescueCheckSeconds);
            RescueConfirmChecks = Math.Max(1, RescueConfirmChecks);
            RescueMaxWallThickness = Math.Max(1, Math.Min(4, RescueMaxWallThickness));
            CriticalHealthFraction = Math.Min(CriticalHealthFraction, LowHealthFraction);
        }
    }
}
