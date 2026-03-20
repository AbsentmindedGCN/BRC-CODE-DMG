using System;
using System.IO;

namespace BRCCodeDmg
{
    public sealed class CodeDmgEmulator
    {
        private const int CyclesPerFrame = 70224;
        private const int SaveStateVersion = 2;

        private MMU Mmu { get; }
        private CPU Cpu { get; }
        public PPU Ppu { get; }
        public APU Apu { get; }
        private Joypad Joypad { get; }
        private Timer Timer { get; }

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

            Mmu = new MMU(gameRom, bootRom, false);
            Cpu = new CPU(Mmu);
            Ppu = new PPU(Mmu);
            Joypad = new Joypad(Mmu);
            Apu = new APU(Mmu);
            Timer = new Timer(Mmu, Apu);

            Mmu.Apu = Apu;
            Mmu.Timer = Timer;

            if (!File.Exists(bootRomPath))
            {
                Cpu.Reset();

                // Approximate post-boot DMG audio defaults
                Mmu.NR52 = 0x80; // APU enabled
                Mmu.NR50 = 0x77; // full left/right master volume
                Mmu.NR51 = 0xF3; // common DMG routing default
            }

            Mmu.Load(_savePath);
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

            while (cycles < CyclesPerFrame)
            {
                int executed = Cpu.ExecuteInstruction();
                cycles += executed;

                Ppu.Step(executed);
                Timer.Step(executed);
                Apu.Step(executed);
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

    public enum GameBoyButton : byte
    {
        A = 0x01,
        B = 0x02,
        Select = 0x04,
        Start = 0x08,
        Right = 0x10,
        Left = 0x20,
        Up = 0x40,
        Down = 0x80
    }
}