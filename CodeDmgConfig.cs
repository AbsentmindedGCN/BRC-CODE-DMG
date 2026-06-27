using BepInEx.Configuration;
using UnityEngine;

namespace BRCCodeDmg
{
    public class CodeDmgConfig
    {
        // ROM
        public ConfigEntry<string> RomPath;
        public ConfigEntry<string> RomDirectory;
        public ConfigEntry<bool> LoadLastPlayed;

        // Controls
        public ConfigEntry<KeyCode> Up;
        public ConfigEntry<KeyCode> Down;
        public ConfigEntry<KeyCode> Left;
        public ConfigEntry<KeyCode> Right;

        public ConfigEntry<KeyCode> A;
        public ConfigEntry<KeyCode> B;
        public ConfigEntry<KeyCode> Start;
        public ConfigEntry<KeyCode> Select;
        public ConfigEntry<bool> SwapButtons;

        // Save states
        public ConfigEntry<bool> AutoSaveOnClose;
        public ConfigEntry<bool> AutoLoadOnOpen;

        // Battery Saves
        public ConfigEntry<bool> BatterySaveAutoSave;
        public ConfigEntry<bool> BatterySaveAutoLoad;

        // Display
        public ConfigEntry<string> BackgroundColor;
        public ConfigEntry<bool> PixelGrid;
        public ConfigEntry<string> Palette;

        // Audio
        public ConfigEntry<bool> EnableAudio;
        public ConfigEntry<int> Volume;
        public ConfigEntry<string> AudioLatency;

        // Link Cable
        public ConfigEntry<bool> LinkCableEnabled;
        public ConfigEntry<int> LinkInputDelayFrames;
        public ConfigEntry<bool> HideLinkStatusWhenDisconnected;
        public ConfigEntry<bool> ShowPeerScreen;
        public ConfigEntry<string> PeerScreenPalette;

        public CodeDmgConfig(ConfigFile config)
        {
            RomPath = config.Bind(
                "Change Game ROM",
                "RomPath",
                "",
                "Path to ROM file (.gb or .gbc). Leave empty to use rom.gbc / rom.gb in plugin folder."
            );

            RomDirectory = config.Bind(
                "Change Game ROM",
                "RomDirectory",
                "",
                "Folder to scan for ROM files shown in the Change Game menu. \n Leave this field empty to use the default ROMs folder: \n \\Documents\\Bomb Rush Cyberfunk Modding\\BRCGameBoyEmu\\Roms"
            );

            LoadLastPlayed = config.Bind(
                "Change Game ROM",
                "LoadLastPlayed",
                true,
                "Load last ROM played automatically when booting the emulator. Overrides the RomPath above."
            );

            Up = config.Bind("Controls", "Up", KeyCode.W, "D-pad Up");
            Down = config.Bind("Controls", "Down", KeyCode.S, "D-pad Down");
            Left = config.Bind("Controls", "Left", KeyCode.A, "D-pad Left");
            Right = config.Bind("Controls", "Right", KeyCode.D, "D-pad Right");

            A = config.Bind("Controls", "A", KeyCode.Period, "GB A button");
            B = config.Bind("Controls", "B", KeyCode.Comma, "GB B button");
            Start = config.Bind("Controls", "Start", KeyCode.Return, "GB Start button");
            Select = config.Bind("Controls", "Select", KeyCode.RightShift, "GB Select button");

            SwapButtons = config.Bind(
                "Controls",
                "SwapButtons",
                false,
                "Swap A/B buttons on controllers. Useful for pads that use a Switch button layout, but are in XInput mode."
            );

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

            BatterySaveAutoSave = config.Bind(
                "SaveStates",
                "BatterySaveAutoSave",
                true,
                "Automatically write the battery save (.sav) file when closing the app."
            );

            BatterySaveAutoLoad = config.Bind(
                "SaveStates",
                "BatterySaveAutoLoad",
                true,
                "Automatically load the battery save (.sav) file when starting the emulator."
            );

            Palette = config.Bind(
                "Display",
                "Palette",
                Helper.DefaultPaletteName,
                new ConfigDescription(
                    "Select the palette used for Game Boy games. Default is the original olive-green DMG palette.",
                    new AcceptableValueList<string>(Helper.PaletteNames)
                )
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
                false,
                "Enable a GBC-style pixel grid overlay on the screen."
            );

            EnableAudio = config.Bind(
                "Music/Audio",
                "EnableAudio",
                true,
                "Enable Game Boy audio emulation."
            );

            Volume = config.Bind(
                "Music/Audio",
                "Volume",
                80,
                new ConfigDescription(
                    "Audio volume (0 = silent, 100 = full volume).",
                    new AcceptableValueRange<int>(0, 100)
                )
            );

            AudioLatency = config.Bind(
                "Music/Audio",
                "AudioLatency",
                "Normal",
                new ConfigDescription(
                    "Audio latency preset. The lower the value, the more chance for popping.",
                    new AcceptableValueList<string>("Very High", "High", "Normal", "Low")
                )
            );

            LinkCableEnabled = config.Bind(
                "Link Cable",
                "LinkCableEnabled",
                true,
                "Emulate the Game Boy Link Cable. Requires ACN. \n (Experimental feature!! Backup your saves before using!!)"
            );

            LinkInputDelayFrames = config.Bind(
                "Link Cable",
                "LinkInputDelayFrames",
                8,
                "Input delay setting for Link Cable emulation. The higher the value, the higher the input delay. Lower values will make gameplay choppy, lag, and much more likely to desync. Host's value is used."
            );

            HideLinkStatusWhenDisconnected = config.Bind(
                "Link Cable",
                "HideLinkStatusWhenDisconnected",
                true,
                "Hide the Link Cable status text when not connected with another player."
            );

            ShowPeerScreen = config.Bind(
                "Link Cable",
                "ShowPeerScreen",
                true,
                "Renders the second GB / GBC emulator used for link cable emulation. This emulator attempts to clone the other player's emulator state, but can desync. Disable for a slight performance increase."
            );

            PeerScreenPalette = config.Bind(
                "Link Cable",
                "PeerScreenPalette",
                Helper.DefaultPeerPaletteName,
                new ConfigDescription(
                    "Select the palette used by the second emulator. Default is a BRC-inspired DMG palette.",
                    new AcceptableValueList<string>(Helper.PaletteNames)
                )
            );
        }

        public int GetSampleFifoSize()
        {
            string latency = AudioLatency?.Value;

            if (string.IsNullOrWhiteSpace(latency))
                return 8192;

            switch (latency.Trim().ToLowerInvariant())
            {
                case "very high":
                    return 32768;

                case "high":
                    return 16384;

                case "low":
                    return 4096;

                case "normal":
                default:
                    return 8192;
            }
        }
    }
}