using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Reptile;

namespace BRCCodeDmg.Patches
{
    [HarmonyPatch]
    internal static class CorePauseLinkCablePatch
    {
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var names = new[]
            {
                "PauseFromGame",
                "SetupPauseCore",
                "TogglePauseCore",
            };

            foreach (var name in names)
            {
                var method = AccessTools.Method(typeof(Core), name);
                if (method != null)
                    yield return method;
            }
        }

        private static void Postfix()
        {
            AppCodeDmg.HandleGamePauseStarted();
        }
    }
}
