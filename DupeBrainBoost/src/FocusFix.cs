using HarmonyLib;
using UnityEngine;

namespace DupeBrainBoost
{
    /// <summary>
    /// The game only reads keyboard and in-world mouse input while it believes its window has focus
    /// (KInputManager.isFocused). If Windows does not report focus coming back (Alt-Tab, overlays,
    /// popups, second monitor), keyboard shortcuts, right/middle mouse and world clicks stop working
    /// while menu buttons still react to left clicks.
    ///
    /// When Unity says the window IS focused but the game has thought otherwise for half a second,
    /// tell the game that focus came back (the same call Unity would have made) and log it.
    /// Dev-tool focus (KInputManager.devToolFocus) is left alone.
    /// </summary>
    [HarmonyPatch(typeof(GameInputManager), nameof(GameInputManager.Update))]
    internal static class FocusFix
    {
        private const float GraceSeconds = 0.5f;
        private static float mismatchSince = -1f;
        public static int Restored;

        internal static bool Prepare() => BoostConfig.Instance.FixLostInputFocus;

        internal static void Prefix(GameInputManager __instance)
        {
            if (KInputManager.isFocused || KInputManager.devToolFocus || !Application.isFocused)
            {
                mismatchSince = -1f;
                return;
            }
            float now = Time.unscaledTime;
            if (mismatchSince < 0f)
            {
                mismatchSince = now;
                return;
            }
            if (now - mismatchSince < GraceSeconds) return;
            mismatchSince = -1f;
            __instance.OnApplicationFocus(true);
            Restored++;
            Debug.LogFormat("[DupeBrainBoost] Input focus restored: the window was focused but the game had stopped " +
                            "reading keyboard/mouse (lost focus event). Fixed {0} time(s) this session.", Restored);
        }
    }
}
