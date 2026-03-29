using System;
using System.IO;
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

        private readonly string _savePath;
        private readonly string _romPath;
        private readonly string _bootRomPath;

        public string RomPath => _romPath;
        public string BootRomPath => _bootRomPath;
        public string SavePath => _savePath;

        public bool FrameDirty => Ppu.FrameDirty;

        public CodeDmgEmulator(string romPath, string bootRomPath, string savePath)
        {
            _romPath = romPath;
            _bootRomPath = bootRomPath;
            _savePath = savePath;

            byte[] gameRom = File.ReadAllBytes(romPath);
            byte[] bootRom = File.Exists(bootRomPath) ? File.ReadAllBytes(bootRomPath) : new byte[256];

            Helper.scale = 1;
            Helper.paletteName = "dmg";

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

                // When no boot ROM is present, real hardware would already have left the
                // MMU/APU in a post-boot state. CPU.Reset() only handles CPU/video basics.
                // CGB games that touch audio immediately can start from a bad APU state
                // unless we also apply CGB post-boot audio defaults.
                if (isGbc)
                {
                    Mmu.InitializeCGBRegisters();

                    // Re-sync the APU's internal state with the visible MMU audio registers.
                    // InitializeCGBRegisters() writes MMU regs directly, but the APU channel
                    // state machines must also see a proper power-on sequence.
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

        public void DeserializeState(byte[] data)
        {
            using (MemoryStream ms = new MemoryStream(data))
            using (BinaryReader reader = new BinaryReader(ms))
            {
                int version = reader.ReadInt32();
                if (version != SaveStateVersion)
                    throw new InvalidOperationException("Unsupported savestate version: " + version);

                string savedRomPath = reader.ReadString();
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