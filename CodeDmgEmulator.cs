using System;
using System.IO;
using UnityEngine;

namespace BRCCodeDmg
{
    public sealed class CodeDmgEmulator
    {
        // ── Cycle budget per frame ─────────────────────────────────────────────
        // DMG / GBC normal-speed: 70224 T-cycles per frame (59.7 Hz)
        // GBC double-speed:      140448 T-cycles per frame
        // The CPU returns 2x T-cycles per instruction in double-speed mode,
        // so the frame loop runs to the same wall-clock time either way.
        private const int CyclesPerFrameNormal = 70224;
        private int CyclesPerFrame =>
            Mmu != null && Mmu.CgbDoubleSpeed ? CyclesPerFrameNormal * 2 : CyclesPerFrameNormal;

        private const int SaveStateVersion = 2; // bumped from 1 for GBC state fields

        private MMU Mmu { get; }
        private CPU Cpu { get; }
        public PPU  Ppu  { get; }
        public APU  Apu  { get; }
        private Joypad Joypad { get; }
        private Timer  Timer  { get; }

        public bool AudioEnabled { get; private set; } = false;

        private readonly string _savePath;
        private readonly string _romPath;
        private readonly string _bootRomPath;

        public string RomPath     => _romPath;
        public string BootRomPath => _bootRomPath;
        public string SavePath    => _savePath;

        public bool FrameDirty => Ppu.FrameDirty;

        // ── Constructor ───────────────────────────────────────────────────────
        public CodeDmgEmulator(string romPath, string bootRomPath, string savePath)
        {
            _romPath     = romPath;
            _bootRomPath = bootRomPath;
            _savePath    = savePath;

            byte[] gameRom = File.ReadAllBytes(romPath);

            // ── GBC detection ─────────────────────────────────────────────────
            // ROM header byte 0x143: 0x80 = GBC compatible, 0xC0 = GBC only.
            bool isGbc = gameRom.Length > 0x143 &&
                         (gameRom[0x143] == 0x80 || gameRom[0x143] == 0xC0);

            // ── Boot ROM selection ────────────────────────────────────────────
            // Prefer a supplied boot ROM.  If the path points to a GBC boot ROM
            // (2304 bytes = 0x900) use it as-is; a DMG boot ROM (256 bytes) is
            // also accepted for GBC games (the CPU Reset() path sets GBC regs).
            byte[] bootRom = null;
            if (File.Exists(bootRomPath))
            {
                bootRom = File.ReadAllBytes(bootRomPath);
            }
            else if (isGbc)
            {
                // Try common alternative filenames next to the ROM.
                string dir = Path.GetDirectoryName(romPath) ?? "";
                string[] candidates = { "gbc_boot.bin", "cgb_boot.bin", "cgb_bios.bin" };
                foreach (string c in candidates)
                {
                    string p = Path.Combine(dir, c);
                    if (File.Exists(p)) { bootRom = File.ReadAllBytes(p); break; }
                }
            }

            if (bootRom == null)
                bootRom = new byte[isGbc ? 0x900 : 0x100]; // zeroed placeholder

            Helper.scale       = 1;
            Helper.paletteName = "dmg"; // GBC games use palette RAM (mmu.GetBgColor/GetObjColor) not this table

            Mmu    = new MMU(gameRom, bootRom, false);
            Cpu    = new CPU(Mmu);
            Ppu    = new PPU(Mmu);
            Joypad = new Joypad(Mmu);
            Apu    = new APU(Mmu, UnityEngine.AudioSettings.outputSampleRate);
            Timer  = new Timer(Mmu, Apu);

            Mmu.Apu   = Apu;
            Mmu.Timer = Timer;

            // If no valid boot ROM was found, jump straight to 0x0100 with the
            // correct post-boot register state for the detected hardware type.
            bool hasValidBoot = bootRom.Length >= (isGbc ? 0x900 : 0x100) &&
                                bootRom[0] != 0x00; // a real boot ROM starts with code

            if (!hasValidBoot)
                Cpu.Reset(isGbc);  // see CPU.cs patch guide for signature change

            Mmu.Load(_savePath);
        }

        // ── Audio ─────────────────────────────────────────────────────────────
        public void SetAudioEnabled(bool enabled)
        {
            AudioEnabled = enabled;
            Apu?.SetEnabled(enabled);
        }

        // ── Main emulation loop ───────────────────────────────────────────────
        public void StepFrame()
        {
            int cycles = 0;
            int budget  = CyclesPerFrame;

            while (cycles < budget)
            {
                int executed = Cpu.ExecuteInstruction();
                cycles += executed;

                // In GBC double-speed mode the CPU ticks at 2x the rate of the
                // PPU and APU.  Divide executed cycles by 2 so those subsystems
                // advance at the correct 4.194 MHz wall-clock speed.
                int systemCycles = Mmu.CgbDoubleSpeed ? executed / 2 : executed;

                Ppu.Step(systemCycles);
                Timer.Step(executed);   // Timer always receives full CPU cycles
                Apu.Step(systemCycles); // APU receives system (half) cycles
            }
        }

        // ── Input ─────────────────────────────────────────────────────────────
        public void SetButton(GameBoyButton button, bool pressed)
        {
            Joypad.SetButton(button, pressed);
        }

        // ── Save RAM ──────────────────────────────────────────────────────────
        public void SaveRam()
        {
            Mmu.Save(_savePath);
        }

        // ── Save states ───────────────────────────────────────────────────────
        public byte[] SerializeState()
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                writer.Write(SaveStateVersion);
                writer.Write(_romPath     ?? string.Empty);
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

        public void DeserializeState(byte[] data)
        {
            using (MemoryStream ms = new MemoryStream(data))
            using (BinaryReader reader = new BinaryReader(ms))
            {
                int version = reader.ReadInt32();
                if (version != SaveStateVersion)
                    throw new InvalidOperationException(
                        $"Save state version mismatch: expected {SaveStateVersion}, got {version}");

                string savedRom  = reader.ReadString();
                string savedBoot = reader.ReadString();

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
