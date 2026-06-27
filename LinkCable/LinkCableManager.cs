using BombRushMP.Plugin;
using BRCCodeDmg.Transport;
using System;
using System.Text.RegularExpressions;
using UnityEngine;

namespace BRCCodeDmg
{
    public enum LinkCableState { Disconnected, Linking, WaitingAccept, Connected }

    public sealed class LinkCableManager : IDisposable
    {
        private static readonly Regex _emojiRx = new Regex(
            @"[\uD800-\uDBFF][\uDC00-\uDFFF]|[\u2600-\u27BF]|<sprite[^>]*>|<[^>]+>",
            RegexOptions.Compiled);

        private const float InviteTimeoutSec     = 30f;
        private const float FailureStatusSeconds  = 5f;
        private const float RomErrorStatusSeconds  = 5f;
        private const byte  DisconnectReasonMissingRom = 1;

        public LinkCableState State { get; private set; } = LinkCableState.Disconnected;
        public ushort LinkedPlayerId { get; private set; }
        public string LinkedPlayerName { get; private set; }
        public bool IsHost { get; private set; }
        public string FailureStatusText { get; private set; }
        public bool HasFailureStatus => _failureStatusTimer > 0f && !string.IsNullOrEmpty(FailureStatusText);
        public int EffectiveLinkInputDelayFrames => GetEffectiveLinkInputDelayFrames();

        public bool ShowRomMismatch { get; private set; }
        public string RomMismatchMessage { get; private set; }
        public bool ShowDesyncWarning { get; private set; }
        private float _romMismatchTimer;
        private float _desyncWarningTimer;
        private string _localRomHash;
        private string _remoteRomHash;
        private string _peerRomPath; // Path to 2P's ROM

        private float _inviteTimeoutTimer;
        private float _pendingInviteTimer;
        private float _failureStatusTimer;

        private readonly LinkCableTransport _transport;
        private AppCodeDmg _app;

        public bool HasPendingInvite { get; private set; }
        public string PendingInviterName { get; private set; }
        private ushort _pendingInviterPlayerId;

        public event Action StateChanged;

        public LinkCableManager(LinkCableTransport transport)
        {
            _transport = transport;
            _transport.InviteReceived     += OnInviteReceived;
            _transport.AcceptReceived     += OnAcceptReceived;
            _transport.DeclineReceived    += OnDeclineReceived;
            _transport.DisconnectReceived += OnDisconnectReceived;
            _transport.RomHashReceived    += OnRomHashReceived;
            _transport.SaveStateReceived  += OnSaveStateReceived;
            _transport.LinkSettingsReceived += OnLinkSettingsReceived;
            _transport.JoypadReceived     += OnJoypadReceived;
            BombRushMP.Plugin.ClientController.ServerDisconnect   += OnServerDisconnected;
            BombRushMP.Plugin.ClientController.PlayerDisconnected += OnPlayerDisconnected;
        }

        public void Dispose()
        {
            _transport.InviteReceived     -= OnInviteReceived;
            _transport.AcceptReceived     -= OnAcceptReceived;
            _transport.DeclineReceived    -= OnDeclineReceived;
            _transport.DisconnectReceived -= OnDisconnectReceived;
            _transport.RomHashReceived    -= OnRomHashReceived;
            _transport.SaveStateReceived  -= OnSaveStateReceived;
            _transport.LinkSettingsReceived -= OnLinkSettingsReceived;
            _transport.JoypadReceived     -= OnJoypadReceived;
            BombRushMP.Plugin.ClientController.ServerDisconnect   -= OnServerDisconnected;
            BombRushMP.Plugin.ClientController.PlayerDisconnected -= OnPlayerDisconnected;
        }

        public void SetApp(AppCodeDmg app)    { _app = app; }
        public void SetLocalRomHash(string h) { _localRomHash = h; }

        // ── Tick ──────────────────────────────────────────────────────────────
        public void Tick(float dt) // Called once per Unity frame from OnAppUpdate
        {
            if (_failureStatusTimer > 0f)
            {
                _failureStatusTimer -= dt;
                if (_failureStatusTimer <= 0f) { _failureStatusTimer = 0f; FailureStatusText = null; StateChanged?.Invoke(); }
            }

            if (State == LinkCableState.WaitingAccept)
            {
                if (LinkedPlayerId != 0 && !PlayerExists(LinkedPlayerId)) { FailPendingLink("LINK FAILED: PLAYER GONE"); return; }
                _inviteTimeoutTimer += dt;
                if (_inviteTimeoutTimer >= InviteTimeoutSec) { FailPendingLink("LINK FAILED: TIMED OUT"); return; }
            }

            if (HasPendingInvite)
            {
                _pendingInviteTimer += dt;
                if (_pendingInviteTimer >= InviteTimeoutSec) { ClearPendingInvite(); ShowFailureStatus("LINK FAILED: YOU'RE TOO SLOW"); return; }
            }

            if (ShowRomMismatch) { _romMismatchTimer -= dt; if (_romMismatchTimer <= 0f) { ShowRomMismatch = false; StateChanged?.Invoke(); } }
            if (ShowDesyncWarning) { _desyncWarningTimer -= dt; if (_desyncWarningTimer <= 0f) { ShowDesyncWarning = false; StateChanged?.Invoke(); } }

            if (State == LinkCableState.Connected)
            {
                var emu = _app?.GetEmulator();
                if (emu != null && emu.DesyncDetected)
                {
                    emu.DesyncDetected = false;
                    ShowDesyncWarning = true;
                    _desyncWarningTimer = 5f;
                    StateChanged?.Invoke();
                }
            }

            if (State == LinkCableState.Connected && LinkedPlayerId != 0 && !_waitingForSync)
            {
                var emu = _app?.GetEmulator();
                if (emu != null)
                {
                    uint frame;
                    byte state;
                    while (emu.TryDequeueJoypadPacket(out frame, out state))
                        _transport.SendJoypad(LinkedPlayerId, frame, state);  // catch-all for any not flushed
                }
            }
        }

        // ── Invite ────────────────────────────────────────────────────────────
        public void HostSendInvite(ushort targetPlayerId, string hostDisplayName, string targetDisplayName = null)
        {
            if (State != LinkCableState.Disconnected || !_transport.Connected) return;
            ClearFailureStatus();
            if (targetPlayerId == 0 || !PlayerExists(targetPlayerId)) { ShowFailureStatus("LINK FAILED: PLAYER GONE"); return; }
            LinkedPlayerId   = targetPlayerId;
            LinkedPlayerName = string.IsNullOrEmpty(targetDisplayName) ? string.Empty : SanitizeName(targetDisplayName);
            IsHost           = true;
            State            = LinkCableState.WaitingAccept;
            _inviteTimeoutTimer = 0f;
            _transport.SendInvite(targetPlayerId, SanitizeName(hostDisplayName));
            StateChanged?.Invoke();
        }

        public void ClientAcceptInvite()
        {
            if (!HasPendingInvite) return;
            if (_pendingInviterPlayerId != 0 && !PlayerExists(_pendingInviterPlayerId))
            { ClearPendingInvite(); ShowFailureStatus("LINK FAILED: PLAYER GONE"); return; }

            HasPendingInvite    = false;
            _pendingInviteTimer = 0f;
            LinkedPlayerId      = _pendingInviterPlayerId;
            LinkedPlayerName    = SanitizeName(PendingInviterName);
            IsHost              = false;
            State               = LinkCableState.Connected;

            _transport.SendAccept(_pendingInviterPlayerId, SanitizeName(GetLocalPlayerName()));
            if (!string.IsNullOrEmpty(_localRomHash))
                _transport.SendRomHash(LinkedPlayerId, _localRomHash);

            SendOurSaveState();
            PostConnectionNotifications(LinkedPlayerName);
            StateChanged?.Invoke();
        }

        public void ClientDeclineInvite()
        {
            if (!HasPendingInvite) return;
            ushort id = _pendingInviterPlayerId;
            HasPendingInvite = false; _pendingInviterPlayerId = 0;
            PendingInviterName = null; _pendingInviteTimer = 0f;
            _transport.SendDecline(id);
            StateChanged?.Invoke();
        }

        // ── Disconnect ────────────────────────────────────────────────────────
        public void Drop()
        {
            if (State == LinkCableState.Disconnected && !HasPendingInvite) return;
            bool hadLink = State != LinkCableState.Disconnected || HasPendingInvite;
            if (LinkedPlayerId != 0 && State != LinkCableState.Disconnected)
                _transport.SendDisconnect(LinkedPlayerId);
            TearDownLinkedEmulator();
            ClearLinkState();
            if (hadLink) ShowFailureStatus("LINK DISCONNECTED");
            else StateChanged?.Invoke();
        }

        private void DropSilent(byte reason = 0)
        {
            if (LinkedPlayerId != 0 && State != LinkCableState.Disconnected)
                _transport.SendDisconnect(LinkedPlayerId, reason);
            TearDownLinkedEmulator();
            ClearLinkState();
            StateChanged?.Invoke();
        }

        // ── Linked emulator setup ─────────────────────────────────────────────
        private byte[] _checkpointState;
        private int? _remoteLinkInputDelayFrames;

        private void SendOurSaveState()
        {
            if (_saveStateSent) return;
            var emu = _app?.GetEmulator();
            if (emu == null || LinkedPlayerId == 0) return;
            _saveStateSent   = true;
            _waitingForSync  = true;  // freeze emu until peer state arrives
            try
            {
                _transport.SendLinkSettings(LinkedPlayerId, GetLocalConfiguredLinkInputDelayFrames()); // Capture state now and rewind emulator to it
                ApplyEffectiveLinkInputDelayToEmulator(); // If StepFrame already ran this Unity frame the rewind corrects it then both states are the same
                _checkpointState = emu.SerializeState();
                emu.DeserializeState(_checkpointState);   // rewind to checkpoint
                Debug.Log($"[CodeDMG] SaveState SEND to {LinkedPlayerId}: {_checkpointState.Length} bytes");
                _transport.SendSaveState(LinkedPlayerId, _checkpointState);
            }
            catch (Exception e) { Debug.LogError("[CodeDMG] SaveState serialize/rewind error: " + e.Message); }
        }

        private void TearDownLinkedEmulator()
        {
            var emu = _app?.GetEmulator();
            emu?.DetachLinkedEmulator();
        }

        // ── Packet handlers ───────────────────────────────────────────────────
        private void OnInviteReceived(ushort fromId, string senderName)
        {
            if (State != LinkCableState.Disconnected) return;
            ClearFailureStatus();
            HasPendingInvite = true; _pendingInviterPlayerId = fromId;
            _pendingInviteTimer = 0f; PendingInviterName = SanitizeName(senderName);
            StateChanged?.Invoke();
        }

        private bool _saveStateSent;
        private bool _waitingForSync; // Block emulator StepFrame via ShouldYieldForSerialLink so both sides freeze at a checkpoint before resuming in lockstep
        public  bool WaitingForSync => _waitingForSync;
        private void OnAcceptReceived(ushort fromId, string clientName)
        {
            if (State != LinkCableState.WaitingAccept || fromId != LinkedPlayerId) return;
            LinkedPlayerName    = SanitizeName(clientName);
            State               = LinkCableState.Connected;
            _inviteTimeoutTimer = 0f;
            ClearFailureStatus();
            if (!string.IsNullOrEmpty(_localRomHash))
                _transport.SendRomHash(LinkedPlayerId, _localRomHash);
            SendOurSaveState();
            PostConnectionNotifications(LinkedPlayerName);
            StateChanged?.Invoke();
        }

        private void OnDeclineReceived(ushort fromId)
        {
            if (fromId != LinkedPlayerId) return;
            if (State == LinkCableState.WaitingAccept) FailPendingLink("LINK FAILED: DECLINED", false);
        }

        private void OnServerDisconnected()
        {
            if (State == LinkCableState.Disconnected && !HasPendingInvite) return;
            TearDownLinkedEmulator();
            ClearLinkState();
            ShowFailureStatus("LINK DISCONNECTED");
        }

        private void OnPlayerDisconnected(ushort playerId)
        {
            if (playerId != LinkedPlayerId && playerId != _pendingInviterPlayerId) return;
            if (State == LinkCableState.Disconnected && !HasPendingInvite) return;
            TearDownLinkedEmulator();
            ClearLinkState();
            ShowFailureStatus("LINK DISCONNECTED");
        }

        private void OnDisconnectReceived(ushort fromId, byte reason)
        {
            if (fromId != LinkedPlayerId && fromId != _pendingInviterPlayerId) return;
            bool wasPending = HasPendingInvite && fromId == _pendingInviterPlayerId;
            TearDownLinkedEmulator();
            ClearLinkState();
            if (reason == DisconnectReasonMissingRom)
                ShowRomError("LINK DISCONNECTED!", "Peer is missing your ROM\nin their RomDirectory!");
            else
                ShowFailureStatus(wasPending ? "LINK FAILED: YOU'RE TOO SLOW" : "LINK DISCONNECTED");
        }

        private void OnRomHashReceived(ushort fromId, string hash)
        {
            if (fromId != LinkedPlayerId) return;
            _remoteRomHash = hash;
            CheckRomCompatibility();
        }

        private void OnSaveStateReceived(ushort fromId, byte[] data)
        {
            if (fromId != LinkedPlayerId || State != LinkCableState.Connected) return;
            var emu = _app?.GetEmulator();
            if (emu == null) return;

            Debug.Log($"[CodeDMG] SaveState RECEIVE from {fromId}: {data.Length} bytes");

            emu.SetLinkInputDelayFrames(GetEffectiveLinkInputDelayFrames());
            string linkedRomPath = !string.IsNullOrEmpty(_peerRomPath) ? _peerRomPath : emu.RomPath;
            emu.AttachLinkedEmulator(linkedRomPath, emu.BootRomPath, string.Empty);
            emu.SetLinkPlayerIds(_transport.LocalPlayerId, LinkedPlayerId);
            emu.DeserializeLinkedState(data);

            _waitingForSync = false;
            Debug.Log("[CodeDMG] Sync complete — linked emulator loaded from peer checkpoint");
        }

        private void OnLinkSettingsReceived(ushort fromId, int inputDelayFrames)
        {
            if (fromId != LinkedPlayerId || State != LinkCableState.Connected) return;
            _remoteLinkInputDelayFrames = CodeDmgEmulator.NormalizeLinkInputDelayFrames(inputDelayFrames);
            ApplyEffectiveLinkInputDelayToEmulator();
        }

        private void OnJoypadReceived(ushort fromId, uint frame, byte state)
        {
            if (fromId != LinkedPlayerId || State != LinkCableState.Connected) return;
            _app?.GetEmulator()?.QueuePeerJoypad(frame, state);
        }

        // ── ROM compatibility ─────────────────────────────────────────────────
        private void CheckRomCompatibility()
        {
            if (string.IsNullOrEmpty(_localRomHash) || string.IsNullOrEmpty(_remoteRomHash)) return;
            if (string.Equals(_localRomHash, _remoteRomHash, StringComparison.OrdinalIgnoreCase)) return;
            string lt = GetRomTitle(_localRomHash), rt = GetRomTitle(_remoteRomHash);
            if (IsPokemonCompatible(lt, rt))
            {
                // Diff Pokemon Vers
                string peerCrc = GetRomCrc(_remoteRomHash);
                if (!string.IsNullOrEmpty(peerCrc))
                {
                    _peerRomPath = FindRomByCrc(peerCrc);
                    if (!string.IsNullOrEmpty(_peerRomPath))
                        return;
                }
                // Pokemon compatible but peer ROM not found
                string peerTitle = GetRomTitle(_remoteRomHash);
                DropSilent(DisconnectReasonMissingRom);
                ShowRomError("LINK DISCONNECTED!", "Missing peer ROM in RomDirectory:\n" + peerTitle + ".gb/.gbc");
                return;
            }

            DropSilent(); // Incompatible ROMs display error and drop loading the peer's save state
            ShowRomError("LINK DISCONNECTED!", "Different ROMs detected!");
        }

        private static string GetRomCrc(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return string.Empty;
            int sep = hash.IndexOf(':');
            return sep >= 0 ? hash.Substring(sep + 1) : string.Empty;
        }

        private string FindRomByCrc(string crc)
        {
            // Search plugin directory and the directory containing the player's own ROM
            var dirs = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { dirs.Add(CodeDmgPlugin.Instance.PluginDirectory); } catch { }
            try
            {
                string ownRom = _app?.GetEmulator()?.RomPath;
                if (!string.IsNullOrEmpty(ownRom))
                    dirs.Add(System.IO.Path.GetDirectoryName(ownRom));
            }
            catch { }

            foreach (string dir in dirs)
            {
                if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) continue;
                try
                {
                    foreach (string file in System.IO.Directory.GetFiles(dir, "*.*"))
                    {
                        string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                        if (ext != ".gb" && ext != ".gbc") continue;
                        byte[] rom = System.IO.File.ReadAllBytes(file);
                        uint fileCrc = CodeDmgEmulator.ComputeCrc32Public(rom);
                        if (fileCrc.ToString("X8").Equals(crc, StringComparison.OrdinalIgnoreCase))
                            return file;
                    }
                }
                catch { }
            }
            return string.Empty;
        }

        private static bool IsPokemonCompatible(string a, string b)
        {
            return IsPokemonTitle(a) && IsPokemonTitle(b);
        }

        private static bool IsPokemonTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            // Gen 1/2/TCG headers all start with POKEMON (space, _, -, or letter) or PM_ (Crystal)
            string t = title.ToUpperInvariant();
            return t.StartsWith("POKEMON") || t.StartsWith("PM_CRYSTAL") || t.StartsWith("POKECARD");
        }

        private static string GetRomTitle(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return string.Empty;
            int sep = hash.IndexOf(':');
            return sep > 0 ? hash.Substring(0, sep) : string.Empty;
        }

        // Called directly from StepLinkedFrame to send joypad without waiting for next Tick()
        internal void FlushJoypadPacket(uint frame, byte state)
        {
            if (State == LinkCableState.Connected && LinkedPlayerId != 0 && !_waitingForSync)
                _transport.SendJoypad(LinkedPlayerId, frame, state);
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private int GetLocalConfiguredLinkInputDelayFrames()
        {
            int value = CodeDmgPlugin.ConfigSettings?.LinkInputDelayFrames?.Value ?? 16;
            return CodeDmgEmulator.NormalizeLinkInputDelayFrames(value);
        }

        private int GetEffectiveLinkInputDelayFrames()
        {
            int local = GetLocalConfiguredLinkInputDelayFrames();
            ushort lobbyHostId = GetLobbyHostId();

            if (lobbyHostId == _transport.LocalPlayerId) return local;
            if (lobbyHostId == LinkedPlayerId && _remoteLinkInputDelayFrames.HasValue) return _remoteLinkInputDelayFrames.Value;
            if (lobbyHostId == 0 && !IsHost && _remoteLinkInputDelayFrames.HasValue) return _remoteLinkInputDelayFrames.Value;
            return local;
        }

        private void ApplyEffectiveLinkInputDelayToEmulator()
        {
            _app?.GetEmulator()?.SetLinkInputDelayFrames(GetEffectiveLinkInputDelayFrames());
        }

        private static ushort GetLobbyHostId()
        {
            try
            {
                var cc = ClientController.Instance;
                var lobby = cc?.ClientLobbyManager?.CurrentLobby;
                if (lobby?.LobbyState == null) return 0;
                return lobby.LobbyState.HostId;
            }
            catch { return 0; }
        }

        private void FailPendingLink(string message, bool sendDisc = true)
        {
            if (sendDisc && LinkedPlayerId != 0 && State != LinkCableState.Disconnected && PlayerExists(LinkedPlayerId))
                _transport.SendDisconnect(LinkedPlayerId);
            TearDownLinkedEmulator();
            ClearLinkState();
            ShowFailureStatus(message);
        }

        private void ClearLinkState()
        {
            HasPendingInvite = false; _pendingInviterPlayerId = 0;
            PendingInviterName = null; _pendingInviteTimer = 0f;
            LinkedPlayerId = 0; LinkedPlayerName = null;
            IsHost = false; State = LinkCableState.Disconnected;
            _remoteRomHash = null; _peerRomPath = null; ShowRomMismatch = false; RomMismatchMessage = null; ShowDesyncWarning = false;
            _saveStateSent   = false;
            _waitingForSync  = false;
            _checkpointState = null;
            _remoteLinkInputDelayFrames = null;
            LinkCableConnectDialog.Hide();
        }

        private void ClearPendingInvite()
        {
            HasPendingInvite = false; _pendingInviterPlayerId = 0;
            PendingInviterName = null; _pendingInviteTimer = 0f;
            LinkCableConnectDialog.Hide();
            StateChanged?.Invoke();
        }

        private void ShowFailureStatus(string message, float duration = FailureStatusSeconds)
        { FailureStatusText = message; _failureStatusTimer = duration; StateChanged?.Invoke(); }

        private void ShowRomError(string statusMsg, string detailMsg, float duration = RomErrorStatusSeconds)
        {
            ShowFailureStatus(statusMsg);
            RomMismatchMessage = detailMsg;
            ShowRomMismatch = true; _romMismatchTimer = duration;
            StateChanged?.Invoke();
        }

        private void ClearFailureStatus()
        { FailureStatusText = null; _failureStatusTimer = 0f; }

        private void PostConnectionNotifications(string otherName)
        { ShowChatNotification(IsHost ? $"{otherName} connected!" : $"Connected to {otherName}!"); }

        private static void ShowChatNotification(string message)
        {
            try
            {
                var cc = ClientController.Instance;
                if (cc != null)
                {
                    var m = cc.GetType().GetMethod("ShowLocalNotification",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (m != null) { m.Invoke(cc, new object[] { message }); return; }
                }
            }
            catch { }
            Debug.Log("[CodeDMG] " + message);
        }

        public static string SanitizeName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Player";
            string s = _emojiRx.Replace(raw, string.Empty).Trim();
            return string.IsNullOrWhiteSpace(s) ? "Player" : s;
        }

        private static bool PlayerExists(ushort id)
        {
            try { var cc = ClientController.Instance; return cc?.Players?.ContainsKey(id) == true; }
            catch { return false; }
        }

        private static string GetLocalPlayerName()
        {
            try
            {
                var cc = ClientController.Instance;
                if (cc == null) return string.Empty;
                if (!cc.Players.TryGetValue(cc.LocalID, out var p) || p == null) return string.Empty;
                return MPUtility.GetPlayerDisplayName(p.ClientState) ?? string.Empty;
            }
            catch { return string.Empty; }
        }
    }
}