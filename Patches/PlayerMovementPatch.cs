using HarmonyLib;
using Reptile;

namespace BRCCodeDmg
{
    public static class GBEmuCurrentState
    {
        public static bool AppActive = false;
    }

    [HarmonyPatch(typeof(Player), "SetInputs")]
    public static class Player_SetInputs_GBEmuPatch
    {
        private static bool Prefix(Player __instance)
        {
            if (!GBEmuCurrentState.AppActive)
                return true;

            __instance.FlushInput();
            return false;
        }
    }
}