using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using UnityEngine;
using Timberborn.Stockpiles;
using Timberborn.BlueprintSystem;

namespace Calloatti.StorageTweaks
{
    public class StorageMetadata
    {
        public string BlueprintId;
        public int DefaultCapacity;
        public int ModdedCapacity;
        public float VisualRatio;
    }

    [HarmonyPatch("Timberborn.BlueprintSystem.SpecService", "Load")]
    public static class StorageCapacityCache
    {
        // Toggle this to true/false to enable/disable detailed logging
        private static bool DebugMode = false;

        public static readonly Dictionary<string, StorageMetadata> StorageRegistry = new Dictionary<string, StorageMetadata>();
        private static readonly string ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Timberborn", "Mods", "Storage Tweaks", "StorageTweaks.txt");

        [HarmonyPostfix]
        public static void Postfix(object __instance)
        {
            Debug.Log("[StorageTweaks] SpecService.Load Postfix started.");
            ProcessConfig(__instance);
            Debug.Log($"[StorageTweaks] Finished. {StorageRegistry.Count} blueprints registered.");
        }

        private static void ProcessConfig(object specService)
        {
            try
            {
                EnsureConfigDirectory();

                if (!File.Exists(ConfigPath))
                {
                    Debug.LogWarning("[StorageTweaks] Config file missing. Attempting to extract default config from embedded resources...");
                    ExtractEmbeddedConfig();
                }

                StorageRegistry.Clear();

                Dictionary<string, int> userConfig = LoadUserConfig();
                List<string> configLines = new List<string> { "# --- STORAGE TWEAKS CONFIG ---" };
                bool fileModified = false;

                var type = specService.GetType();
                var sourceService = type.GetField("_blueprintSourceService", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(specService) as BlueprintSourceService;
                var deserializer = type.GetField("_blueprintDeserializer", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(specService) as BlueprintDeserializer;
                var specDict = type.GetField("_cachedBlueprintsBySpecs", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(specService) as IDictionary;

                if (specDict == null || deserializer == null || sourceService == null) return;

                foreach (DictionaryEntry entry in specDict)
                {
                    if ((Type)entry.Key != typeof(StockpileSpec)) continue;

                    var lazyList = entry.Value as IList;
                    if (lazyList == null) continue;

                    foreach (object lazyObj in lazyList)
                    {
                        var blueprint = lazyObj.GetType().GetProperty("Value")?.GetValue(lazyObj) as Blueprint;
                        if (blueprint == null) continue;

                        // 1. Get Default Capacity
                        string rawJson = OriginalCapacityFetcher.GetRawJson(sourceService, blueprint);
                        int defaultCap = OriginalCapacityFetcher.GetOriginalCapacity(deserializer, blueprint, rawJson);
                        if (defaultCap <= 0) defaultCap = blueprint.GetSpec<StockpileSpec>().MaxCapacity;

                        // 2. Get Visual Limit (DIRECTLY FROM OBJECT)
                        float visualLimit = VolumeCalculator.Calculate(blueprint);

                        // 3. Modded Capacity & Ratio
                        int moddedCap = userConfig.TryGetValue(blueprint.Name, out int val) && val > 0 ? val : defaultCap;
                        float ratio = (moddedCap > 0) ? visualLimit / (float)moddedCap : 1f;

                        StorageRegistry[blueprint.Name] = new StorageMetadata
                        {
                            BlueprintId = blueprint.Name,
                            DefaultCapacity = defaultCap,
                            ModdedCapacity = moddedCap,
                            VisualRatio = ratio
                        };

                        Debug.Log($"[StorageTweaks] {blueprint.Name} | Def: {defaultCap} | Mod: {moddedCap} | VolLimit: {visualLimit} | Ratio: {ratio:F4}");

                        configLines.Add($"{blueprint.Name}={moddedCap} # Default: {defaultCap}");
                        if (!userConfig.ContainsKey(blueprint.Name) || userConfig[blueprint.Name] != moddedCap) fileModified = true;

                        var field = typeof(StockpileSpec).GetField("<MaxCapacity>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                        field?.SetValue(blueprint.GetSpec<StockpileSpec>(), moddedCap);
                    }
                }

                if (fileModified || !File.Exists(ConfigPath))
                {
                    if (DebugMode) Debug.Log("[StorageTweaks] Updating config file on disk...");
                    File.WriteAllLines(ConfigPath, configLines.ToArray());
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[StorageTweaks] Error in ProcessConfig: {ex}");
            }
        }

        private static void EnsureConfigDirectory()
        {
            string dir = Path.GetDirectoryName(ConfigPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        private static void ExtractEmbeddedConfig()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                string resourceName = "Calloatti.StorageTweaks.StorageTweaks.txt";

                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        using (StreamReader reader = new StreamReader(stream))
                        {
                            string content = reader.ReadToEnd();
                            File.WriteAllText(ConfigPath, content);
                            if (DebugMode) Debug.Log($"[StorageTweaks] Successfully extracted default config to {ConfigPath}");
                        }
                    }
                    else
                    {
                        Debug.LogError($"[StorageTweaks] Embedded resource '{resourceName}' not found! Available resources: {string.Join(", ", assembly.GetManifestResourceNames())}");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[StorageTweaks] Failed to extract embedded config: {e}");
            }
        }

        private static Dictionary<string, int> LoadUserConfig()
        {
            var config = new Dictionary<string, int>();

            if (DebugMode) Debug.Log($"[StorageTweaks] LoadUserConfig called. Path: {ConfigPath}");

            if (!File.Exists(ConfigPath))
            {
                Debug.LogWarning($"[StorageTweaks] Config file NOT found at: {ConfigPath}");
                return config;
            }

            try
            {
                string[] lines = File.ReadAllLines(ConfigPath);
                if (DebugMode) Debug.Log($"[StorageTweaks] File opened. Lines count: {lines.Length}");

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    if (DebugMode) Debug.Log($"[StorageTweaks] Processing line: '{line}'");

                    // 1. Clean comment
                    string clean = line.Split('#')[0].Trim();

                    if (string.IsNullOrWhiteSpace(clean))
                    {
                        if (DebugMode) Debug.Log($"[StorageTweaks] Line ignored (comment/empty after trim).");
                        continue;
                    }

                    // 2. Split
                    string[] parts = clean.Split('=');
                    if (parts.Length == 2)
                    {
                        string key = parts[0].Trim();
                        string valStr = parts[1].Trim();

                        if (int.TryParse(valStr, out int val))
                        {
                            config[key] = val;
                            if (DebugMode) Debug.Log($"[StorageTweaks] SUCCESS: Parsed '{key}' = {val}");
                        }
                        else
                        {
                            Debug.LogError($"[StorageTweaks] PARSE FAIL: Could not parse integer from '{valStr}' in line: {line}");
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[StorageTweaks] FORMAT ERROR: Line does not match 'Key=Value' format: {clean}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[StorageTweaks] Exception in LoadUserConfig: {ex}");
            }

            return config;
        }
    }
}