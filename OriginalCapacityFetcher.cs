using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using HarmonyLib;
using Timberborn.BlueprintSystem;
using Timberborn.Stockpiles;

namespace Calloatti.StorageTweaks
{
    public static class OriginalCapacityFetcher
    {
        // --- 1. SAFE JSON RETRIEVAL ---
        public static string GetRawJson(BlueprintSourceService sourceService, Blueprint blueprint)
        {
            try
            {
                var bundle = sourceService.Get(blueprint);
                if (bundle == null) return null;

                // Reflection to get the private 'Jsons' property from the bundle
                var jsonsProp = typeof(BlueprintFileBundle).GetProperty("Jsons");
                var jsonsObj = jsonsProp?.GetValue(bundle);

                if (jsonsObj == null) return null;

                // ImmutableArray<string> indexing to get item [0]
                var itemProp = jsonsObj.GetType().GetProperty("Item");
                return itemProp?.GetValue(jsonsObj, new object[] { 0 }) as string;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[StorageTweaks] Failed to extract JSON for {blueprint.Name}: {ex.Message}");
                return null;
            }
        }

        // --- 2. ORIGINAL CAPACITY RETRIEVAL ---
        public static int GetOriginalCapacity(BlueprintDeserializer deserializer, Blueprint blueprint, string originalJson)
        {
            try
            {
                if (string.IsNullOrEmpty(originalJson)) return -1;

                // Create a "BaseGame" bundle using reflection to bypass CS1729 errors
                var baseGameBundle = ReconstructBundle(blueprint.Name, originalJson);
                if (baseGameBundle == null) return -1;

                // Deserialize using the game's internal deserializer
                MethodInfo deserializeMethod = typeof(BlueprintDeserializer).GetMethod("Deserialize", new Type[] { typeof(BlueprintFileBundle) });
                if (deserializeMethod == null) return -1;

                var originalBlueprint = deserializeMethod.Invoke(deserializer, new object[] { baseGameBundle }) as Blueprint;

                return originalBlueprint?.GetSpec<StockpileSpec>()?.MaxCapacity ?? -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        // --- 3. BUNDLE RECONSTRUCTION HELPER ---
        private static BlueprintFileBundle ReconstructBundle(string name, string json)
        {
            try
            {
                // Reflection to create ImmutableArray<string>
                var immutableType = AccessTools.TypeByName("System.Collections.Immutable.ImmutableArray");
                var createMethod = immutableType?.GetMethods().FirstOrDefault(m =>
                    m.Name == "Create" && m.IsGenericMethod && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsArray
                )?.MakeGenericMethod(typeof(string));

                if (createMethod == null) return null;

                object jsons = createMethod.Invoke(null, new object[] { new string[] { json } });
                object sources = createMethod.Invoke(null, new object[] { new string[] { "BaseGame" } });

                // Use reflection to find the correct constructor
                var ctor = typeof(BlueprintFileBundle).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).FirstOrDefault();

                // Invoke constructor: (name, path, jsons, sources)
                return ctor?.Invoke(new object[] { name, name, jsons, sources }) as BlueprintFileBundle;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[StorageTweaks] Bundle Reconstruction Failed: {ex.Message}");
                return null;
            }
        }
    }
}