using BepInEx;
using HarmonyLib;
using System.IO;

namespace BRCCodeDmg
{
    [BepInPlugin("transrights.codedmg", "CodeDMG", "1.0.0")]
    [BepInDependency("CommonAPI", BepInDependency.DependencyFlags.HardDependency)]
    public class CodeDmgPlugin : BaseUnityPlugin
    {
        public static CodeDmgPlugin Instance { get; private set; }
        public static CodeDmgConfig ConfigSettings;

        public string PluginDirectory
        {
            get { return Path.GetDirectoryName(this.Info.Location); }
        }

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;

            ConfigSettings = new CodeDmgConfig(Config);

            _harmony = new Harmony("transrights.codedmg");
            _harmony.PatchAll();

            AppCodeDmg.Initialize();

            Logger.LogMessage("CodeDMG loaded.");
        }

        private void OnDestroy()
        {
            if (_harmony != null)
                _harmony.UnpatchSelf();
        }
    }
}