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
            string romPath     = GetConfiguredRomPath();
            string bootRomPath = Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "dmg_boot.bin");
            string savePath    = GetBatterySavePath(romPath);
            string statePath   = GetStatePath();
            string md5Path     = GetMd5Path(romPath);

            if (!File.Exists(romPath))
            {
                Debug.LogWarning("[CODE-DMG] Missing ROM. Checked: " + romPath);
                _emulator = null;
                return;
            }

            // ── MD5 ROM integrity check ───────────────────────────────────────
            byte[] romBytes  = File.ReadAllBytes(romPath);
            string romTitle  = ReadRomTitle(romBytes);
            string currentMd5 = ComputeMd5(romBytes);

            string storedMd5 = null;
            if (File.Exists(md5Path))
            {
                try { storedMd5 = File.ReadAllText(md5Path).Trim(); }
                catch { /* treat as missing */ }
            }

            if (storedMd5 == null)
            {
                // First boot with this ROM — write the fingerprint and continue.
                WriteMd5(md5Path, currentMd5);
                Debug.Log($"[CODE-DMG] ROM fingerprinted: \"{romTitle}\" ({currentMd5})");
            }
            else if (!string.Equals(storedMd5, currentMd5, StringComparison.OrdinalIgnoreCase))
            {
                // ROM has changed — rotate old save/state files and update fingerprint.
                Debug.LogWarning($"[CODE-DMG] ROM changed! Was {storedMd5}, now {currentMd5} (\"{romTitle}\"). Rotating save files.");
                RotateFile(savePath);
                RotateFile(statePath);
                WriteMd5(md5Path, currentMd5);
            }
            // ─────────────────────────────────────────────────────────────────

            // ── Save-states-disabled cleanup ──────────────────────────────────
            bool autoSave = CodeDmgPlugin.ConfigSettings?.AutoSaveOnClose.Value ?? true;
            bool autoLoad = CodeDmgPlugin.ConfigSettings?.AutoLoadOnOpen.Value ?? true;
            if (!autoSave && !autoLoad)
            {
                RotateFile(statePath);
                RotateFile(savePath);
            }
            // ─────────────────────────────────────────────────────────────────

            _emulator = new CodeDmgEmulator(romPath, bootRomPath, savePath);

            if (CodeDmgPlugin.ConfigSettings != null)
                _emulator.SetAudioEnabled(CodeDmgPlugin.ConfigSettings.EnableAudio.Value);
            else
                _emulator.SetAudioEnabled(false);

            if (_audioDriver != null)
                _audioDriver.SetEmulator(_emulator);
        }

        // ── ROM helpers ───────────────────────────────────────────────────────

        /// <summary>Computes the MD5 of <paramref name="data"/> and returns it as a lowercase hex string.</summary>
        private static string ComputeMd5(byte[] data)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] hash = md5.ComputeHash(data);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>Reads the ROM title from header bytes 0x134–0x143, trimmed of null/whitespace.</summary>
        private static string ReadRomTitle(byte[] rom)
        {
            if (rom.Length < 0x144) return "Unknown";
            // GBC games use 0x134–0x13E (11 bytes); DMG uses up to 0x143 (16 bytes).
            // Reading all 16 and trimming is safe for both.
            int len = Math.Min(16, rom.Length - 0x134);
            string raw = System.Text.Encoding.ASCII.GetString(rom, 0x134, len);
            return raw.TrimEnd('\0', ' ');
        }

        private static string GetMd5Path(string romPath)
        {
            string dir  = Path.GetDirectoryName(romPath) ?? CodeDmgPlugin.Instance.PluginDirectory;
            string name = Path.GetFileNameWithoutExtension(romPath);
            return Path.Combine(dir, name + ".md5");
        }

        private static void WriteMd5(string md5Path, string hash)
        {
            try { File.WriteAllText(md5Path, hash); }
            catch (Exception ex) { Debug.LogWarning("[CODE-DMG] Could not write MD5 file: " + ex.Message); }
        }

        /// <summary>
        /// Renames <paramref name="path"/> to the next available <c>-old1</c>, <c>-old2</c>, …
        /// slot so that no existing backup is ever overwritten.
        /// </summary>
        private static void RotateFile(string path)
        {
            if (!File.Exists(path)) return;

            string dir  = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileNameWithoutExtension(path);
            string ext  = Path.GetExtension(path);

            int n = 1;
            string dest;
            do
            {
                dest = Path.Combine(dir, $"{name}-old{n}{ext}");
                n++;
            }
            while (File.Exists(dest));

            try
            {
                File.Move(path, dest);
                Debug.Log($"[CODE-DMG] Rotated: {Path.GetFileName(path)} → {Path.GetFileName(dest)}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CODE-DMG] Could not rotate {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        private static string PeekStateRomPath(string statePath)
        {
            if (!File.Exists(statePath)) return null;
            try
            {
                using (FileStream fs = File.OpenRead(statePath))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    int version = br.ReadInt32();
                    if (version < 1) return null;
                    return br.ReadString();
                }
            }
            catch { return null; }
        }

        private string GetConfiguredRomPath()
        {
            if (CodeDmgPlugin.ConfigSettings != null)
            {
                string configuredPath = CodeDmgPlugin.ConfigSettings.RomPath.Value;

                if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
                {
                    string ext = Path.GetExtension(configuredPath).ToLowerInvariant();
                    if (ext == ".gb" || ext == ".gbc")
                        return configuredPath;
                }
            }

            // Try rom.gbc first, then fall back to rom.gb
            string gbcPath = Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "rom.gbc");
            if (File.Exists(gbcPath))
                return gbcPath;

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

                if (CodeDmgPlugin.ConfigSettings != null)
                    loaded.SetAudioEnabled(CodeDmgPlugin.ConfigSettings.EnableAudio.Value);
                else
                    loaded.SetAudioEnabled(false);

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

            // Use joystick-specific axes (Joystick Axis 1/2) rather than Unity's
            // general "Horizontal"/"Vertical" axes, which also pick up BRC's keyboard
            // bindings and cause spurious dpad presses when Z/X/S are held.
            float h = Input.GetAxisRaw("Joystick Axis 1");
            float v = Input.GetAxisRaw("Joystick Axis 2");

            _emulator.SetButton(
                GameBoyButton.A,
                Input.GetKey(cfg.A.Value) ||
                Input.GetKey(KeyCode.JoystickButton0)
            );

            _emulator.SetButton(
                GameBoyButton.B,
                Input.GetKey(cfg.B.Value) ||
                Input.GetKey(KeyCode.JoystickButton1)
            );

            _emulator.SetButton(
                GameBoyButton.Start,
                Input.GetKey(cfg.Start.Value) ||
                Input.GetKey(KeyCode.JoystickButton3)
            );

            _emulator.SetButton(
                GameBoyButton.Select,
                Input.GetKey(cfg.Select.Value) ||
                Input.GetKey(KeyCode.JoystickButton2)
            );

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