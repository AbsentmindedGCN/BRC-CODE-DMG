using System;
using System.IO;
using CommonAPI;
using CommonAPI.Phone;
using Reptile;
using UnityEngine;

namespace BRCCodeDmg
{
    public class AppCodeDmg : CustomApp
    {
        private static Sprite IconSprite;
        private static bool _initialized;

        private CodeDmgEmulator _emulator;
        private CodeDmgRenderer _renderer;
        private CodeDmgAudioDriver _audioDriver;

        public override bool Available => true;

        private const float TargetGameBoyFps = 59.7275f;
        private const float TargetFrameTime = 1f / TargetGameBoyFps;

        private float _emulationTimeAccumulator = 0f;

        public static void Initialize()
        {
            if (_initialized)
                return;

            _initialized = true;

            string iconPath = Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "appicon.png");
            if (File.Exists(iconPath))
                IconSprite = TextureUtility.LoadSprite(iconPath);

            if (IconSprite != null)
                PhoneAPI.RegisterApp<AppCodeDmg>("gb emu", IconSprite);
            else
                PhoneAPI.RegisterApp<AppCodeDmg>("gb emu");
        }

        public override void OnAppInit()
        {
            base.OnAppInit();

            if (IconSprite != null)
                CreateTitleBar("GB-EMU", IconSprite);
            else
                CreateIconlessTitleBar("GB-EMU");

            _renderer = new CodeDmgRenderer(this);
            _renderer.Build();

            _audioDriver = gameObject.GetComponent<CodeDmgAudioDriver>();
            if (_audioDriver == null)
                _audioDriver = gameObject.AddComponent<CodeDmgAudioDriver>();

            _audioDriver.SetMuted(true);

            TryBootEmulator();
            RenderNow();
        }

        public override void OnAppEnable()
        {
            base.OnAppEnable();

            CodeDmgState.AppActive = true;
            GBEmuCurrentState.AppActive = true;
            FlushCurrentPlayerInput();

            _emulationTimeAccumulator = 0f;

            if (_renderer == null)
            {
                _renderer = new CodeDmgRenderer(this);
                _renderer.Build();
            }

            if (_audioDriver != null)
                _audioDriver.SetMuted(false);

            if (_emulator == null)
                TryBootEmulator();

            if (_emulator != null &&
                CodeDmgPlugin.ConfigSettings != null &&
                CodeDmgPlugin.ConfigSettings.AutoLoadOnOpen.Value)
            {
                string statePath = GetStatePath();
                if (File.Exists(statePath))
                    TryLoadState();
            }

            RenderNow();
        }

        public override void OnAppDisable()
        {
            base.OnAppDisable();

            if (_audioDriver != null)
                _audioDriver.SetMuted(true);

            CodeDmgState.AppActive = false;
            GBEmuCurrentState.AppActive = false;
            FlushCurrentPlayerInput();

            if (_emulator != null && CodeDmgPlugin.ConfigSettings != null && CodeDmgPlugin.ConfigSettings.AutoSaveOnClose.Value)
            {
                SaveState();
            }
            _emulator?.SaveRam();
        }

        public override void OnAppUpdate()
        {
            base.OnAppUpdate();

            if (_emulator == null || _renderer == null)
                return;

            HandleEmulatorInput();

            _emulationTimeAccumulator += Time.unscaledDeltaTime;

            if (_emulationTimeAccumulator > TargetFrameTime * 3f)
                _emulationTimeAccumulator = TargetFrameTime * 3f;

            bool renderedFrame = false;

            while (_emulationTimeAccumulator >= TargetFrameTime)
            {
                _emulationTimeAccumulator -= TargetFrameTime;
                _emulator.StepFrame();
                renderedFrame = true;
            }

            if (renderedFrame && _emulator.FrameDirty)
                _renderer.Render(_emulator);
        }

        private void TryBootEmulator()
        {
            string romPath = GetConfiguredRomPath();
            string bootRomPath = Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "dmg_boot.bin");
            string savePath = GetBatterySavePath(romPath);

            if (!File.Exists(romPath))
            {
                Debug.LogWarning("[CODE-DMG] Missing ROM. Checked: " + romPath);
                _emulator = null;
                return;
            }

            _emulator = new CodeDmgEmulator(romPath, bootRomPath, savePath);
            if (_audioDriver != null)
                _audioDriver.SetEmulator(_emulator);
        }

        private string GetConfiguredRomPath()
        {
            if (CodeDmgPlugin.ConfigSettings != null)
            {
                string configuredPath = CodeDmgPlugin.ConfigSettings.RomPath.Value;

                if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
                    return configuredPath;
            }

            return Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "rom.gb");
        }

        private string GetBatterySavePath(string romPath)
        {
            string romDirectory = Path.GetDirectoryName(romPath);
            string romName = Path.GetFileNameWithoutExtension(romPath);

            if (string.IsNullOrEmpty(romDirectory))
                romDirectory = CodeDmgPlugin.Instance.PluginDirectory;

            return Path.Combine(romDirectory, romName + ".sav");
        }

        private string GetStatePath()
        {
            string romPath = GetConfiguredRomPath();
            string romDirectory = Path.GetDirectoryName(romPath);
            string romName = Path.GetFileNameWithoutExtension(romPath);

            if (string.IsNullOrEmpty(romDirectory))
                romDirectory = CodeDmgPlugin.Instance.PluginDirectory;

            return Path.Combine(romDirectory, romName + ".state");
        }

        private void SaveState()
        {
            if (_emulator == null)
                return;

            try
            {
                byte[] stateData = _emulator.SerializeState();
                File.WriteAllBytes(GetStatePath(), stateData);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CODE-DMG] Failed to save state: " + ex.Message);
            }
        }

        /*
        private void TryLoadState()
        {
            if (_emulator == null)
                return;

            string statePath = GetStatePath();
            if (!File.Exists(statePath))
                return;

            try
            {
                byte[] stateData = File.ReadAllBytes(statePath);
                _emulator.DeserializeState(stateData);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CODE-DMG] Failed to load state: " + ex.Message);
            }
        }
        */

        private void TryLoadState()
        {
            if (_emulator == null)
                return;

            string statePath = GetStatePath();
            if (!File.Exists(statePath))
                return;

            try
            {
                byte[] stateData = File.ReadAllBytes(statePath);

                CodeDmgEmulator loaded = new CodeDmgEmulator(
                    _emulator.RomPath,
                    _emulator.BootRomPath,
                    _emulator.SavePath
                );

                loaded.DeserializeState(stateData);
                _emulator = loaded;

                if (_audioDriver != null)
                    _audioDriver.SetEmulator(_emulator);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CODE-DMG] Failed to load state: " + ex.Message);

                try
                {
                    string badPath = statePath + ".bad";
                    if (File.Exists(badPath))
                        File.Delete(badPath);

                    File.Move(statePath, badPath);
                }
                catch (Exception renameEx)
                {
                    Debug.LogWarning("[CODE-DMG] Failed to quarantine bad state file: " + renameEx.Message);
                }

                TryBootEmulator();
                if (_audioDriver != null)
                    _audioDriver.SetEmulator(_emulator);
            }
        }

        private void HandleEmulatorInput()
        {
            if (_emulator == null || CodeDmgPlugin.ConfigSettings == null)
                return;

            CodeDmgConfig cfg = CodeDmgPlugin.ConfigSettings;

            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");

            // --- A / B ---
            _emulator.SetButton(
                GameBoyButton.A,
                Input.GetKey(cfg.A.Value) ||
                Input.GetKey(KeyCode.JoystickButton0) // Controller A
            );

            _emulator.SetButton(
                GameBoyButton.B,
                Input.GetKey(cfg.B.Value) ||
                Input.GetKey(KeyCode.JoystickButton1) // Controller B
            );

            // --- Start / Select ---
            _emulator.SetButton(
                GameBoyButton.Start,
                Input.GetKey(cfg.Start.Value) ||
                Input.GetKey(KeyCode.JoystickButton3) // Controller Y
            );

            _emulator.SetButton(
                GameBoyButton.Select,
                Input.GetKey(cfg.Select.Value) ||
                Input.GetKey(KeyCode.JoystickButton2) // Controller X
            );

            // --- D-Pad ---
            _emulator.SetButton(
                GameBoyButton.Right,
                Input.GetKey(cfg.Right.Value) || h > 0.5f
            );

            _emulator.SetButton(
                GameBoyButton.Left,
                Input.GetKey(cfg.Left.Value) || h < -0.5f
            );

            _emulator.SetButton(
                GameBoyButton.Up,
                Input.GetKey(cfg.Up.Value) || v > 0.5f
            );

            _emulator.SetButton(
                GameBoyButton.Down,
                Input.GetKey(cfg.Down.Value) || v < -0.5f
            );
        }

        private void RenderNow()
        {
            if (_renderer == null)
                return;

            _renderer.Render(_emulator);
        }

        private void FlushCurrentPlayerInput()
        {
            Player player = WorldHandler.instance?.GetCurrentPlayer();
            if (player != null)
                player.FlushInput();
        }
    }
}