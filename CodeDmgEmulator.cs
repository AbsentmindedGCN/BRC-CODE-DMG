using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace BRCCodeDmg
{
    public sealed class CodeDmgEmulator
    {
        // private const int CyclesPerFrame = 70224;
        private const int CyclesPerFrameNormal = 70224;

        // Bumped from 1 → 2 because MMU layout expanded for GBC support
        // (dual VRAM banks, 8×WRAM banks, CGB palette data, HDMA state).
        // Bumped to 3 for RTC support
        // Bumped to 4 for updated Audio
        private const int SaveStateVersion = 4;

        private MMU Mmu { get; }
        private CPU Cpu { get; }
        public PPU Ppu { get; }
        public APU Apu { get; }
        private Joypad Joypad { get; }
        private Timer Timer { get; }

        public bool AudioEnabled { get; private set; } = false;

        // Peer Emu Instance
        internal CodeDmgEmulator LinkedEmulator { get; private set; }

        private const int DefaultLinkInputDelayFrames = 4;
        private const int RollbackDepth = 16;
        private const int MaxBufferedLinkInputs = 240;

        private int _linkInputDelayFrames = DefaultLinkInputDelayFrames;
        private byte _localJoypad = 0xFF;
        private uint _linkFrame;
        public  bool DesyncDetected { get; internal set; }
        private uint _nextQueuedLocalInputFrame;
        private readonly Dictionary<uint, byte> _localInputFrames = new Dictionary<uint, byte>();
        private readonly Dictionary<uint, byte> _peerInputFrames = new Dictionary<uint, byte>();
        private readonly Queue<LinkJoypadPacket> _pendingJoypadPackets = new Queue<LinkJoypadPacket>();

        private ushort _mainLinkPlayerId;
        private ushort _linkedLinkPlayerId;
        private int _activeSerialMaster;
        private byte _lastKnownPeerJoypad = 0xFF;
        private readonly Dictionary<uint, byte> _predictedPeerInputs = new Dictionary<uint, byte>();
        private int _linkCycleAccum;

        // Pre-allocated snapshot slots, one per rollback frame, reused every cycle
        // 64KB each covers both emulators' MMU+CPU+PPU+Timer state with plenty of headroom
        private const int SnapshotSlotSize = 512 * 1024;
        private readonly byte[][] _snapshotSlots  = new byte[RollbackDepth][];
        private readonly uint[]   _snapshotFrames = new uint[RollbackDepth];
        private readonly Dictionary<uint, byte> _unverifiedPredictions = new Dictionary<uint, byte>();
        private readonly List<uint> _confirmedFramesScratch = new List<uint>(32);
        private readonly List<uint> _pruneScratch = new List<uint>(64);
        private readonly System.IO.MemoryStream _snapshotStream = new System.IO.MemoryStream(512 * 1024);
        private readonly int[] _snapshotLengths = new int[RollbackDepth];

        private readonly string _savePath;
        private readonly string _romPath;
        private readonly string _bootRomPath;
        private readonly string _romTitle;

        public string RomPath => _romPath;
        public string BootRomPath => _bootRomPath;
        public string SavePath => _savePath;

        public bool FrameDirty => Ppu.FrameDirty;
        public bool IsTetrisRom => _romTitle.StartsWith("TETRIS", StringComparison.OrdinalIgnoreCase);

        public CodeDmgEmulator(string romPath, string bootRomPath, string savePath)
        {
            _romPath = romPath;
            _bootRomPath = bootRomPath;
            _savePath = savePath;

            byte[] gameRom = File.ReadAllBytes(romPath);
            _romTitle = ReadRomTitle(gameRom);
            byte[] bootRom = File.Exists(bootRomPath) ? File.ReadAllBytes(bootRomPath) : new byte[256];

            Helper.scale = 1;
            //Helper.paletteName = "dmg";
            Helper.paletteName = CodeDmgPlugin.ConfigSettings?.Palette?.Value;

            // MMU auto-detects GBC mode from cartridge header byte 0x143
            // (0x80 = GBC-compatible, 0xC0 = GBC-only)
            Mmu = new MMU(gameRom, bootRom, false);
            Cpu = new CPU(Mmu);
            Ppu = new PPU(Mmu);
            Joypad = new Joypad(Mmu);
            Apu = new APU(Mmu, UnityEngine.AudioSettings.outputSampleRate);
            Timer = new Timer(Mmu, Apu);

            Mmu.Apu = Apu;
            Mmu.Timer = Timer;

            if (!File.Exists(bootRomPath))
            {
                // Detect GBC from cartridge header byte 0x143 (0x80/0xC0 = GBC).
                // Pass isGbc so CPU sets A=0x11 (GBC hardware ID) instead of A=0x01 (DMG).
                bool isGbc = gameRom.Length > 0x143 &&
                             (gameRom[0x143] == 0x80 || gameRom[0x143] == 0xC0);

                Cpu.Reset(isGbc);

                if (isGbc)
                {
                    Mmu.InitializeCGBRegisters();

                    // Re-sync the APU
                    Mmu.NR52 = 0x00;
                    Apu.WriteRegister(0xFF26, 0x80);
                    Apu.WriteRegister(0xFF24, 0x77);
                    Apu.WriteRegister(0xFF25, 0xF3);
                }
            }

            //Mmu.Load(_savePath);
        }

        public void SetAudioEnabled(bool enabled)
        {
            AudioEnabled = enabled;
            Apu?.SetEnabled(enabled);
        }

        public byte ReadSerialData() => Mmu.ReadSerialData();
        public bool ShouldYieldForSerialLink()
        {
            var lc = BRCCodeDmg.CodeDmgPlugin.LinkCable;
            return lc != null && lc.WaitingForSync;
        }

        public void TickSerialLinkWait(float deltaSeconds) { }

        public void DeliverRemoteSerialByte(byte value)
        {
            Mmu.DeliverRemoteSerialByte(value); // Delivers a remote peer byte to the MMU that gets picked up by StepSerialLink on next tick
        }

        public void CancelSerialTransfer()
        {
            Mmu.CancelSerial();
            Mmu.IF = (byte)(Mmu.IF & ~0x08);
        }

        private static string ReadRomTitle(byte[] rom)
        {
            if (rom == null || rom.Length < 0x0143) return string.Empty;
            var sb = new StringBuilder(16);
            for (int i = 0x0134; i < 0x0143; i++)
            {
                byte b = rom[i];
                if (b == 0) break;
                if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
            }
            return sb.ToString().Trim();
        }

        // ── Rom Hash ──────────────────────────────────────────────────────────
        public string GetRomHash()
        {
            try
            {
                byte[] rom = File.ReadAllBytes(_romPath);
                if (rom.Length < 0x0143) return string.Empty;

                // Pull 15-char title from header
                var sb = new System.Text.StringBuilder(16);
                for (int i = 0x0134; i < 0x0143; i++)
                {
                    byte b = rom[i];
                    if (b == 0) break;
                    if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
                }

                uint crc = ComputeCrc32Public(rom);
                return sb.ToString().Trim() + ":" + crc.ToString("X8");
            }
            catch { return string.Empty; }
        }

        internal static uint ComputeCrc32Public(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return crc ^ 0xFFFFFFFF;
        }

        public void StepFrame()
        {
            if (LinkedEmulator != null)
            {
                StepLinkedFrame();
                return;
            }

            int cycles     = 0;
            int budget     = Mmu.CgbDoubleSpeed ? CyclesPerFrameNormal * 2 : CyclesPerFrameNormal;
            int lastCycles = 4;

            while (cycles < budget)
            {
                int systemCycles = Mmu.CgbDoubleSpeed ? lastCycles / 2 : lastCycles;
                Mmu.StepSerialLink(lastCycles);
                Timer.Step(lastCycles);
                Apu.Step(systemCycles);
                Ppu.Step(systemCycles);

                int executed = Cpu.ExecuteInstruction();
                cycles    += executed;
                lastCycles = executed;
            }
        }

        private void StepLinkedFrame()
        {
            QueueLocalInputForFutureFrame();
            FlushJoypadPackets();

            ProcessConfirmedInputs(); // Apply confirmed peer inputs that arrived for frames predicted

            if (!_localInputFrames.TryGetValue(_linkFrame, out byte localState))
                localState = _localJoypad;

            // Barebones rollback
            // Predict peer input as last known input.
            bool confirmed = _peerInputFrames.TryGetValue(_linkFrame, out byte peerState);
            if (!confirmed)
                peerState = _lastKnownPeerJoypad;
            else
                _lastKnownPeerJoypad = peerState;

            if (!_predictedPeerInputs.ContainsKey(_linkFrame))
            {
                _predictedPeerInputs[_linkFrame] = peerState;
                if (!confirmed) _unverifiedPredictions[_linkFrame] = peerState;
            }

            // Save state BEFORE stepping so we can roll back to this point if we need to
            SaveRollbackSnapshot(_linkFrame);

            Joypad.SetStateRaw(localState);
            LinkedEmulator.SetJoypadStateRaw(peerState);
            StepBothEmulators();
            PruneLinkInputs(_linkFrame);
            _linkFrame++;
        }

        private void ProcessConfirmedInputs()
        {
            uint oldestMismatch = uint.MaxValue;
            _confirmedFramesScratch.Clear();
            foreach (var kv in _unverifiedPredictions)
            {
                uint frame = kv.Key;
                if (!_peerInputFrames.TryGetValue(frame, out byte actual)) continue;
                _confirmedFramesScratch.Add(frame);
                if (actual == kv.Value) continue;
                if (frame < oldestMismatch) oldestMismatch = frame;
            }
            for (int i = 0; i < _confirmedFramesScratch.Count; i++) _unverifiedPredictions.Remove(_confirmedFramesScratch[i]);

            if (oldestMismatch == uint.MaxValue) return;

            // If the mismatch is beyond rollback range we're fucked but we can still update _lastKnownPeerJoypad so future predictions use reality
            if (_linkFrame - oldestMismatch > RollbackDepth)
            {
                if (_peerInputFrames.TryGetValue(oldestMismatch, out byte lateActual))
                    _lastKnownPeerJoypad = lateActual;
                DesyncDetected = true;
                return;
            }

            int slot = (int)(oldestMismatch % RollbackDepth);
            if (_snapshotFrames[slot] != oldestMismatch) return;

            // Restore state to the mismatched frame and re-simulate
            uint resimFrom = oldestMismatch;
            uint resimTo   = _linkFrame;

            RestoreRollbackSnapshot(resimFrom);
            _linkFrame = resimFrom;
            _unverifiedPredictions.Clear();

            while (_linkFrame < resimTo)
            {
                if (!_localInputFrames.TryGetValue(_linkFrame, out byte loc))
                    loc = 0xFF;
                bool hasPeer = _peerInputFrames.TryGetValue(_linkFrame, out byte peer);
                if (hasPeer) _lastKnownPeerJoypad = peer;
                else peer = _lastKnownPeerJoypad;

                _predictedPeerInputs[_linkFrame] = peer;
                _unverifiedPredictions[_linkFrame] = peer;
                SaveRollbackSnapshot(_linkFrame);
                Joypad.SetStateRaw(loc);
                LinkedEmulator.SetJoypadStateRaw(peer);
                StepBothEmulators();
                PruneLinkInputs(_linkFrame);
                _linkFrame++;
            }
        }

        public void SetButton(GameBoyButton button, bool pressed)
        {
            if (LinkedEmulator != null)
            {
                // Delay-based lockstep: accumulate input into the buffer this frame
                // Applies at start of next StepFrame alongside the player/peer's input
                byte mask = (byte)button;
                if (pressed) _localJoypad &= (byte)~mask;
                else         _localJoypad |= mask;
            }
            else
            {
                Joypad.SetButton(button, pressed);
            }
        }

        public byte GetJoypadState() => Joypad.GetState();

        internal void SetJoypadStateRaw(byte state) => Joypad.SetStateRaw(state);

        private void StepBothEmulators()
        {
            int mainCycles   = 0;
            int linkedCycles = 0;
            int mainBudget   = Mmu.CgbDoubleSpeed ? CyclesPerFrameNormal * 2 : CyclesPerFrameNormal;
            int linkedBudget = LinkedEmulator.Mmu.CgbDoubleSpeed ? CyclesPerFrameNormal * 2 : CyclesPerFrameNormal;
            while (mainCycles < mainBudget || linkedCycles < linkedBudget)
            {
                if ((mainCycles <= linkedCycles && mainCycles < mainBudget) || linkedCycles >= linkedBudget)
                {
                    int executed = StepSelfOneInstruction();
                    mainCycles += executed;
                    LocalLinkStep(true, executed);
                }
                else
                {
                    int executed = LinkedEmulator.StepSelfOneInstruction();
                    linkedCycles += executed;
                    LocalLinkStep(false, executed);
                }
            }
        }

        private void FlushJoypadPackets()
        {
            var lc = BRCCodeDmg.CodeDmgPlugin.LinkCable;
            if (lc == null || lc.State != LinkCableState.Connected) return;
            uint frame;
            byte state;
            while (TryDequeueJoypadPacket(out frame, out state)) // Send any queued joypad packets immediately rather than waiting for next Tick()
                lc.FlushJoypadPacket(frame, state);
        }

        internal bool TryDequeueJoypadPacket(out uint frame, out byte state)
        {
            if (_pendingJoypadPackets.Count == 0)
            {
                frame = 0;
                state = 0xFF;
                return false;
            }

            var packet = _pendingJoypadPackets.Dequeue();
            frame = packet.Frame;
            state = packet.State;
            return true;
        }

        internal void QueuePeerJoypad(uint frame, byte state)
        {
            if (frame + MaxBufferedLinkInputs < _linkFrame) return;
            _peerInputFrames[frame] = state;
            PruneLinkInputs(_linkFrame);
        }

        internal void SetLinkPlayerIds(ushort mainPlayerId, ushort linkedPlayerId)
        {
            _mainLinkPlayerId = mainPlayerId;
            _linkedLinkPlayerId = linkedPlayerId;
        }

        internal void SetLinkInputDelayFrames(int frames)
        {
            int normalized = NormalizeLinkInputDelayFrames(frames);
            if (_linkInputDelayFrames == normalized) return;
            _linkInputDelayFrames = normalized;
            if (LinkedEmulator != null) ResetLocalLinkState();
        }

        internal static int NormalizeLinkInputDelayFrames(int frames)
        {
            if (frames < 1) return 1;
            if (frames > 60) return 60;
            return frames;
        }

        // ── Hidden Peer Emu ───────────────────────────────────────────────────
        internal void AttachLinkedEmulator(string romPath, string bootRomPath, string savePath)
        {
            DetachLinkedEmulator();
            var linked = new CodeDmgEmulator(romPath, bootRomPath, savePath);
            linked.SetAudioEnabled(false);
            linked.Mmu.IsLocalLinked = true;
            linked.Ppu.PaletteOverride = Helper.NormalizePaletteName(
                CodeDmgPlugin.ConfigSettings?.PeerScreenPalette?.Value,
                Helper.DefaultPeerPaletteName
            );
            LinkedEmulator = linked;
            ResetLocalLinkState();
        }

        internal void DetachLinkedEmulator()
        {
            LinkedEmulator = null;
            ResetLocalLinkState();
        }

        internal void DeserializeLinkedState(byte[] data)
        {
            if (LinkedEmulator == null || data == null) return;
            using (var ms = new System.IO.MemoryStream(data))
            using (var reader = new System.IO.BinaryReader(ms))
            {
                int version = reader.ReadInt32();
                if (version != SaveStateVersion) return;
                reader.ReadString();
                reader.ReadString();
                LinkedEmulator.Mmu.LoadState(reader);
                LinkedEmulator.Cpu.LoadState(reader);
                LinkedEmulator.Ppu.LoadState(reader);
                LinkedEmulator.Joypad.LoadState(reader);
                LinkedEmulator.Timer.LoadState(reader);
                LinkedEmulator.Apu.LoadState(reader);
            }
            Debug.Log("[CodeDMG] Linked emulator state loaded from peer savestate");
        }

        internal int _lastCycles = 4;
        private int StepSelfOneInstruction()
        {
            int sys = Mmu.CgbDoubleSpeed ? _lastCycles / 2 : _lastCycles;
            Timer.Step(_lastCycles);
            Apu.Step(sys);
            Ppu.Step(sys);
            int executed = Cpu.ExecuteInstruction();
            _lastCycles = executed;
            return executed;
        }

        private void LocalLinkStep(bool mainStepped, int cycles)
        {
            if (LinkedEmulator == null || cycles <= 0) return;

            if (_activeSerialMaster != 0 && !IsActiveMasterStillRequesting())
            {
                _activeSerialMaster = 0;
                _linkCycleAccum = 0;
            }

            if (_activeSerialMaster == 0)
                _activeSerialMaster = PickSerialMaster();

            if (_activeSerialMaster == 0) return;
            if ((_activeSerialMaster == 1) != mainStepped) return;

            MMU master = _activeSerialMaster == 1 ? Mmu : LinkedEmulator.Mmu;
            MMU slave  = _activeSerialMaster == 1 ? LinkedEmulator.Mmu : Mmu;
            _linkCycleAccum += cycles;

            int maxCycles = master.SerialClockCycles;
            while (_linkCycleAccum >= maxCycles && IsSerialMasterRequesting(master))
            {
                _linkCycleAccum -= maxCycles;
                int outBit = (master.SB >> 7) & 1;
                int inBit  = slave.ShiftBit(outBit);
                master.ShiftBit(inBit);

                if (!IsSerialMasterRequesting(master))
                {
                    _activeSerialMaster = 0;
                    _linkCycleAccum = 0;
                    break;
                }

            }
        }

        private int PickSerialMaster()
        {
            bool mainMaster = IsSerialMasterRequesting(Mmu);
            bool peerMaster = IsSerialMasterRequesting(LinkedEmulator.Mmu);

            if (mainMaster && !peerMaster) return 1;
            if (peerMaster && !mainMaster) return 2;
            if (!mainMaster) return 0;

            if (_mainLinkPlayerId != 0 && _linkedLinkPlayerId != 0)
                return _mainLinkPlayerId <= _linkedLinkPlayerId ? 1 : 2;

            return 1;
        }

        private bool IsActiveMasterStillRequesting()
        {
            if (_activeSerialMaster == 1) return IsSerialMasterRequesting(Mmu);
            if (_activeSerialMaster == 2 && LinkedEmulator != null) return IsSerialMasterRequesting(LinkedEmulator.Mmu);
            return false;
        }

        private static bool IsSerialMasterRequesting(MMU mmu)
        {
            return mmu != null && (mmu.SC & 0x81) == 0x81;
        }

        private void QueueLocalInputForFutureFrame()
        {
            uint frame = _linkFrame + (uint)_linkInputDelayFrames;
            if (frame < _nextQueuedLocalInputFrame) return;

            while (_nextQueuedLocalInputFrame <= frame)
            {
                _localInputFrames[_nextQueuedLocalInputFrame] = _localJoypad;
                _pendingJoypadPackets.Enqueue(new LinkJoypadPacket(_nextQueuedLocalInputFrame, _localJoypad));
                _nextQueuedLocalInputFrame++;
            }

            PruneLinkInputs(_linkFrame);
        }

        private void ResetLocalLinkState()
        {
            _linkFrame = 0;
            _nextQueuedLocalInputFrame = (uint)_linkInputDelayFrames;
            _activeSerialMaster = 0;
            _linkCycleAccum = 0;
            _lastCycles = 4;
            if (LinkedEmulator != null)
                LinkedEmulator._lastCycles = 4;
            DesyncDetected = false;
            _localInputFrames.Clear();
            _peerInputFrames.Clear();
            _pendingJoypadPackets.Clear();
            for (int i = 0; i < RollbackDepth; i++)
            {
                if (_snapshotSlots[i] == null) _snapshotSlots[i] = new byte[SnapshotSlotSize];
                _snapshotFrames[i] = uint.MaxValue;
                _snapshotLengths[i] = 0;
            }
            _unverifiedPredictions.Clear();
            _predictedPeerInputs.Clear();
            _lastKnownPeerJoypad = 0xFF;

            for (uint i = 0; i < _linkInputDelayFrames; i++)
            {
                _localInputFrames[i] = 0xFF;
                _peerInputFrames[i] = 0xFF;
            }

            Mmu.LinkReset();
            LinkedEmulator?.Mmu.LinkReset();
        }

        private void PruneLinkInputs(uint frame)
        {
            if (frame <= MaxBufferedLinkInputs) return;
            uint minFrame = frame - MaxBufferedLinkInputs;
            PruneInputDictionary(_localInputFrames, minFrame);
            PruneInputDictionary(_peerInputFrames, minFrame);
            PruneInputDictionary(_predictedPeerInputs, minFrame);
            PruneInputDictionary(_unverifiedPredictions, minFrame);
        }

        private void PruneInputDictionary(Dictionary<uint, byte> dict, uint minFrame)
        {
            if (dict.Count == 0) return;
            _pruneScratch.Clear();
            foreach (var kv in dict)
                if (kv.Key < minFrame) _pruneScratch.Add(kv.Key);
            for (int i = 0; i < _pruneScratch.Count; i++)
                dict.Remove(_pruneScratch[i]);
        }

        private struct LinkJoypadPacket
        {
            public readonly uint Frame;
            public readonly byte State;

            public LinkJoypadPacket(uint frame, byte state)
            {
                Frame = frame;
                State = state;
            }
        }

        private void SaveRollbackSnapshot(uint frame)
        {
            int slot = (int)(frame % RollbackDepth);
            if (_snapshotSlots[slot] == null) _snapshotSlots[slot] = new byte[SnapshotSlotSize];
            byte[] buf = _snapshotSlots[slot];
            _snapshotFrames[slot] = frame;

            _snapshotStream.SetLength(0);
            using (var w = new System.IO.BinaryWriter(_snapshotStream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                Mmu.SaveState(w);
                Cpu.SaveState(w);
                Ppu.SaveStateNoBuffers(w);
                Timer.SaveState(w);
                w.Write(_lastCycles);
                w.Write(_linkCycleAccum);
                w.Write(_activeSerialMaster);
                LinkedEmulator.Mmu.SaveState(w);
                LinkedEmulator.Cpu.SaveState(w);
                LinkedEmulator.Ppu.SaveStateNoBuffers(w);
                LinkedEmulator.Timer.SaveState(w);
                w.Write(LinkedEmulator._lastCycles);
            }
            int len = (int)_snapshotStream.Length;
            if (buf.Length < len) { buf = new byte[len + 65536]; _snapshotSlots[slot] = buf; }
            System.Array.Copy(_snapshotStream.GetBuffer(), 0, buf, 0, len);
            _snapshotLengths[slot] = len;

            if (frame >= RollbackDepth)
                _predictedPeerInputs.Remove(frame - (uint)RollbackDepth);
        }

        private void RestoreRollbackSnapshot(uint frame)
        {
            int slot = (int)(frame % RollbackDepth);
            if (_snapshotFrames[slot] != frame) return;
            byte[] buf = _snapshotSlots[slot];

            int rlen = _snapshotLengths[slot];
            if (rlen == 0) return;
            using (var ms = new System.IO.MemoryStream(buf, 0, rlen, false))
            using (var r  = new System.IO.BinaryReader(ms))
            {
                Mmu.LoadState(r);
                Cpu.LoadState(r);
                Ppu.LoadStateNoBuffers(r);
                Timer.LoadState(r);
                _lastCycles         = r.ReadInt32();
                _linkCycleAccum     = r.ReadInt32();
                _activeSerialMaster = r.ReadInt32();
                LinkedEmulator.Mmu.LoadState(r);
                LinkedEmulator.Cpu.LoadState(r);
                LinkedEmulator.Ppu.LoadStateNoBuffers(r);
                LinkedEmulator.Timer.LoadState(r);
                LinkedEmulator._lastCycles = r.ReadInt32();
            }
        }

        public void SaveRam()
        {
            Mmu.Save(_savePath);
        }

        public void LoadSaveRam()
        {
            Mmu.Load(_savePath);
        }

        public byte[] SerializeState()
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                writer.Write(SaveStateVersion);
                writer.Write(_romPath ?? string.Empty);
                writer.Write(_bootRomPath ?? string.Empty);
                Mmu.SaveState(writer);
                Cpu.SaveState(writer);
                Ppu.SaveState(writer);
                Joypad.SaveState(writer);
                Timer.SaveState(writer);
                Apu.SaveState(writer);
                writer.Flush();
                return ms.ToArray();
            }
        }

        public void DeserializeState(byte[] data, bool skipPathCheck = false)
        {
            using (MemoryStream ms = new MemoryStream(data))
            using (BinaryReader reader = new BinaryReader(ms))
            {
                int version = reader.ReadInt32();
                if (version != SaveStateVersion)
                    throw new InvalidOperationException("Unsupported savestate version: " + version);

                string savedRomPath = reader.ReadString();
                string savedBootRomPath = reader.ReadString();

                if (!skipPathCheck && !string.Equals(savedRomPath ?? string.Empty, _romPath ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Savestate ROM mismatch.");

                Mmu.LoadState(reader);
                Cpu.LoadState(reader);
                Ppu.LoadState(reader);
                Joypad.LoadState(reader);
                Timer.LoadState(reader);
                Apu.LoadState(reader);
            }
        }

    }
}