using HarmonyLib;
using Timberborn.BlockSystem;
using UnityEngine;
using System.Reflection;
using System.Collections.Generic;
using System;

namespace Calloatti.StorageTweaks
{
    public static class VisualizerCache
    {
        public static Dictionary<int, float> Ratios = new Dictionary<int, float>();
    }

    [HarmonyPatch]
    public static class StockpileVisualizerPatches
    {
        // --- SCREENSHOT TOGGLE ---
        public static bool EnableVisualScaling = true;
        // -------------------------

        [HarmonyPatch("Timberborn.StockpileVisualization.StockpileGoodPileVisualizer", "Initialize")]
        [HarmonyPostfix]
        public static void InitializePostfix(MonoBehaviour __instance, int capacity)
        {
            try
            {
                var type = __instance.GetType();
                var blockObjField = type.GetField("_blockObject", BindingFlags.NonPublic | BindingFlags.Instance);
                var perLevelField = type.GetField("_perLevelAmount", BindingFlags.NonPublic | BindingFlags.Instance);

                if (blockObjField == null || perLevelField == null) return;

                var blockObject = blockObjField.GetValue(__instance) as BlockObject;
                int itemsPerLayer = (int)perLevelField.GetValue(__instance);

                if (blockObject != null && blockObject.Blocks != null && itemsPerLayer > 0)
                {
                    // Calculate the standard visual capacity (e.g., 180 for small pile)
                    int maxVisualCapacity = itemsPerLayer * blockObject.Blocks.Size.z * 5;

                    // Store the ratio (Visual Limit / Real Modded Capacity)
                    VisualizerCache.Ratios[__instance.GetInstanceID()] = (float)maxVisualCapacity / (float)capacity;
                }
            }
            catch { }
        }

        [HarmonyPatch("Timberborn.StockpileVisualization.StockpileGoodPileVisualizer", "UpdateAmount")]
        [HarmonyPrefix]
        public static bool UpdateAmountPrefix(MonoBehaviour __instance, ref int amountInStock)
        {
            // High-performance check: Verify the instance exists and hasn't been destroyed.
            // The !__instance check is the Unity-specific way to catch 'fake-null' objects
            // left behind by removed mods.
            if (__instance == null || !__instance)
            {
                return true;
            }

            // Direct dictionary lookup is O(1) and very fast.
            if (EnableVisualScaling && VisualizerCache.Ratios.TryGetValue(__instance.GetInstanceID(), out float ratio))
            {
                amountInStock = Mathf.CeilToInt(amountInStock * ratio);
            }

            return true;
        }

        [HarmonyPatch("Timberborn.StockpileVisualization.StockpileGoodPileVisualizer", "Clear")]
        [HarmonyPostfix]
        public static void ClearPostfix(MonoBehaviour __instance)
        {
            VisualizerCache.Ratios.Remove(__instance.GetInstanceID());
        }
    }
}