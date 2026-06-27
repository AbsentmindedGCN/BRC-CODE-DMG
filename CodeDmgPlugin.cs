using BepInEx;
using HarmonyLib;
using System.IO;
using BRCCodeDmg.Transport;
using Reptile;

namespace BRCCodeDmg
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    [BepInDependency("CommonAPI", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("BombRushMP.Plugin", BepInDependency.DependencyFlags.SoftDependency)]
    public class CodeDmgPlugin : BaseUnityPlugin
    {
        public static CodeDmgPlugin Instance { get; private set; }
        public static CodeDmgConfig ConfigSettings;

        public static LinkCableTransport LinkTransport { get; private set; }
        public static LinkCableManager LinkCable { get; private set; }

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
            try
            {
                _harmony.PatchAll();
            }
            catch (System.Exception ex)
            {
                Logger.LogWarning("CodeDMG Harmony patch failed: " + ex);
            }

            if (ConfigSettings.LinkCableEnabled.Value && IsBombRushMPPresent())
            {
                try
                {
                    LinkTransport = new LinkCableTransport();
                    LinkCable = new LinkCableManager(LinkTransport);
                }
                catch
                {
                    LinkTransport = null;
                    LinkCable = null;
                }
            }

            AppCodeDmg.Initialize();

            Logger.LogMessage("CodeDMG loaded.");
        }

        private bool _wasGamePaused;

        private void Update()
        {
            var core = Core.Instance;
            bool gamePaused = core != null && core.IsCorePaused;

            if (gamePaused && !_wasGamePaused)
                AppCodeDmg.HandleGamePauseStarted();

            _wasGamePaused = gamePaused;
        }

        private void OnDestroy()
        {
            if (_harmony != null)
                _harmony.UnpatchSelf();

            LinkCable?.Dispose();
            LinkTransport?.Dispose();
        }

        private static bool IsBombRushMPPresent()
        {
            try
            {
                var t = System.Type.GetType("BombRushMP.Plugin.ClientController, BombRushMP.Plugin");
                return t != null;
            }
            catch { return false; }
        }

        public static bool IsBombRushMPReady()
        {
            try
            {
                var t = System.Type.GetType("BombRushMP.Plugin.ClientController, BombRushMP.Plugin");
                if (t == null) return false;
                var prop = t.GetProperty("Instance",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                return prop?.GetValue(null) != null;
            }
            catch { return false; }
        }
    }
}