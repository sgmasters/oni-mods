using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KMod;
using UnityEngine;

namespace DupeTraffic
{
    public sealed class DupeTrafficMod : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            TrafficConfig.Load(path);
            base.OnLoad(harmony); // PatchAll
            // "Options" button for this mod in the Mods menu (PLib).
            PeterHan.PLib.Core.PUtil.InitLibrary(false);
            new PeterHan.PLib.Options.POptions().RegisterOptions(this, typeof(TrafficConfig));
            TrafficOverlay.RegisterStrings();
            Debug.Log("[DupeTraffic] loaded");
        }
    }

    /// <summary>Record every step a duplicant starts.</summary>
    [HarmonyPatch(typeof(Navigator), nameof(Navigator.BeginTransition))]
    internal static class Navigator_BeginTransition_Patch
    {
        internal static void Postfix(Navigator __instance, NavGrid.Transition transition)
        {
            try
            {
                if (__instance == null || !__instance.HasTag(GameTags.BaseMinion)) return;
                TrafficTracker.RecordStep(Grid.PosToCell(__instance), transition.start);
            }
            catch (Exception)
            {
                // Never let the heatmap break movement.
            }
        }
    }

    /// <summary>Fresh map for every loaded save / new world.</summary>
    [HarmonyPatch(typeof(Game), "OnSpawn")]
    internal static class Game_OnSpawn_Patch
    {
        internal static void Postfix()
        {
            TrafficTracker.Reset();
        }
    }

    /// <summary>Register the overlay mode with OverlayScreen.</summary>
    [HarmonyPatch(typeof(OverlayScreen), "RegisterModes")]
    internal static class OverlayScreen_RegisterModes_Patch
    {
        private static readonly MethodInfo RegisterMode =
            AccessTools.Method(typeof(OverlayScreen), "RegisterMode", new[] { typeof(OverlayModes.Mode) });

        internal static void Postfix(OverlayScreen __instance)
        {
            RegisterMode.Invoke(__instance, new object[] { new TrafficOverlay() });
        }
    }

    /// <summary>Hook the per-cell colour function into SimDebugView.</summary>
    [HarmonyPatch(typeof(SimDebugView), "OnPrefabInit")]
    internal static class SimDebugView_OnPrefabInit_Patch
    {
        internal static void Postfix(SimDebugView __instance)
        {
            var funcs = Traverse.Create(__instance).Field("getColourFuncs")
                .GetValue<Dictionary<HashedString, Func<SimDebugView, int, Color>>>();
            if (funcs != null)
                funcs[TrafficOverlay.ID] = TrafficOverlay.GetCellColour;
            else
                Debug.LogWarning("[DupeTraffic] SimDebugView.getColourFuncs not found");
        }
    }

    /// <summary>Legend panel (title, colour key, All/Climbing/Walking filter).</summary>
    [HarmonyPatch(typeof(OverlayLegend), "OnSpawn")]
    internal static class OverlayLegend_OnSpawn_Patch
    {
        internal static void Postfix(OverlayLegend __instance)
        {
            var list = Traverse.Create(__instance).Field("overlayInfoList")
                .GetValue<List<OverlayLegend.OverlayInfo>>();
            if (list == null || list.Exists(i => i.mode == TrafficOverlay.ID)) return;
            list.Add(new OverlayLegend.OverlayInfo
            {
                name = "DUPE TRAFFIC",
                mode = TrafficOverlay.ID,
                infoUnits = new List<OverlayLegend.OverlayInfoUnit>(),
                diagrams = new List<GameObject>(),
                isProgrammaticallyPopulated = true,
            });
        }
    }

    /// <summary>Add the toggle button to the overlay bar.</summary>
    [HarmonyPatch(typeof(OverlayMenu), "InitializeToggles")]
    internal static class OverlayMenu_InitializeToggles_Patch
    {
        internal const string IconName = "overlay_dupetraffic";

        internal static void Postfix(OverlayMenu __instance)
        {
            TrafficOverlay.RegisterStrings(); // again, in case localization reloaded the string table
            EnsureIcon();
            var toggles = Traverse.Create(__instance).Field("overlayToggleInfos").GetValue<IList>();
            var infoType = AccessTools.Inner(typeof(OverlayMenu), "OverlayToggleInfo");
            if (toggles == null || infoType == null)
            {
                Debug.LogWarning("[DupeTraffic] could not add overlay button");
                return;
            }
            // OverlayToggleInfo(string text, string icon_name, HashedString sim_view,
            //                   string required_tech_item, Action hotKey, string tooltip, string tooltip_header)
            object info = Activator.CreateInstance(infoType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[]
                {
                    "Dupe Traffic", IconName, TrafficOverlay.ID, "", global::Action.NumActions,
                    "Shows where duplicants walk and climb most.\nUse it to place plastic ladders, fire poles, better tiles or transit tubes.",
                    "Dupe Traffic"
                }, null);
            toggles.Add(info);
        }

        /// <summary>Creates a small procedural "heat" icon so we don't ship image files.</summary>
        private static void EnsureIcon()
        {
            if (Assets.Sprites == null || Assets.Sprites.ContainsKey(IconName)) return;
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var clear = new Color(0, 0, 0, 0);
            var px = new Color[size * size];
            for (int i = 0; i < px.Length; i++) px[i] = clear;
            // Footstep-like blobs from cool (bottom-left) to hot (top-right) on a diagonal path.
            Color[] cols =
            {
                new Color(0.35f, 0.55f, 1f), new Color(0.2f, 0.9f, 0.9f), new Color(0.4f, 0.95f, 0.3f),
                new Color(1f, 0.9f, 0.2f), new Color(1f, 0.35f, 0.2f)
            };
            for (int s = 0; s < cols.Length; s++)
            {
                float cx = 10 + s * 11f, cy = 10 + s * 11f, r = 6.5f + s * 0.4f;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float dx = x - cx, dy = y - cy;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        if (d <= r)
                        {
                            var c = cols[s];
                            c.a = Mathf.Clamp01((r - d) * 1.5f);
                            int idx = y * size + x;
                            px[idx] = Color.Lerp(px[idx], c, c.a);
                            px[idx].a = Mathf.Max(px[idx].a, c.a);
                        }
                    }
            }
            tex.SetPixels(px);
            tex.Apply();
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = IconName;
            Assets.Sprites[IconName] = sprite;
        }
    }
}
