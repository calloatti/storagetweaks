using HarmonyLib;
using Timberborn.ModManagerScene;
using UnityEngine;

namespace StorageTweaks
{
    public class StorageTweaksStartup : IModStarter
    {
        public void StartMod(IModEnvironment environment)
        {
            // This line finds all [HarmonyPatch] attributes in your project and runs them
            new Harmony("calloatti.storagetweaks").PatchAll();

            // This confirms the mod actually loaded in the Player.log
            Debug.Log("[StorageTweaks] Harmony Patches Applied.");
        }
    }
}