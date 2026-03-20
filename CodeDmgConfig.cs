using BepInEx.Configuration;
using UnityEngine;

namespace BRCCodeDmg
{
    public class CodeDmgConfig
    {
        // ROM
        public ConfigEntry<string> RomPath;

        // Controls
        public ConfigEntry<KeyCode> Up;
        public ConfigEntry<KeyCode> Down;
        public ConfigEntry<KeyCode> Left;
        public ConfigEntry<KeyCode> Right;

        public ConfigEntry<KeyCode> A;
        public ConfigEntry<KeyCode> B;
        public ConfigEntry<KeyCode> Start;
        public ConfigEntry<KeyCode> Select;

        // Save states
        public ConfigEntry<bool> AutoSaveOnClose;
        public ConfigEntry<bool> AutoLoadOnOpen;

        public CodeDmgConfig(ConfigFile config)
        {
            RomPath = config.Bind(
                "General",
                "RomPath",
                "",
                "Path to ROM file. Leave empty to use rom.gb in plugin folder."
            );

            Up = config.Bind("Controls", "Up", KeyCode.W);
            Down = config.Bind("Controls", "Down", KeyCode.S);
            Left = config.Bind("Controls", "Left", KeyCode.A);
            Right = config.Bind("Controls", "Right", KeyCode.D);

            A = config.Bind("Controls", "A", KeyCode.X);
            B = config.Bind("Controls", "B", KeyCode.Z);

            Start = config.Bind("Controls", "Start", KeyCode.Q);
            Select = config.Bind("Controls", "Select", KeyCode.E);

            AutoSaveOnClose = config.Bind(
                "SaveStates",
                "AutoSaveOnClose",
                true,
                "Automatically save state when closing the app"
            );

            AutoLoadOnOpen = config.Bind(
                "SaveStates",
                "AutoLoadOnOpen",
                true,
                "Automatically load state when opening the app"
            );
        }
    }
}