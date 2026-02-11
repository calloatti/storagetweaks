using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.IO;
using UnityEngine;
using Timberborn.Stockpiles;
using Timberborn.BlueprintSystem;

namespace StorageTweaks
{
    [HarmonyPatch("Timberborn.BlueprintSystem.SpecService", "Load")]
    public static class StorageCapacityCache
    {
        private static readonly string ModFolderName = "Storage Tweaks";
        private static readonly string ConfigFileName = "StorageTweaks.txt";

        [HarmonyPostfix]
        public static void Postfix(object __instance)
        {
            Debug.Log("[StorageTweaks] SpecService.Load Intercepted!");
            ProcessConfig(__instance);
        }

        private static void ProcessConfig(object specServiceInstance)
        {
            Debug.Log("[StorageTweaks] Processing Config...");

            try
            {
                // 1. PATH SETUP
                string docsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string modDirectory = Path.Combine(docsPath, "Timberborn", "Mods", ModFolderName);
                string configPath = Path.Combine(modDirectory, ConfigFileName);

                if (!Directory.Exists(modDirectory)) Directory.CreateDirectory(modDirectory);
                if (!File.Exists(configPath)) ExtractEmbeddedConfig(configPath);

                List<string> fileLines = File.Exists(configPath) ? File.ReadAllLines(configPath).ToList() : new List<string>();
                Dictionary<string, int> userConfig = new Dictionary<string, int>();
                Dictionary<string, int> lineIndices = new Dictionary<string, int>();

                for (int i = 0; i < fileLines.Count; i++)
                {
                    string clean = fileLines[i].Split('#')[0].Trim();
                    if (string.IsNullOrWhiteSpace(clean)) continue;
                    string[] parts = clean.Split('=');
                    if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out int val))
                    {
                        userConfig[parts[0].Trim()] = val;
                        lineIndices[parts[0].Trim()] = i;
                    }
                }

                // 2. REFLECTION SETUP
                var type = specServiceInstance.GetType();
                var sourceService = type.GetField("_blueprintSourceService", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(specServiceInstance) as BlueprintSourceService;
                var deserializer = type.GetField("_blueprintDeserializer", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(specServiceInstance) as BlueprintDeserializer;
                var specDict = type.GetField("_cachedBlueprintsBySpecs", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(specServiceInstance) as IDictionary;

                if (specDict == null)
                {
                    Debug.LogError("[StorageTweaks] SpecDict not found!");
                    return;
                }

                bool fileModified = false;
                HashSet<string> processed = new HashSet<string>();

                // 3. MAIN LOOP
                foreach (DictionaryEntry entry in specDict)
                {
                    if (((Type)entry.Key) != typeof(StockpileSpec)) continue;

                    var lazyList = entry.Value as IList;
                    if (lazyList == null) continue;

                    foreach (object lazyObj in lazyList)
                    {
                        var bpValue = lazyObj.GetType().GetProperty("Value")?.GetValue(lazyObj) as Blueprint;
                        if (bpValue == null) continue;

                        string id = bpValue.Name;
                        if (processed.Contains(id)) continue;
                        processed.Add(id);

                        var spec = bpValue.GetSpec<StockpileSpec>();
                        if (spec == null) continue;

                        // A. DETERMINE CAPACITIES
                        int originalCap = FetchOriginalCapacity(sourceService, deserializer, bpValue);
                        if (originalCap <= 0) originalCap = spec.MaxCapacity;
                        int finalCap = originalCap;

                        // B. CHECK CONFIG
                        if (userConfig.TryGetValue(id, out int userCap))
                        {
                            finalCap = userCap > 0 ? userCap : originalCap;

                            string expectedLineStart = $"{id}={finalCap}";
                            string expectedComment = $"# Default: {originalCap}";
                            if (lineIndices.ContainsKey(id) && lineIndices[id] < fileLines.Count)
                            {
                                if (!fileLines[lineIndices[id]].StartsWith(expectedLineStart))
                                {
                                    fileLines[lineIndices[id]] = $"{id}={finalCap} {expectedComment}";
                                    fileModified = true;
                                }
                            }
                        }
                        else
                        {
                            fileLines.Add($"{id}={spec.MaxCapacity} # Default: {originalCap}");
                            fileModified = true;
                        }

                        // C. APPLY & LOG
                        var field = spec.GetType().GetField("<MaxCapacity>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (field != null)
                        {
                            field.SetValue(spec, finalCap);

                            Debug.Log($"[StorageTweaks] {id} Default Capacity: {originalCap} Modded Capacity: {finalCap}");
                        }
                    }
                }

                if (fileModified) File.WriteAllLines(configPath, fileLines);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[StorageTweaks] Error during config processing: {ex.Message}");
            }
        }

        private static void ExtractEmbeddedConfig(string path)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                string actualResourceName = assembly.GetManifestResourceNames().FirstOrDefault(name => name.EndsWith(ConfigFileName, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(actualResourceName))
                {
                    File.WriteAllLines(path, new[] { "# StorageTweaks Config", "# Format: BlueprintId=Capacity # Default: Value", "" });
                    return;
                }
                using (Stream stream = assembly.GetManifestResourceStream(actualResourceName))
                {
                    if (stream != null) using (StreamReader reader = new StreamReader(stream)) File.WriteAllText(path, reader.ReadToEnd());
                }
            }
            catch { }
        }

        private static int FetchOriginalCapacity(BlueprintSourceService sourceService, BlueprintDeserializer deserializer, Blueprint blueprint)
        {
            try
            {
                var bundle = sourceService.Get(blueprint);
                if (bundle == null) return -1;
                var jsonsProp = typeof(BlueprintFileBundle).GetProperty("Jsons");
                var jsonsObj = jsonsProp?.GetValue(bundle);
                var itemProp = jsonsObj?.GetType().GetProperty("Item");
                string originalJson = itemProp?.GetValue(jsonsObj, new object[] { 0 }) as string;
                if (string.IsNullOrEmpty(originalJson)) return -1;
                var originalBundle = ReconstructBaseBundle(bundle, originalJson);
                var originalBlueprint = deserializer.DeserializeUnsafe(originalBundle);
                return originalBlueprint?.GetSpec<StockpileSpec>()?.MaxCapacity ?? -1;
            }
            catch { return -1; }
        }

        private static BlueprintFileBundle ReconstructBaseBundle(BlueprintFileBundle original, string json)
        {
            var immutableType = AccessTools.TypeByName("System.Collections.Immutable.ImmutableArray");
            var createMethod = immutableType?.GetMethods().FirstOrDefault(m => m.Name == "Create" && m.IsGenericMethod && m.GetParameters().Length == 1)?.MakeGenericMethod(typeof(string));
            if (createMethod == null) return null;
            object jsons = createMethod.Invoke(null, new object[] { json });
            object sources = createMethod.Invoke(null, new object[] { "BaseGame" });
            var ctor = typeof(BlueprintFileBundle).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault();
            return ctor?.Invoke(new object[] { original.Name, original.Path, jsons, sources }) as BlueprintFileBundle;
        }
    }
}