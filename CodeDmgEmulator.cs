using System;
using System.IO;
using UnityEngine;

namespace BRCCodeDmg
{
    public sealed class CodeDmgEmulator
    {
        //private const int CyclesPerFrame = 70224;
        private const int CyclesPerFrameNormal = 70224;

        // Bumped from 1 → 2 because MMU layout expanded for GBC support
        // (dual VRAM banks, 8×WRAM banks, CGB palette data, HDMA state).
        // Old save states will fail gracefully with a clear error message.
        private const int SaveStateVersion = 2;

        private MMU    Mmu    { get; }
        private CPU    Cpu    { get; }
        public  PPU    Ppu    { get; }
        public  APU    Apu    { get; }
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

        public CodeDmgEmulator(string romPath, string bootRomPath, string savePath)
        {
            _romPath     = romPath;
            _bootRomPath = bootRomPath;
            _savePath    = savePath;

            byte[] gameRom = File.ReadAllBytes(romPath);
            byte[] bootRom = File.Exists(bootRomPath) ? File.ReadAllBytes(bootRomPath) : new byte[256];

            Helper.scale       = 1;
            Helper.paletteName = "dmg";

            // MMU auto-detects GBC mode from cartridge header byte 0x143
            // (0x80 = GBC-compatible, 0xC0 = GBC-only)
            Mmu    = new MMU(gameRom, bootRom, false);
            Cpu    = new CPU(Mmu);
            Ppu    = new PPU(Mmu);
            Joypad = new Joypad(Mmu);
            Apu    = new APU(Mmu, UnityEngine.AudioSettings.outputSampleRate);
            Timer  = new Timer(Mmu, Apu);

            Mmu.Apu   = Apu;
            Mmu.Timer = Timer;

            /*
            if (!File.Exists(bootRomPath))
            {
                // GBC FIX: pass isGbc so the CPU sets A=0x11 (GBC hardware ID).
                // Without this, GBC games see A=0x01 (DMG) and show the
                // "This game is designed for use on Game Boy Color" warning.
                Cpu.Reset(Mmu.IsCGBMode);

                // Seed GBC palette / WRAM-bank / audio registers to the
                // values the CGB boot ROM would have left behind.
                if (Mmu.IsCGBMode)
                    Mmu.InitializeCGBRegisters();
            }
            */

            if (!File.Exists(bootRomPath))
            {
                // Detect GBC from cartridge header byte 0x143 (0x80/0xC0 = GBC).
                // Pass isGbc so CPU sets A=0x11 (GBC hardware ID) instead of A=0x01 (DMG).
                // Without this, GBC games show the "designed for Game Boy Color" warning.
                bool isGbc = gameRom.Length > 0x143 &&
                             (gameRom[0x143] == 0x80 || gameRom[0x143] == 0xC0);
                Cpu.Reset(isGbc);
            }

            Mmu.Load(_savePath);
        }

        public void SetAudioEnabled(bool enabled)
        {
            AudioEnabled = enabled;
            Apu?.SetEnabled(enabled);
        }

        /*
        public void StepFrame()
        {
            int cycles = 0;
            while (cycles < CyclesPerFrame)
            {
                int executed = Cpu.ExecuteInstruction();
                cycles += executed;
                Ppu.Step(executed);
                Timer.Step(executed);
                Apu.Step(executed);
            }
        }
        */

        public void StepFrame()
        {
            int cycles = 0;
            int budget = Mmu.CgbDoubleSpeed ? CyclesPerFrameNormal * 2 : CyclesPerFrameNormal;

            while (cycles < budget)
            {
                int executed = Cpu.ExecuteInstruction();
                cycles += executed;

                // In double-speed mode the CPU runs at 2× the clock of the PPU/APU.
                // Halve the cycles so those subsystems advance at the correct rate.
                int systemCycles = Mmu.CgbDoubleSpeed ? executed / 2 : executed;

                Ppu.Step(systemCycles);
                Timer.Step(executed);   // Timer always receives full CPU cycles
                Apu.Step(systemCycles); // APU receives system-speed cycles
            }
        }

        public void SetButton(GameBoyButton button, bool pressed)
        {
            Joypad.SetButton(button, pressed);
        }

        public void SaveRam()
        {
            Mmu.Save(_savePath);
        }

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
                    throw new InvalidOperationException("Unsupported savestate version: " + version);

                string savedRomPath     = reader.ReadString();
                string savedBootRomPath = reader.ReadString();

                if (!string.Equals(savedRomPath ?? string.Empty, _romPath ?? string.Empty, StringComparison.OrdinalIgnoreCase))
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
