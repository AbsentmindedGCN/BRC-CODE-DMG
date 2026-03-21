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

        // Display
        public ConfigEntry<string> BackgroundColor;
        public ConfigEntry<bool>   PixelGrid;

        // Audio
        public ConfigEntry<bool> EnableAudio;
        public ConfigEntry<int>  Volume;

        public CodeDmgConfig(ConfigFile config)
        {
            RomPath = config.Bind(
                "General",
                "RomPath",
                "",
                "Path to ROM file (.gb or .gbc). Leave empty to use rom.gbc / rom.gb in plugin folder."
            );

            Up     = config.Bind("Controls", "Up",     KeyCode.W, "D-pad Up");
            Down   = config.Bind("Controls", "Down",   KeyCode.S, "D-pad Down");
            Left   = config.Bind("Controls", "Left",   KeyCode.A, "D-pad Left");
            Right  = config.Bind("Controls", "Right",  KeyCode.D, "D-pad Right");

            A      = config.Bind("Controls", "A",      KeyCode.Period,     "GB A button");
            B      = config.Bind("Controls", "B",      KeyCode.Comma,      "GB B button");
            Start  = config.Bind("Controls", "Start",  KeyCode.Return,     "GB Start button");
            Select = config.Bind("Controls", "Select", KeyCode.RightShift, "GB Select button");

            AutoSaveOnClose = config.Bind(
                "SaveStates",
                "AutoSaveOnClose",
                true,
                "Automatically save state when closing the app."
            );

            AutoLoadOnOpen = config.Bind(
                "SaveStates",
                "AutoLoadOnOpen",
                true,
                "Automatically load state when opening the app."
            );

            BackgroundColor = config.Bind(
                "Display",
                "BackgroundColor",
                "#000000",
                "Background color behind the Game Boy screen, as a hex value (e.g. #000000 for black, #1a1a2e for dark navy gray)."
            );

            PixelGrid = config.Bind(
                "Display",
                "PixelGrid",
                true,
                "Enable a GBC-style pixel grid overlay on the screen."
            );

            EnableAudio = config.Bind(
                "Audio",
                "EnableAudio",
                true,
                "Enable Game Boy audio emulation."
            );

            Volume = config.Bind(
                "Audio",
                "Volume",
                80,
                new ConfigDescription(
                    "Audio volume (0 = silent, 100 = full volume).",
                    new AcceptableValueRange<int>(0, 100)
                )
            );
        }
    }
}