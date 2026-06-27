using System;
using System.IO;
using System.Reflection;
using System.Text;
using BombRushMP.Plugin;
using CommonAPI;
using CommonAPI.Phone;
using Reptile;
using UnityEngine;
using UnityEngine.EventSystems;
using TMPro;

namespace BRCCodeDmg
{
    public class AppCodeDmg : CustomApp
    {
        private static Sprite IconSprite;
        private static bool   _initialized;
        private static AppCodeDmg _activeInstance;

        private CodeDmgEmulator    _emulator;
        private CodeDmgRenderer    _renderer;
        private CodeDmgAudioDriver _audioDriver;

        // Tracks which ROM is currently loaded so hot-swaps can be detected.
        private string _loadedRomPath;

        // Chat fix
        private static PropertyInfo _slopChatInputBlockedProperty;
        private static bool _slopChatReflectionInitialized;
        private bool _wasChatActive;

        public CodeDmgEmulator GetEmulator() => _emulator;

        public override bool Available => true;

        private const float TargetGameBoyFps = 59.7275f;
        private const float TargetFrameTime  = 1f / TargetGameBoyFps;
        private float _emulationTimeAccumulator = 0f;

        // Start+Select Hold to Open Menu for Pads
        private const float LinkHoldRequired = 1f;
        private float _linkHoldTimer = 0f;
        private bool _linkHoldFired = false;
        private float _suppressEmulatorInputUntil = -1f;
        private static float _rightMenuOpenProbeUntil = -1f;
        private static bool _rightMenuOpenBlockedUntilRelease;

        // Hold R to reboot ROM
        /*
        private const float RebootHoldRequired = 3f;
        private float _rebootHoldTimer = 0f;
        private bool _rebootHoldFired = false;
        */

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
            _activeInstance = this;

            if (IconSprite != null) CreateTitleBar("GB-EMU", IconSprite);
            else                    CreateIconlessTitleBar("GB-EMU");

            _renderer = new CodeDmgRenderer(this);
            _renderer.Build();

            _audioDriver = gameObject.GetComponent<CodeDmgAudioDriver>();
            if (_audioDriver == null)
                _audioDriver = gameObject.AddComponent<CodeDmgAudioDriver>();

            _audioDriver.SetMuted(true);

            //TryBootEmulator();
            RenderNow();
        }
        public override void OnAppEnable()
        {
            base.OnAppEnable();
            _activeInstance = this;
            BeginRightMenuOpenReleaseGuard();

            CodeDmgPlugin.Instance.Config.Reload();
            CodeDmgPlugin.ConfigSettings = new CodeDmgConfig(CodeDmgPlugin.Instance.Config);
            Helper.paletteName = CodeDmgPlugin.ConfigSettings?.Palette?.Value;

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

            string configuredRom = GetConfiguredRomPath();
            string loadedRom = NormalizeConfiguredRomPath(_loadedRomPath ?? string.Empty);

            bool romChanged = _emulator != null && !string.Equals(
                Path.GetFullPath(configuredRom),
                Path.GetFullPath(loadedRom),
                StringComparison.OrdinalIgnoreCase);

            if (romChanged)
            {
                if (ReadBoolFromConfig("SaveStates", "AutoSaveOnClose", true))
                    SaveState();

                if (ReadBoolFromConfig("SaveStates", "BatterySaveAutoSave", true))
                    _emulator.SaveRam();

                _emulator = null;
            }

            if (_emulator == null)
                TryBootEmulator();

            bool autoLoad = ReadBoolFromConfig("SaveStates", "AutoLoadOnOpen", true);
            if (_emulator != null && autoLoad)
                TryLoadState();

            // Register with Link Cable Manager
            CodeDmgPlugin.LinkCable?.SetApp(this);

            if (_emulator != null && CodeDmgPlugin.LinkCable != null)
                CodeDmgPlugin.LinkCable.SetLocalRomHash(_emulator.GetRomHash());

            if (CodeDmgPlugin.LinkCable != null)
                CodeDmgPlugin.LinkCable.StateChanged += OnLinkCableStateChanged;

            // Check for pending invites already sent
            CheckPendingInviteOnOpen();

            RenderNow();
        }


        public override void OnAppDisable()
        {
            base.OnAppDisable();

            if (_activeInstance == this)
                _activeInstance = null;

            _renderer?.Teardown();

            if (_audioDriver != null)
                _audioDriver.SetMuted(true);

            CodeDmgState.AppActive = false;
            GBEmuCurrentState.AppActive = false;

            FlushCurrentPlayerInput();

            if (_emulator != null)
            {
                if (CodeDmgPlugin.ConfigSettings != null &&
                    CodeDmgPlugin.ConfigSettings.AutoSaveOnClose.Value)
                {
                    SaveState();
                }

                // Only save cartridge RAM, like a real GBC
                if (CodeDmgPlugin.ConfigSettings != null &&
                    CodeDmgPlugin.ConfigSettings.AutoSaveOnClose.Value)
                {
                    _emulator.SaveRam();
                }
            }

            CodeDmgPlugin.LinkCable?.Drop(); // Kill the link cable if the app closes

            if (CodeDmgPlugin.LinkCable != null)
                CodeDmgPlugin.LinkCable.StateChanged -= OnLinkCableStateChanged;

            // Force a fresh session next time the app opens.
            _emulator = null;
            _emulationTimeAccumulator = 0f;
            _linkHoldTimer = 0f;
            _linkHoldFired = false;
            // _rebootHoldTimer = 0f;
            // _rebootHoldFired = false;

            if (_audioDriver != null)
                _audioDriver.SetEmulator(null);
        }

        public override void OnAppUpdate()
        {
            base.OnAppUpdate();

            // Tick link cable manager (timeout, ROM mismatch timer)
            if (CodeDmgPlugin.ConfigSettings?.LinkCableEnabled.Value == true)
                CodeDmgPlugin.LinkCable?.Tick(Time.unscaledDeltaTime);

            // Pending Invites
            if (CodeDmgPlugin.ConfigSettings?.LinkCableEnabled.Value == true &&
                CodeDmgPlugin.LinkCable != null &&
                CodeDmgPlugin.LinkCable.HasPendingInvite &&
                !LinkCableConnectDialog.IsVisible &&
                !MasterMenu.IsVisible &&
                !RomSelectMenu.IsVisible &&
                !VolumeMenu.IsVisible &&
                !GBPaletteMenu.IsVisible)
            {
                ShowConnectDialog();
            }

            if (_emulator == null || _renderer == null) return;

            UpdateRightMenuOpenReleaseGuard();

            if (Input.GetKeyDown(KeyCode.RightArrow) && !ShouldBlockRightMenuOpenSignal())
                TryOpenMasterMenuFromRightInput();

            // Hold Start+Select for GB-Emu Master Menu
            bool suppressStartSelect = false;
            if (!MasterMenu.IsVisible &&
                !LinkCablePlayerList.IsVisible &&
                !LinkCableConnectDialog.IsVisible &&
                !RomSelectMenu.IsVisible &&
                !VolumeMenu.IsVisible &&
                !GBPaletteMenu.IsVisible)
            {
                CodeDmgConfig cfg = CodeDmgPlugin.ConfigSettings;
                bool startHeld  = cfg != null && (Input.GetKey(cfg.Start.Value)  || Input.GetKey(KeyCode.JoystickButton3));
                bool selectHeld = cfg != null && (Input.GetKey(cfg.Select.Value) || Input.GetKey(KeyCode.JoystickButton2));

                if (startHeld && selectHeld)
                {
                    suppressStartSelect = true;
                    _linkHoldTimer += Time.unscaledDeltaTime;
                    if (_linkHoldTimer >= LinkHoldRequired && !_linkHoldFired)
                    {
                        _linkHoldFired = true;
                        OpenMasterMenu();
                    }
                }
                else
                {
                    _linkHoldTimer = 0f;
                    _linkHoldFired = false;
                }
            }

            HandleEmulatorInput(suppressStartSelect);

            /*
            if (!MasterMenu.IsVisible && !LinkCablePlayerList.IsVisible && !LinkCableConnectDialog.IsVisible && !RomSelectMenu.IsVisible && !VolumeMenu.IsVisible && !GBPaletteMenu.IsVisible)
            {
                if (Input.GetKey(KeyCode.R))
                {
                    _rebootHoldTimer += Time.unscaledDeltaTime;
                    if (_rebootHoldTimer >= RebootHoldRequired && !_rebootHoldFired)
                    {
                        _rebootHoldFired = true;
                        RebootEmulator();
                    }
                }
                else
                {
                    _rebootHoldTimer = 0f;
                    _rebootHoldFired = false;
                }
            }
            */

            _emulationTimeAccumulator += Time.unscaledDeltaTime;
            if (_emulationTimeAccumulator > TargetFrameTime * 3f)
                _emulationTimeAccumulator = TargetFrameTime * 3f;

            bool renderedFrame = false;
            while (_emulationTimeAccumulator >= TargetFrameTime)
            {
                if (_emulator.ShouldYieldForSerialLink())
                {
                    _emulator.TickSerialLinkWait(Time.unscaledDeltaTime);
                    _emulationTimeAccumulator = 0f;
                    break;
                }

                _emulationTimeAccumulator -= TargetFrameTime;
                _emulator.StepFrame();
                renderedFrame = true;
            }

            if (renderedFrame && _emulator.FrameDirty)
                _renderer.Render(_emulator);
        }

        internal static void PlayMenuSelectSFX()
        {
            _activeInstance?.m_AudioManager.PlaySfxGameplay(SfxCollectionID.PhoneSfx, AudioClipID.FlipPhone_Select, 0f);
        }

        internal static void PlayMenuConfirmSFX()
        {
            _activeInstance?.m_AudioManager.PlaySfxGameplay(SfxCollectionID.PhoneSfx, AudioClipID.FlipPhone_Confirm, 0f);
        }

        internal static void PlayMenuBackSFX()
        {
            _activeInstance?.m_AudioManager.PlaySfxGameplay(SfxCollectionID.PhoneSfx, AudioClipID.FlipPhone_Back, 0f);
        }

        // ── Link Cable ────────────────────────────────────────────────────────
        internal static void HandleGamePauseStarted()
        {
            var app = _activeInstance;
            if (app == null) return;

            var core = Core.Instance;
            if (core != null && !core.IsCorePaused) return;

            app._renderer?.HideSecondPlayerScreen();
            app._emulationTimeAccumulator = 0f;

            var lc = CodeDmgPlugin.LinkCable;
            if (lc != null && (lc.State != LinkCableState.Disconnected || lc.HasPendingInvite))
                lc.Drop();
        }

        private void OnLinkCableStateChanged()
        {
            _renderer?.RenderLinkCableStatus(CodeDmgPlugin.LinkCable);
        }

        private void CheckPendingInviteOnOpen()
        {
            var lc = CodeDmgPlugin.LinkCable;
            if (lc != null && lc.HasPendingInvite)
                ShowConnectDialog();
        }

        private void ShowConnectDialog()
        {
            var lc = CodeDmgPlugin.LinkCable;
            if (CodeDmgPlugin.ConfigSettings?.LinkCableEnabled.Value != true || lc == null) return;
            string hostName = lc.PendingInviterName ?? "Host";
            LinkCableConnectDialog.Show(
                hostName,
                onYes: () => lc.ClientAcceptInvite(),
                onNo:  () => lc.ClientDeclineInvite()
            );
        }

        internal void OnLinkButtonPressed()
        {
            var lc = CodeDmgPlugin.LinkCable;
            if (CodeDmgPlugin.ConfigSettings?.LinkCableEnabled.Value != true || lc == null) return;

            if (lc.State != LinkCableState.Disconnected)
            {
                lc.Drop();
                return;
            }

            BackupSaveData();

            string localName = GetLocalPlayerDisplayName();
            LinkCablePlayerList.Show((targetId, targetName) =>
            {
                lc.HostSendInvite(targetId, localName, targetName);
                _renderer?.RenderLinkCableStatus(lc);
            });
        }

        private static string GetLocalPlayerDisplayName()
        {
            try
            {
                var cc = ClientController.Instance;
                if (cc == null) return string.Empty;
                ushort localId = cc.LocalID;
                if (cc.Players == null || !cc.Players.TryGetValue(localId, out var localPlayer) || localPlayer == null)
                    return string.Empty;
                string name = MPUtility.GetPlayerDisplayName(localPlayer.ClientState);
                return string.IsNullOrWhiteSpace(name) ? string.Empty : name;
            }
            catch { }
            return string.Empty;
        }

        private void RebootEmulator()
        {
            CodeDmgPlugin.LinkCable?.Drop();

            if (_emulator == null) return;

            if (CodeDmgPlugin.ConfigSettings?.BatterySaveAutoSave.Value == true)
                _emulator.SaveRam();

            string romPath  = _loadedRomPath ?? GetConfiguredRomPath();
            string bootPath = _emulator.BootRomPath;
            string savePath = _emulator.SavePath;

            if (_audioDriver != null)
                _audioDriver.SetEmulator(null);

            _emulator = new CodeDmgEmulator(romPath, bootPath, savePath);

            if (CodeDmgPlugin.ConfigSettings != null)
                _emulator.SetAudioEnabled(CodeDmgPlugin.ConfigSettings.EnableAudio.Value);

            if (CodeDmgPlugin.ConfigSettings?.BatterySaveAutoLoad.Value == true)
                _emulator.LoadSaveRam();

            if (_audioDriver != null)
                _audioDriver.SetEmulator(_emulator);

            _loadedRomPath = romPath;
            _emulationTimeAccumulator = 0f;
            _linkHoldTimer = 0f;
            _linkHoldFired = false;

            CodeDmgPlugin.LinkCable?.SetApp(this);
            CodeDmgPlugin.LinkCable?.SetLocalRomHash(_emulator.GetRomHash());

            RenderNow();
        }

        private void OpenMasterMenu()
        {
            MasterMenu.Show(
                onChangeGame: () => RomSelectMenu.Show(BootRom),
                onLinkCable:  () => OnLinkButtonPressed(),
                onVolume:     () => VolumeMenu.Show(),
                onGBPalette:  () => GBPaletteMenu.Show(),
                onReboot:     () => RebootEmulator()
            );
        }

        internal static bool TryOpenMasterMenuFromRightInput()
        {
            var app = _activeInstance;
            if (app == null || app._emulator == null || app._renderer == null)
                return false;

            if (IsChatInputActive() || PopupState.AnyVisible || ShouldBlockRightMenuOpenSignal())
                return false;

            app.OpenMasterMenu();
            app.SuppressEmulatorInputFor(0.2f);
            PopupState.SuppressPhoneNavFor(0.2f);
            return true;
        }

        // ROM Select Menu ROM Booter
        internal void BootRom(string romPath)
        {
            if (string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath)) return;

            SuppressEmulatorInputFor(0.5f);

            CodeDmgPlugin.LinkCable?.Drop();

            if (_emulator != null)
            {
                if (CodeDmgPlugin.ConfigSettings?.AutoSaveOnClose.Value == true)
                    SaveState();
                if (CodeDmgPlugin.ConfigSettings?.BatterySaveAutoSave.Value == true)
                    _emulator.SaveRam();
            }

            if (_audioDriver != null)
                _audioDriver.SetEmulator(null);

            string bootPath = Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "dmg_boot.bin");
            string savePath = GetBatterySavePath(romPath);

            _emulator = new CodeDmgEmulator(romPath, bootPath, savePath);
            _loadedRomPath = romPath;
            SaveLastRomPath(romPath);

            if (CodeDmgPlugin.ConfigSettings != null)
                _emulator.SetAudioEnabled(CodeDmgPlugin.ConfigSettings.EnableAudio.Value);

            if (CodeDmgPlugin.ConfigSettings?.BatterySaveAutoLoad.Value == true)
                _emulator.LoadSaveRam();

            if (_audioDriver != null)
                _audioDriver.SetEmulator(_emulator);

            _emulationTimeAccumulator = 0f;
            _linkHoldTimer = 0f;
            _linkHoldFired = false;

            if (CodeDmgPlugin.ConfigSettings?.AutoLoadOnOpen.Value == true)
                TryLoadState();

            CodeDmgPlugin.LinkCable?.SetApp(this);
            CodeDmgPlugin.LinkCable?.SetLocalRomHash(_emulator.GetRomHash());

            RenderNow();
        }

        private void BackupSaveData()
        {
            if (_emulator == null) return;
            try
            {
                string statePath = GetStatePath(_loadedRomPath ?? GetConfiguredRomPath());
                if (File.Exists(statePath))
                {
                    string backup = statePath + ".linkbackup";
                    File.Copy(statePath, backup, true);
                }

                string romPath = _loadedRomPath ?? GetConfiguredRomPath();
                string savPath = GetBatterySavePath(romPath);
                if (File.Exists(savPath))
                {
                    string backup = savPath + ".linkbackup";
                    File.Copy(savPath, backup, true);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CODE-DMG] Link cable save backup failed: " + ex.Message);
            }
        }

        // ── Boot ──────────────────────────────────────────────────────────────
        private void TryBootEmulator()
        {
            string romPath = GetConfiguredRomPath();
            _loadedRomPath = romPath;

            string bootRomPath = Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "dmg_boot.bin");
            string savePath = GetBatterySavePath(romPath);

            if (!File.Exists(romPath))
            {
                Debug.LogWarning("[CODE-DMG] Missing ROM: " + romPath);
                _emulator = null;
                _loadedRomPath = null;
                return;
            }

            _emulator = new CodeDmgEmulator(romPath, bootRomPath, savePath);
            SaveLastRomPath(romPath);

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

            string romPath = !string.IsNullOrWhiteSpace(_loadedRomPath)
                ? _loadedRomPath
                : GetConfiguredRomPath();

            if (string.IsNullOrWhiteSpace(romPath))
                return;

            try
            {
                File.WriteAllBytes(GetStatePath(romPath), _emulator.SerializeState());
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CODE-DMG] Failed to save state: " + ex.Message);
            }
        }

        private void TryLoadState()
        {
            if (_emulator == null) return;

            string romPath = !string.IsNullOrWhiteSpace(_loadedRomPath)
                ? _loadedRomPath
                : GetConfiguredRomPath();

            if (string.IsNullOrWhiteSpace(romPath))
                return;

            string statePath = GetStatePath(romPath);
            if (!File.Exists(statePath)) return;

            try
            {
                byte[] data = File.ReadAllBytes(statePath);

                var loaded = new CodeDmgEmulator(
                    romPath,
                    _emulator.BootRomPath,
                    _emulator.SavePath
                );

                if (CodeDmgPlugin.ConfigSettings != null)
                    loaded.SetAudioEnabled(CodeDmgPlugin.ConfigSettings.EnableAudio.Value);

                loaded.DeserializeState(data);
                _emulator = loaded;
                _loadedRomPath = romPath;

                if (_audioDriver != null)
                    _audioDriver.SetEmulator(_emulator);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CODE-DMG] Failed to load state: " + ex.Message);
            }
        }

        // ── Path helpers ──────────────────────────────────────────────────────
        private static string GetDataFolder()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Bomb Rush Cyberfunk Modding",
                "BRCGameBoyEmu");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static string GetSavesFolder()
        {
            string folder = Path.Combine(GetDataFolder(), "Saves");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static string GetLastRomPathFile()
        {
            return Path.Combine(GetDataFolder(), "LastRomDir");
        }

        private static string ReadLastRomPath()
        {
            try
            {
                string path = GetLastRomPathFile();
                if (!File.Exists(path)) return string.Empty;
                string romPath = NormalizeConfiguredRomPath(File.ReadAllText(path));
                if (string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath)) return string.Empty;
                string ext = Path.GetExtension(romPath).ToLowerInvariant();
                return ext == ".gb" || ext == ".gbc" ? romPath : string.Empty;
            }
            catch { }
            return string.Empty;
        }

        private static void SaveLastRomPath(string romPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath)) return;
                File.WriteAllText(GetLastRomPathFile(), romPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CODE-DMG] Failed to save last ROM path: " + ex.Message);
            }
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
                if (CodeDmgPlugin.ConfigSettings.LoadLastPlayed.Value)
                {
                    string lastRom = ReadLastRomPath();
                    if (!string.IsNullOrWhiteSpace(lastRom))
                        return lastRom;
                }

                string configured = CodeDmgPlugin.ConfigSettings.RomPath.Value;
                string normalized = NormalizeConfiguredRomPath(configured);

                // If normalization fixed the path, write it back so the config self-heals.
                if (!string.Equals(configured, normalized, StringComparison.Ordinal))
                {
                    CodeDmgPlugin.ConfigSettings.RomPath.Value = normalized;
                    try
                    {
                        CodeDmgPlugin.Instance.Config.Save();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[CODE-DMG] Failed to save normalized RomPath: " + ex.Message);
                    }
                }

                if (!string.IsNullOrWhiteSpace(normalized) && File.Exists(normalized))
                {
                    string ext = Path.GetExtension(normalized).ToLowerInvariant();
                    if (ext == ".gb" || ext == ".gbc")
                        return normalized;
                }
            }

            string gbcPath = Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "rom.gbc");
            if (File.Exists(gbcPath))
                return gbcPath;

            return Path.Combine(CodeDmgPlugin.Instance.PluginDirectory, "rom.gb");
        }

        private static string NormalizeConfiguredRomPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            string normalized = path.Trim();

            // Fix escaped apostrophes written as \'
            normalized = normalized.Replace("\\'", "'");

            // Fix escaped quotes just in case
            normalized = normalized.Replace("\\\"", "\"");

            // If the config stored the whole path wrapped in quotes, remove them
            if (normalized.Length >= 2 &&
                normalized[0] == '"' &&
                normalized[normalized.Length - 1] == '"')
            {
                normalized = normalized.Substring(1, normalized.Length - 2);
            }

            return normalized;
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

        // Shutdown Emu
        private void ShutdownEmulator(bool saveState, bool saveBatteryRam)
        {
            if (_emulator != null)
            {
                if (saveState)
                    SaveState();

                if (saveBatteryRam)
                    _emulator.SaveRam();
            }

            _emulator = null;
            _loadedRomPath = null;
            _emulationTimeAccumulator = 0f;

            if (_audioDriver != null)
                _audioDriver.SetEmulator(null);
        }

        // ── Input ─────────────────────────────────────────────────────────────
        private void HandleEmulatorInput(bool suppressStartSelect = false)
        {
            if (_emulator == null || CodeDmgPlugin.ConfigSettings == null) return;

            if (Time.unscaledTime <= _suppressEmulatorInputUntil)
            {
                ReleaseAllButtons();
                return;
            }

            // Ignore input when menus are visible
            if (MasterMenu.IsVisible || LinkCablePlayerList.IsVisible || LinkCableConnectDialog.IsVisible || RomSelectMenu.IsVisible || VolumeMenu.IsVisible || GBPaletteMenu.IsVisible)
            {
                ReleaseAllButtons();
                return;
            }

            if (IgnoreInputForChat())
            {
                ReleaseAllButtons();
                return;
            }

            CodeDmgConfig cfg = CodeDmgPlugin.ConfigSettings;
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");

            bool swap = cfg.SwapButtons != null && cfg.SwapButtons.Value;
            _emulator.SetButton(GameBoyButton.A,
                swap
                    ? (Input.GetKey(cfg.A.Value) || Input.GetKey(KeyCode.JoystickButton1))
                    : (Input.GetKey(cfg.A.Value) || Input.GetKey(KeyCode.JoystickButton0)));
            _emulator.SetButton(GameBoyButton.B,
                swap
                    ? (Input.GetKey(cfg.B.Value) || Input.GetKey(KeyCode.JoystickButton0))
                    : (Input.GetKey(cfg.B.Value) || Input.GetKey(KeyCode.JoystickButton1)));
            
            // Suppress Start+Select
            _emulator.SetButton(GameBoyButton.Start,
                !suppressStartSelect && (Input.GetKey(cfg.Start.Value) || Input.GetKey(KeyCode.JoystickButton3)));
            _emulator.SetButton(GameBoyButton.Select,
                !suppressStartSelect && (Input.GetKey(cfg.Select.Value) || Input.GetKey(KeyCode.JoystickButton2)));
            _emulator.SetButton(GameBoyButton.Right,
                Input.GetKey(cfg.Right.Value) || h > 0.5f);
            _emulator.SetButton(GameBoyButton.Left,
                Input.GetKey(cfg.Left.Value)  || h < -0.5f);
            _emulator.SetButton(GameBoyButton.Up,
                Input.GetKey(cfg.Up.Value)    || v > 0.5f);
            _emulator.SetButton(GameBoyButton.Down,
                Input.GetKey(cfg.Down.Value)  || v < -0.5f);
        }

        internal static void BeginRightMenuOpenReleaseGuard()
        {
            float now = Time.unscaledTime;
            _rightMenuOpenProbeUntil = now + 0.25f;
            _rightMenuOpenBlockedUntilRelease = Input.GetKey(KeyCode.RightArrow);
        }

        private static void UpdateRightMenuOpenReleaseGuard()
        {
            if (_rightMenuOpenBlockedUntilRelease && !Input.GetKey(KeyCode.RightArrow))
                _rightMenuOpenBlockedUntilRelease = false;
        }

        internal static bool ShouldBlockRightMenuOpenSignal()
        {
            if (_rightMenuOpenBlockedUntilRelease)
                return true;

            if (Time.unscaledTime <= _rightMenuOpenProbeUntil)
            {
                _rightMenuOpenBlockedUntilRelease = true;
                return true;
            }

            return false;
        }

        internal static void MarkRightMenuOpenReleased()
        {
            _rightMenuOpenBlockedUntilRelease = false;
            _rightMenuOpenProbeUntil = -1f;
        }

        internal static bool IsChatInputActive()
        {
            if (TryGetSlopChatInputBlocked(out bool inputBlocked) && inputBlocked)
                return true;

            if (EventSystem.current != null)
            {
                var selected = EventSystem.current.currentSelectedGameObject;
                if (selected != null)
                {
                    if (selected.GetComponent<TMP_InputField>() != null)
                        return true;

                    if (selected.GetComponent<UnityEngine.UI.InputField>() != null)
                        return true;
                }
            }

            return false;
        }

        private bool IgnoreInputForChat()
        {
            bool chatActive = IsChatInputActive();
            if (_wasChatActive && !chatActive)
            {
                SuppressEmulatorInputFor(0.3f);
                _wasChatActive = false;
                return true;  // block input this frame too — Enter still physically held
            }
            _wasChatActive = chatActive;
            return chatActive;
        }

        private static bool TryGetSlopChatInputBlocked(out bool blocked)
        {
            blocked = false;

            try
            {
                if (!_slopChatReflectionInitialized)
                {
                    _slopChatReflectionInitialized = true;

                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var type = assembly.GetType("SlopChat.InputUtils", false);
                        if (type == null)
                            continue;

                        _slopChatInputBlockedProperty = type.GetProperty(
                            "InputBlocked",
                            BindingFlags.Public | BindingFlags.Static);
                        break;
                    }
                }

                if (_slopChatInputBlockedProperty == null)
                    return false;

                object value = _slopChatInputBlockedProperty.GetValue(null, null);
                if (value is bool b)
                {
                    blocked = b;
                    return true;
                }
            }
            catch
            {
                // Ignore and fall back to normal input
            }

            return false;
        }

        private void SuppressEmulatorInputFor(float seconds)
        {
            _suppressEmulatorInputUntil = Mathf.Max(_suppressEmulatorInputUntil, Time.unscaledTime + seconds);
            ReleaseAllButtons();
        }

        private void ReleaseAllButtons()
        {
            _emulator.SetButton(GameBoyButton.A, false);
            _emulator.SetButton(GameBoyButton.B, false);
            _emulator.SetButton(GameBoyButton.Start, false);
            _emulator.SetButton(GameBoyButton.Select, false);
            _emulator.SetButton(GameBoyButton.Right, false);
            _emulator.SetButton(GameBoyButton.Left, false);
            _emulator.SetButton(GameBoyButton.Up, false);
            _emulator.SetButton(GameBoyButton.Down, false);
        }

        private void RenderNow()  => _renderer?.Render(_emulator);

        private void FlushCurrentPlayerInput()
        {
            Player player = WorldHandler.instance?.GetCurrentPlayer();
            if (player != null) player.FlushInput();
        }
    }
}
