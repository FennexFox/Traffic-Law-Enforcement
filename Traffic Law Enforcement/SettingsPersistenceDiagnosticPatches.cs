using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Colossal.IO.AssetDatabase;
using Colossal.Json;
using Game.Input;
using Game.Modding;
using Game.Settings;
using HarmonyLib;

namespace Traffic_Law_Enforcement
{
    internal static class SettingsPersistenceDiagnosticPatches
    {
        private const string HarmonyId = "Traffic_Law_Enforcement.SettingsPersistenceDiagnosticPatches";

        private static readonly MethodInfo s_KeybindingSettingsBindingsGetter =
            AccessTools.PropertyGetter(typeof(KeybindingSettings), nameof(KeybindingSettings.bindings));

        private static readonly MethodInfo s_AssetDatabaseLoadSettings =
            AccessTools.Method(typeof(AssetDatabase), nameof(AssetDatabase.LoadSettings), new[] { typeof(string), typeof(object), typeof(object), typeof(bool) });

        private static readonly MethodInfo s_AssetDatabaseSaveSettings =
            AccessTools.Method(typeof(AssetDatabase), nameof(AssetDatabase.SaveSettings), Type.EmptyTypes);

        private static readonly MethodInfo s_AssetDatabaseSaveAllSettings =
            AccessTools.Method(typeof(AssetDatabase), nameof(AssetDatabase.SaveAllSettings), Type.EmptyTypes);

        private static readonly MethodInfo s_AssetDatabaseSaveSpecificSetting =
            AccessTools.Method(typeof(AssetDatabase), nameof(AssetDatabase.SaveSpecificSetting), new[] { typeof(string) });

        private static readonly Type s_SaveSettingsHelperType =
            AccessTools.TypeByName("Colossal.IO.AssetDatabase.Internal.SaveSettingsHelper");

        private static readonly MethodInfo s_SettingAssetSaveInternal =
            s_SaveSettingsHelperType == null
                ? null
                : AccessTools.Method(typeof(SettingAsset), "Save", new[] { typeof(bool), typeof(bool), s_SaveSettingsHelperType });

        private static readonly MethodInfo s_SettingAssetSaveWithPersist =
            AccessTools.Method(typeof(SettingAsset), "SaveWithPersist");

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

                Patch(s_KeybindingSettingsBindingsGetter, nameof(KeybindingSettingsBindingsPrefix), nameof(KeybindingSettingsBindingsPostfix));
                Patch(s_AssetDatabaseLoadSettings, nameof(LoadSettingsPrefix), nameof(LoadSettingsPostfix));
                Patch(s_AssetDatabaseSaveSettings, nameof(SaveSettingsPrefix));
                Patch(s_AssetDatabaseSaveAllSettings, nameof(SaveAllSettingsPrefix));
                Patch(s_AssetDatabaseSaveSpecificSetting, nameof(SaveSpecificSettingPrefix));
                Patch(s_SettingAssetSaveInternal, nameof(SettingAssetSavePrefix));
                Patch(s_SettingAssetSaveWithPersist, nameof(SettingAssetSaveWithPersistPrefix));
            }
            catch (Exception ex)
            {
                s_Harmony = null;
                Mod.log.Error(ex, "Failed to apply settings persistence diagnostic patches.");
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
                    Mod.log.Info("[SETTINGS_DIAG] CaptureCurrentBindingSnapshot skipped: InputManager is null.");
                    return;
                }

                List<ProxyBinding> builtInBindings = InputManager.instance
                    .GetBindings(InputManager.PathType.Effective, InputManager.BindingOptions.OnlyRebound | InputManager.BindingOptions.OnlyBuiltIn)
                    .ToList();

                int allReboundCount = InputManager.instance
                    .GetBindings(InputManager.PathType.Effective, InputManager.BindingOptions.OnlyRebound)
                    .Count;

                s_LastKnownBuiltInBindings = CloneBindings(builtInBindings);
                s_LoggedMissingBuiltInBindingCache = false;

                Mod.log.Info(
                    $"[SETTINGS_DIAG] Captured binding snapshot: builtInReboundCount={builtInBindings.Count}, allReboundCount={allReboundCount}");
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex, "Failed to capture current binding snapshot.");
            }
        }

        private static void Patch(MethodInfo original, string prefixName = null, string postfixName = null)
        {
            if (original == null)
            {
                return;
            }

            HarmonyMethod prefix = string.IsNullOrWhiteSpace(prefixName)
                ? null
                : new HarmonyMethod(typeof(SettingsPersistenceDiagnosticPatches), prefixName);

            HarmonyMethod postfix = string.IsNullOrWhiteSpace(postfixName)
                ? null
                : new HarmonyMethod(typeof(SettingsPersistenceDiagnosticPatches), postfixName);

            s_Harmony.Patch(original, prefix: prefix, postfix: postfix);
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
                    $"[SETTINGS_DIAG] KeybindingSettings.bindings requested after InputManager disposal. Returning cached built-in bindings count={__result.Count}.");
                return false;
            }

            if (!s_LoggedMissingBuiltInBindingCache)
            {
                s_LoggedMissingBuiltInBindingCache = true;
                Mod.log.Warn(
                    "[SETTINGS_DIAG] KeybindingSettings.bindings requested after InputManager disposal without a cache. Returning an empty list to avoid crashing settings save.");
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

        private static void LoadSettingsPrefix(string name, object obj, object defaultObj, bool userSetting)
        {
            if (!ShouldLogSource(obj))
            {
                return;
            }

            Mod.log.Info(
                $"[SETTINGS_DIAG] LoadSettings begin: name={name}, sourceType={obj.GetType().FullName}, defaultType={defaultObj?.GetType().FullName ?? "<null>"}, userSetting={userSetting}, fileLocation={GetFileLocation(obj.GetType())}");
        }

        private static void LoadSettingsPostfix(string name, object obj, object defaultObj, bool userSetting)
        {
            if (!ShouldLogSource(obj))
            {
                return;
            }

            Mod.log.Info(
                $"[SETTINGS_DIAG] LoadSettings done: name={name}, sourceType={obj.GetType().FullName}, state={DescribeSource(obj)}");
        }

        private static void SaveSettingsPrefix()
        {
            LogSaveEntry("SaveSettings", null);
        }

        private static void SaveAllSettingsPrefix()
        {
            LogSaveEntry("SaveAllSettings", null);
        }

        private static void SaveSpecificSettingPrefix(string settingName)
        {
            LogSaveEntry("SaveSpecificSetting", settingName);
        }

        private static void SettingAssetSavePrefix(SettingAsset __instance, bool saveAll, bool cleanupSettings)
        {
            LogSettingAssetSave("Save", __instance, saveAll, cleanupSettings, preReadSettingsData: null);
        }

        private static void SettingAssetSaveWithPersistPrefix(
            SettingAsset __instance,
            bool saveAll,
            bool cleanupSettings,
            Dictionary<string, Variant> preReadSettingsData)
        {
            LogSettingAssetSave("SaveWithPersist", __instance, saveAll, cleanupSettings, preReadSettingsData);
        }

        private static void LogSaveEntry(string methodName, string detail)
        {
            int cachedBuiltInCount = s_LastKnownBuiltInBindings?.Count ?? -1;
            int liveReboundCount = -1;

            try
            {
                if (InputManager.instance != null)
                {
                    liveReboundCount = InputManager.instance
                        .GetBindings(InputManager.PathType.Effective, InputManager.BindingOptions.OnlyRebound)
                        .Count;
                }
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"[SETTINGS_DIAG] Failed to inspect rebound bindings before {methodName}: {ex.GetType().Name}: {ex.Message}");
            }

            string detailSuffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $", detail={detail}";
            string cachedText = cachedBuiltInCount >= 0 ? cachedBuiltInCount.ToString() : "none";
            string liveText = liveReboundCount >= 0 ? liveReboundCount.ToString() : "unavailable";

            Mod.log.Info(
                $"[SETTINGS_DIAG] {methodName} begin: inputManagerAlive={InputManager.instance != null}, cachedBuiltInBindings={cachedText}, liveReboundBindings={liveText}{detailSuffix}");
        }

        private static void LogSettingAssetSave(
            string methodName,
            SettingAsset asset,
            bool saveAll,
            bool cleanupSettings,
            Dictionary<string, Variant> preReadSettingsData)
        {
            if (asset == null)
            {
                return;
            }

            try
            {
                foreach (SettingAsset.Fragment fragment in asset)
                {
                    if (fragment == null || !fragment.asset.database.canWriteSettings)
                    {
                        continue;
                    }

                    object source = fragment.source;
                    if (!ShouldLogSource(source))
                    {
                        continue;
                    }

                    string filePath = fragment.meta.path ?? "<null>";
                    bool hasPersistedCategory = preReadSettingsData != null && preReadSettingsData.ContainsKey(asset.name);

                    Mod.log.Info(
                        $"[SETTINGS_DIAG] {methodName}: asset={asset.name}, file={filePath}, sourceType={source.GetType().FullName}, saveAll={saveAll}, cleanupSettings={cleanupSettings}, hasPersistedCategory={hasPersistedCategory}, state={DescribeSource(source)}");
                }
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex, $"Failed to log settings persistence details for asset '{asset.name}'.");
            }
        }

        private static bool ShouldLogSource(object source)
        {
            return source is KeybindingSettings || source is ModSetting;
        }

        private static string DescribeSource(object source)
        {
            if (source == null)
            {
                return "<null>";
            }

            if (source is KeybindingSettings keybindingSettings)
            {
                try
                {
                    List<ProxyBinding> bindings = keybindingSettings.bindings ?? new List<ProxyBinding>();
                    return $"bindingsCount={bindings.Count}, sample={FormatBindingSample(bindings)}";
                }
                catch (Exception ex)
                {
                    return $"bindings=<error:{ex.GetType().Name}:{ex.Message}>";
                }
            }

            PropertyInfo[] bindingProperties = source
                .GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(static property => property.CanRead && property.PropertyType == typeof(ProxyBinding))
                .ToArray();

            if (bindingProperties.Length == 0)
            {
                return "proxyBindings=0";
            }

            List<string> entries = new List<string>(bindingProperties.Length);
            foreach (PropertyInfo property in bindingProperties.Take(12))
            {
                try
                {
                    ProxyBinding binding = (ProxyBinding)property.GetValue(source);
                    entries.Add($"{property.Name}={FormatBinding(binding)}");
                }
                catch (Exception ex)
                {
                    entries.Add($"{property.Name}=<error:{ex.GetType().Name}>");
                }
            }

            if (bindingProperties.Length > 12)
            {
                entries.Add($"+{bindingProperties.Length - 12} more");
            }

            return $"proxyBindings={bindingProperties.Length}, {string.Join("; ", entries)}";
        }

        private static string FormatBindingSample(IEnumerable<ProxyBinding> bindings)
        {
            List<string> sample = bindings
                .Take(6)
                .Select(FormatBinding)
                .ToList();

            if (sample.Count == 0)
            {
                return "none";
            }

            return string.Join(" | ", sample);
        }

        private static string FormatBinding(ProxyBinding binding)
        {
            string path = string.IsNullOrWhiteSpace(binding.path) ? "<empty>" : binding.path;
            return $"{binding.mapName}/{binding.actionName}:{path}";
        }

        private static string GetFileLocation(Type type)
        {
            FileLocationAttribute attribute = type == null
                ? null
                : type.GetCustomAttribute<FileLocationAttribute>(inherit: false);

            return attribute?.fileName ?? "FallbackSettings.coc";
        }

        private static List<ProxyBinding> CloneBindings(IEnumerable<ProxyBinding> bindings)
        {
            return bindings?.Select(static binding => binding.Copy()).ToList() ?? new List<ProxyBinding>();
        }
    }
}
