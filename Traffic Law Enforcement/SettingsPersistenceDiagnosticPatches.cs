using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Input;
using Game.Settings;
using HarmonyLib;

namespace Traffic_Law_Enforcement
{
    internal static class SettingsPersistenceDiagnosticPatches
    {
        private const string HarmonyId = "Traffic_Law_Enforcement.SettingsPersistenceDiagnosticPatches";

        private static readonly MethodInfo s_KeybindingSettingsBindingsGetter =
            AccessTools.PropertyGetter(typeof(KeybindingSettings), nameof(KeybindingSettings.bindings));

        private static Harmony s_Harmony;
        private static List<ProxyBinding> s_LastKnownBuiltInBindings;
        private static bool s_LoggedMissingBuiltInBindingCache;

        public static void Apply()
        {
            if (s_Harmony != null)
            {
                return;
            }

            try
            {
                s_Harmony = new Harmony(HarmonyId);
                HarmonyMethod prefix = new HarmonyMethod(typeof(SettingsPersistenceDiagnosticPatches), nameof(KeybindingSettingsBindingsPrefix));
                HarmonyMethod postfix = new HarmonyMethod(typeof(SettingsPersistenceDiagnosticPatches), nameof(KeybindingSettingsBindingsPostfix));
                s_Harmony.Patch(s_KeybindingSettingsBindingsGetter, prefix: prefix, postfix: postfix);
            }
            catch (Exception ex)
            {
                s_Harmony = null;
                Mod.log.Error(ex, "Failed to apply keybinding persistence safeguard.");
            }
        }

        public static void Remove()
        {
            if (s_Harmony == null)
            {
                return;
            }

            s_Harmony.UnpatchAll(HarmonyId);
            s_Harmony = null;
        }

        public static void CaptureCurrentBindingSnapshot()
        {
            try
            {
                if (InputManager.instance == null)
                {
                    return;
                }

                List<ProxyBinding> builtInBindings = InputManager.instance
                    .GetBindings(InputManager.PathType.Effective, InputManager.BindingOptions.OnlyRebound | InputManager.BindingOptions.OnlyBuiltIn)
                    .ToList();

                s_LastKnownBuiltInBindings = CloneBindings(builtInBindings);
                s_LoggedMissingBuiltInBindingCache = false;
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex, "Failed to capture current binding snapshot.");
            }
        }

        private static bool KeybindingSettingsBindingsPrefix(ref List<ProxyBinding> __result)
        {
            if (InputManager.instance != null)
            {
                return true;
            }

            if (s_LastKnownBuiltInBindings != null)
            {
                __result = CloneBindings(s_LastKnownBuiltInBindings);
                Mod.log.Warn(
                    $"[KEYBIND_GUARD] KeybindingSettings.bindings requested after InputManager disposal. Returning cached bindings count={__result.Count}.");
                return false;
            }

            if (!s_LoggedMissingBuiltInBindingCache)
            {
                s_LoggedMissingBuiltInBindingCache = true;
                Mod.log.Warn(
                    "[KEYBIND_GUARD] KeybindingSettings.bindings requested after InputManager disposal without a cache. Returning an empty list to avoid crashing settings save.");
            }

            __result = new List<ProxyBinding>();
            return false;
        }

        private static void KeybindingSettingsBindingsPostfix(List<ProxyBinding> __result)
        {
            if (InputManager.instance == null || __result == null)
            {
                return;
            }

            s_LastKnownBuiltInBindings = CloneBindings(__result);
            s_LoggedMissingBuiltInBindingCache = false;
        }

        private static List<ProxyBinding> CloneBindings(IEnumerable<ProxyBinding> bindings)
        {
            return bindings?.Select(static binding => binding.Copy()).ToList() ?? new List<ProxyBinding>();
        }
    }
}
