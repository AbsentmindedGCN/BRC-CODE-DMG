using System;
using System.IO;
using System.Text;
using CommonAPI;
using CommonAPI.Phone;
using Reptile;
using UnityEngine;

namespace BRCCodeDmg
{
    public class AppCodeDmg : CustomApp
    {
        private static Sprite IconSprite;
        private static bool   _initialized;

        private CodeDmgEmulator    _emulator;
        private CodeDmgRenderer    _renderer;
        private CodeDmgAudioDriver _audioDriver;

        // Tracks which ROM is currently loaded so hot-swaps can be detected.
        private string _loadedRomPath;

        public override bool Available => true;

        private const float TargetGameBoyFps = 59.7275f;
        private const float TargetFrameTime  = 1f / TargetGameBoyFps;
        private float _emulationTimeAccumulator = 0f;

        // ── Static init ───────────────────────────────────────────────────────
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            string iconPath = Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "appicon.png");
            if (File.Exists(iconPath))
                IconSprite = TextureUtility.LoadSprite(iconPath);

            if (IconSprite != null)
                PhoneAPI.RegisterApp<AppCodeDmg>("gb emu", IconSprite);
            else
                PhoneAPI.RegisterApp<AppCodeDmg>("gb emu");
        }

        // ── App lifecycle ─────────────────────────────────────────────────────
        public override void OnAppInit()
        {
            base.OnAppInit();

            if (IconSprite != null) CreateTitleBar("GB-EMU", IconSprite);
            else                    CreateIconlessTitleBar("GB-EMU");

            _renderer = new CodeDmgRenderer(this);
            _renderer.Build();

            _audioDriver = gameObject.GetComponent<CodeDmgAudioDriver>();
            if (_audioDriver == null)
                _audioDriver = gameObject.AddComponent<CodeDmgAudioDriver>();

            _audioDriver.SetMuted(true);

            // First boot — no state loading yet; that happens in OnAppEnable.
            TryBootEmulator();
            RenderNow();
        }

        public override void OnAppEnable()
        {
            base.OnAppEnable();

            // Force BepInEx to re-read the .cfg file
            CodeDmgPlugin.Instance.Config.Reload();
            CodeDmgPlugin.ConfigSettings = new CodeDmgConfig(CodeDmgPlugin.Instance.Config);

            CodeDmgState.AppActive      = true;
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

            // ── Step 1: Check whether the configured ROM has changed ──────────
            string configuredRom = GetConfiguredRomPath();
            bool romChanged = _emulator != null &&
                  !string.Equals(
                      Path.GetFullPath(configuredRom),
                      Path.GetFullPath(_loadedRomPath ?? string.Empty),
                      StringComparison.OrdinalIgnoreCase);

            if (romChanged)
            {
                Debug.Log("[CODE-DMG] ROM change detected — rebooting: " + configuredRom);

                // Save the outgoing session before tearing it down.
                if (ReadBoolFromConfig("SaveStates", "AutoSaveOnClose", true))
                    SaveState();
                if (ReadBoolFromConfig("SaveStates", "BatterySaveAutoSave", true))
                    _emulator.SaveRam();

                _emulator      = null;
                _loadedRomPath = null;
            }

            // ── Step 2: Boot the emulator if we don't have one running ────────
            if (_emulator == null)
                TryBootEmulator();

            // Step 3: Load save state — only after the correct ROM is confirmed running,
            // and only if the user has auto-load enabled.
            bool autoLoad = ReadBoolFromConfig("SaveStates", "AutoLoadOnOpen", true);

            if (_emulator != null && autoLoad)
            {
                string statePath = GetStatePath(_loadedRomPath);
                if (File.Exists(statePath))
                    TryLoadState();
            }

            RenderNow();
        }

        public override void OnAppDisable()
        {
            base.OnAppDisable();
            CodeDmgPlugin.Instance.Config.Reload();

            if (_audioDriver != null)
                _audioDriver.SetMuted(true);

            CodeDmgState.AppActive      = false;
            GBEmuCurrentState.AppActive = false;
            FlushCurrentPlayerInput();

            if (_emulator != null)
            {
                if (CodeDmgPlugin.ConfigSettings?.AutoSaveOnClose.Value ?? true)
                    SaveState();
                if (CodeDmgPlugin.ConfigSettings?.BatterySaveAutoSave.Value ?? true)
                    _emulator.SaveRam();
            }
        }

        public override void OnAppUpdate()
        {
            base.OnAppUpdate();

            if (_emulator == null || _renderer == null) return;

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

        // ── Boot ──────────────────────────────────────────────────────────────
        private void TryBootEmulator()
        {
            string romPath     = GetConfiguredRomPath();
            string bootRomPath = Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "dmg_boot.bin");
            string savePath    = GetBatterySavePath(romPath);

            if (!File.Exists(romPath))
            {
                Debug.LogWarning("[CODE-DMG] Missing ROM: " + romPath);
                _emulator      = null;
                _loadedRomPath = null;
                return;
            }

            _emulator      = new CodeDmgEmulator(romPath, bootRomPath, savePath);
            _loadedRomPath = romPath;

            // Battery save — gated by config.
            if (ReadBoolFromConfig("SaveStates", "BatterySaveAutoLoad", true))
                _emulator.LoadSaveRam();

            if (CodeDmgPlugin.ConfigSettings != null)
                _emulator.SetAudioEnabled(CodeDmgPlugin.ConfigSettings.EnableAudio.Value);
            else
                _emulator.SetAudioEnabled(false);

            if (_audioDriver != null)
                _audioDriver.SetEmulator(_emulator);
        }

        // ── Save state helpers ────────────────────────────────────────────────
        private void SaveState()
        {
            if (_emulator == null) return;
            try
            {
                File.WriteAllBytes(GetStatePath(_loadedRomPath), _emulator.SerializeState());
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CODE-DMG] Failed to save state: " + ex.Message);
            }
        }

        private void TryLoadState()
        {
            if (_emulator == null) return;

            string statePath = GetStatePath(_loadedRomPath);
            if (!File.Exists(statePath)) return;

            try
            {
                byte[] data = File.ReadAllBytes(statePath);

                var loaded = new CodeDmgEmulator(
                    _emulator.RomPath,
                    _emulator.BootRomPath,
                    _emulator.SavePath);

                if (CodeDmgPlugin.ConfigSettings != null)
                    loaded.SetAudioEnabled(CodeDmgPlugin.ConfigSettings.EnableAudio.Value);

                loaded.DeserializeState(data);
                _emulator = loaded;

                if (_audioDriver != null)
                    _audioDriver.SetEmulator(_emulator);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CODE-DMG] Failed to load state: " + ex.Message);
                try
                {
                    string bad = statePath + ".bad";
                    if (File.Exists(bad)) File.Delete(bad);
                    File.Move(statePath, bad);
                }
                catch { }

                TryBootEmulator();
                if (_audioDriver != null)
                    _audioDriver.SetEmulator(_emulator);
            }
        }

        // ── Path helpers ──────────────────────────────────────────────────────
        private static string GetSavesFolder()
        {
            string folder = Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "saves");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static string GetRomTitle(string romPath)
        {
            try
            {
                using (var fs = File.OpenRead(romPath))
                {
                    if (fs.Length < 0x0143) throw new Exception("ROM too short.");
                    fs.Seek(0x0134, SeekOrigin.Begin);
                    byte[] raw = new byte[15];
                    fs.Read(raw, 0, raw.Length);

                    var sb = new StringBuilder(15);
                    foreach (byte b in raw)
                    {
                        if (b == 0) break;
                        if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
                    }

                    string title = sb.ToString().Trim();
                    if (title.Length == 0) throw new Exception("Empty title.");

                    foreach (char c in Path.GetInvalidFileNameChars())
                        title = title.Replace(c.ToString(), "");

                    title = title.Trim();
                    if (title.Length == 0) throw new Exception("Title all-invalid.");
                    return title;
                }
            }
            catch
            {
                return Path.GetFileNameWithoutExtension(romPath);
            }
        }

        private static string GetBatterySavePath(string romPath)
        {
            return Path.Combine(GetSavesFolder(), GetRomTitle(romPath) + ".sav");
        }

        private static string GetStatePath(string romPath)
        {
            if (string.IsNullOrEmpty(romPath)) return string.Empty;
            return Path.Combine(GetSavesFolder(), GetRomTitle(romPath) + ".state");
        }

        private string GetConfiguredRomPath()
        {
            if (CodeDmgPlugin.ConfigSettings != null)
            {
                string configured = CodeDmgPlugin.ConfigSettings.RomPath.Value;
                if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                {
                    string ext = Path.GetExtension(configured).ToLowerInvariant();
                    if (ext == ".gb" || ext == ".gbc") return configured;
                }
            }

            string gbcPath = Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "rom.gbc");
            if (File.Exists(gbcPath)) return gbcPath;
            return Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "rom.gb");
        }

        /// Config Reload
        private static bool ReadBoolFromConfig(string section, string key, bool defaultValue)
        {
            try
            {
                string path = CodeDmgPlugin.Instance.Config.ConfigFilePath;
                if (!File.Exists(path)) return defaultValue;

                bool inSection = false;
                foreach (string line in File.ReadAllLines(path))
                {
                    string t = line.Trim();
                    if (t.StartsWith("[") && t.EndsWith("]"))
                    {
                        inSection = string.Equals(t.Substring(1, t.Length - 2),
                            section, StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!inSection) continue;

                    int eq = t.IndexOf('=');
                    if (eq < 0) continue;

                    string k = t.Substring(0, eq).Trim();
                    string v = t.Substring(eq + 1).Trim();

                    if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                    {
                        if (bool.TryParse(v, out bool result)) return result;
                        return defaultValue;
                    }
                }
            }
            catch { }
            return defaultValue;
        }

        // ── Input ─────────────────────────────────────────────────────────────
        private void HandleEmulatorInput()
        {
            if (_emulator == null || CodeDmgPlugin.ConfigSettings == null) return;

            CodeDmgConfig cfg = CodeDmgPlugin.ConfigSettings;
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");

            _emulator.SetButton(GameBoyButton.A,
                Input.GetKey(cfg.A.Value) || Input.GetKey(KeyCode.JoystickButton0));
            _emulator.SetButton(GameBoyButton.B,
                Input.GetKey(cfg.B.Value) || Input.GetKey(KeyCode.JoystickButton1));
            _emulator.SetButton(GameBoyButton.Start,
                Input.GetKey(cfg.Start.Value) || Input.GetKey(KeyCode.JoystickButton3));
            _emulator.SetButton(GameBoyButton.Select,
                Input.GetKey(cfg.Select.Value) || Input.GetKey(KeyCode.JoystickButton2));
            _emulator.SetButton(GameBoyButton.Right,
                Input.GetKey(cfg.Right.Value) || h > 0.5f);
            _emulator.SetButton(GameBoyButton.Left,
                Input.GetKey(cfg.Left.Value)  || h < -0.5f);
            _emulator.SetButton(GameBoyButton.Up,
                Input.GetKey(cfg.Up.Value)    || v > 0.5f);
            _emulator.SetButton(GameBoyButton.Down,
                Input.GetKey(cfg.Down.Value)  || v < -0.5f);
        }

        private void RenderNow()  => _renderer?.Render(_emulator);

        private void FlushCurrentPlayerInput()
        {
            Player player = WorldHandler.instance?.GetCurrentPlayer();
            if (player != null) player.FlushInput();
        }
    }
}
