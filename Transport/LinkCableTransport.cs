using BombRushMP.Common.Networking;
using BombRushMP.Plugin;
using BRCCodeDmg.Transport.Packets;
using System;
using System.Text;

namespace BRCCodeDmg.Transport
{
    public sealed class LinkCableTransport : IDisposable
    {
        private bool _disposed;

        public event Action<ushort, string>  InviteReceived;
        public event Action<ushort, string>  AcceptReceived;
        public event Action<ushort>          DeclineReceived;
        public event Action<ushort, byte>    DisconnectReceived;
        public event Action<ushort, string>  RomHashReceived;
        public event Action<ushort, byte[]>  SaveStateReceived;
        public event Action<ushort, int>     LinkSettingsReceived;
        public event Action<ushort, uint, byte> JoypadReceived;

        public ushort LocalPlayerId =>
            ClientController.Instance != null ? ClientController.Instance.LocalID : (ushort)0;

        public bool Connected =>
            ClientController.Instance != null && ClientController.Instance.Connected;

        public LinkCableTransport()
        {
            ClientController.RegisterCustomPacketHandler(LinkCablePacketIds.Invite,     OnRawInvite);
            ClientController.RegisterCustomPacketHandler(LinkCablePacketIds.Accept,     OnRawAccept);
            ClientController.RegisterCustomPacketHandler(LinkCablePacketIds.Decline,    OnRawDecline);
            ClientController.RegisterCustomPacketHandler(LinkCablePacketIds.Disconnect, OnRawDisconnect);
            ClientController.RegisterCustomPacketHandler(LinkCablePacketIds.RomHash,    OnRawRomHash);
            ClientController.RegisterCustomPacketHandler(LinkCablePacketIds.SaveState,  OnRawSaveState);
            ClientController.RegisterCustomPacketHandler(LinkCablePacketIds.LinkSettings, OnRawLinkSettings);
            ClientController.RegisterCustomPacketHandler(LinkCablePacketIds.Joypad,     OnRawJoypad);
        }

        public void Dispose()
        {
            _disposed = true;
            ClientController.UnregisterCustomPacketHandler(LinkCablePacketIds.Invite);
            ClientController.UnregisterCustomPacketHandler(LinkCablePacketIds.Accept);
            ClientController.UnregisterCustomPacketHandler(LinkCablePacketIds.Decline);
            ClientController.UnregisterCustomPacketHandler(LinkCablePacketIds.Disconnect);
            ClientController.UnregisterCustomPacketHandler(LinkCablePacketIds.RomHash);
            ClientController.UnregisterCustomPacketHandler(LinkCablePacketIds.SaveState);
            ClientController.UnregisterCustomPacketHandler(LinkCablePacketIds.LinkSettings);
            ClientController.UnregisterCustomPacketHandler(LinkCablePacketIds.Joypad);
        }

        // ── Send ──────────────────────────────────────────────────────────────
        public void SendInvite(ushort t, string name)     => Send(Str(name), LinkCablePacketIds.Invite, t);
        public void SendAccept(ushort t, string name)     => Send(Str(name), LinkCablePacketIds.Accept, t);
        public void SendDecline(ushort t)                 => Send(new byte[0], LinkCablePacketIds.Decline, t);
        public void SendDisconnect(ushort t, byte reason = 0) => Send(new[] { reason }, LinkCablePacketIds.Disconnect, t);
        public void SendRomHash(ushort t, string hash)    => Send(Str(hash), LinkCablePacketIds.RomHash, t);

        public void SendSaveState(ushort t, byte[] data)
        {
            if (_disposed || !Connected || data == null) return;
            UnityEngine.Debug.Log($"[CodeDMG] SaveState SEND to {t}: {data.Length} bytes");
            try { ClientController.Instance.SendCustomPacketToPlayer(data, LinkCablePacketIds.SaveState, t, IMessage.SendModes.Reliable); }
            catch (Exception e) { UnityEngine.Debug.LogError("[CodeDMG] SaveState send error: " + e.Message); }
        }

        public void SendLinkSettings(ushort t, int inputDelayFrames)
        {
            if (_disposed || !Connected) return;
            byte[] payload = new byte[4];
            payload[0] = (byte)(inputDelayFrames & 0xFF);
            payload[1] = (byte)((inputDelayFrames >> 8) & 0xFF);
            payload[2] = (byte)((inputDelayFrames >> 16) & 0xFF);
            payload[3] = (byte)((inputDelayFrames >> 24) & 0xFF);
            try { ClientController.Instance.SendCustomPacketToPlayer(payload, LinkCablePacketIds.LinkSettings, t, IMessage.SendModes.Reliable); }
            catch { }
        }

        // Ring buffer of last 3 sent frames for redundant delivery.
        private readonly uint[] _joypadSentFrames  = new uint[3];
        private readonly byte[] _joypadSentStates  = new byte[3] { 0xFF, 0xFF, 0xFF };
        private int _joypadSentIndex;

        // Each packet carries the current frame plus up to 2 older frames.
        // Switching to Unreliable eliminates retransmit latency; redundancy covers loss.
        // Layout: frame(4) state(1) [prev1_delta(1) prev1_state(1)] [prev2_delta(1) prev2_state(1)]
        public void SendJoypad(ushort t, uint frame, byte state)
        {
            if (_disposed || !Connected) return;

            int idx = _joypadSentIndex % 3;
            _joypadSentFrames[idx] = frame;
            _joypadSentStates[idx] = state;
            _joypadSentIndex++;

            byte[] payload = new byte[9];
            payload[0] = (byte)(frame & 0xFF);
            payload[1] = (byte)((frame >> 8) & 0xFF);
            payload[2] = (byte)((frame >> 16) & 0xFF);
            payload[3] = (byte)((frame >> 24) & 0xFF);
            payload[4] = state;

            for (int i = 1; i <= 2; i++)
            {
                int prevIdx = ((_joypadSentIndex - 1 - i) % 3 + 3) % 3;
                uint prevFrame = _joypadSentFrames[prevIdx];
                byte prevState = _joypadSentStates[prevIdx];
                long delta = (long)frame - (long)prevFrame;
                payload[5 + (i - 1) * 2] = delta > 0 && delta <= 255 ? (byte)delta : (byte)0;
                payload[5 + (i - 1) * 2 + 1] = prevState;
            }

            try { ClientController.Instance.SendCustomPacketToPlayer(payload, LinkCablePacketIds.Joypad, t, IMessage.SendModes.Unreliable); }
            catch { }
        }

        // ── Receive ───────────────────────────────────────────────────────────
        private void OnRawInvite(ushort from, byte[] data)
        { if (!_disposed) InviteReceived?.Invoke(from, ReadStr(data)); }

        private void OnRawAccept(ushort from, byte[] data)
        { if (!_disposed) AcceptReceived?.Invoke(from, ReadStr(data)); }

        private void OnRawDecline(ushort from, byte[] data)
        { if (!_disposed) DeclineReceived?.Invoke(from); }

        private void OnRawDisconnect(ushort from, byte[] data)
        { if (!_disposed) DisconnectReceived?.Invoke(from, data != null && data.Length > 0 ? data[0] : (byte)0); }

        private void OnRawRomHash(ushort from, byte[] data)
        { if (!_disposed) RomHashReceived?.Invoke(from, ReadStr(data)); }

        private void OnRawSaveState(ushort from, byte[] data)
        {
            if (_disposed || data == null || data.Length == 0) return;
            UnityEngine.Debug.Log($"[CodeDMG] SaveState RECEIVE from {from}: {data.Length} bytes");
            SaveStateReceived?.Invoke(from, data);
        }

        private void OnRawLinkSettings(ushort from, byte[] data)
        {
            if (_disposed || data == null || data.Length < 4) return;
            int inputDelayFrames = data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
            LinkSettingsReceived?.Invoke(from, inputDelayFrames);
        }

        private void OnRawJoypad(ushort from, byte[] data)
        {
            if (_disposed || data == null || data.Length < 5) return;
            uint frame = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
            JoypadReceived?.Invoke(from, frame, data[4]);

            // Decode redundant older frames packed into the same packet.
            for (int i = 0; i < 2 && 5 + i * 2 + 1 < data.Length; i++)
            {
                byte delta = data[5 + i * 2];
                if (delta == 0) continue;
                uint prevFrame = frame - delta;
                JoypadReceived?.Invoke(from, prevFrame, data[5 + i * 2 + 1]);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private void Send(byte[] payload, string id, ushort t,
                          IMessage.SendModes mode = IMessage.SendModes.ReliableUnordered)
        {
            if (_disposed || !Connected) return;
            try { ClientController.Instance.SendCustomPacketToPlayer(payload, id, t, mode); }
            catch { }
        }

        private static byte[] Str(string s) => Encoding.UTF8.GetBytes(s ?? string.Empty);
        private static string ReadStr(byte[] d)
        {
            if (d == null || d.Length == 0) return string.Empty;
            try { return Encoding.UTF8.GetString(d); } catch { return string.Empty; }
        }
    }
}
