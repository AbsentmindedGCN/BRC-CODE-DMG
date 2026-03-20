using System;
using System.IO;

public class MMU
{
    private byte[] rom; //ROM
    private byte[] wram; //Work RAM
    private byte[] vram; //Video RAM
    private byte[] oam; //Object Attribute Memory
    private byte[] hram; //High RAM
    private byte[] io; //I/O Registers
    private byte[] bootRom; //Boot ROM
    private bool bootEnabled; //Boot ROM enabled flag

    private MBC mbc;

    private const int ROM_SIZE = 0x8000; //32KB
    private const int BOOT_ROM_SIZE = 0x0100; //256 bytes
    private const int WRAM_SIZE = 0x2000; //8KB
    private const int VRAM_SIZE = 0x2000; //8KB
    private const int OAM_SIZE = 0x00A0; //160 bytes
    private const int HRAM_SIZE = 0x007F; //127 bytes
    private const int IO_SIZE = 0x0080; //128 bytes

    public byte IE; //0xFFFF
    public byte IF; //0xFF0F
    public byte JOYP; //0xFF00
    public byte DIV; //0xFF04
    public byte TIMA; //0xFF05
    public byte TMA; //0xFF06
    public byte TAC; //0xFF07
    public byte LCDC; //0xFF40
    public byte STAT; //0xFF41
    public byte SCY; //0xFF42
    public byte SCX; //0xFF43
    public byte LY; //0xFF44
    public byte LYC; //0xFF45
    public byte BGP; //0xFF47
    public byte OBP0; //0xFF48
    public byte OBP1; //0xFF49
    public byte WY; //0xFF4A
    public byte WX; //0xFF4B

    public byte NR50; //0xFF24
    public byte NR51; //0xFF25
    public byte NR52; //0xFF26

    public byte joypadState = 0xFF; //Raw inputs

    public byte[] ram; //64 KB RAM
    public bool mode;

    public APU Apu;
    public Timer Timer;

    public MMU(byte[] gameRom, byte[] bootRomData, bool mode)
    {
        rom = gameRom;
        bootRom = bootRomData;
        wram = new byte[WRAM_SIZE];
        vram = new byte[VRAM_SIZE];
        oam = new byte[OAM_SIZE];
        hram = new byte[HRAM_SIZE];
        io = new byte[IO_SIZE];
        bootEnabled = true;

        ram = new byte[65536];
        this.mode = mode;

        mbc = new MBC(rom);

        Console.WriteLine("MMU init");
    }

    public void Save(string path)
    {
        if (mbc.mbcType != 0)
        {
            Console.WriteLine("Writing to save to: " + path);
            File.WriteAllBytes(path, mbc.ramBanks);
        }
    }

    public void Load(string path)
    {
        if (File.Exists(path) && mbc.mbcType != 0)
        {
            Console.WriteLine("Loading save: " + path);
            mbc.ramBanks = File.ReadAllBytes(path);
        }
        else if (mbc.mbcType != 0)
        {
            Console.WriteLine("Save not found at: " + path);
        }
    }

    public string HeaderInfo()
    {
        return mbc.GetTitle() + "\n" + mbc.GetCartridgeType() + "\n" + mbc.GetRomSize() + "\n" + mbc.GetRamSize() + "\n" + mbc.GetChecksum();
    }

    public byte Read(ushort address)
    {
        if (mode == false)
        {
            return Read1(address);
        }
        else if (mode == true)
        {
            return Read2(address);
        }
        return 0xFF;
    }

    public void Write(ushort address, byte value)
    {
        if (mode == false)
        {
            Write1(address, value);
        }
        else if (mode == true)
        {
            Write2(address, value);
        }
    }

    public void Write2(ushort address, byte value)
    {
        ram[address] = value;
    }

    public byte Read2(ushort address)
    {
        return ram[address];
    }

    public byte Read1(ushort address)
    {
        if (bootEnabled && address < BOOT_ROM_SIZE)
        {
            return bootRom[address]; //Boot ROM
        }

        if (address < 0x8000 || (address >= 0xA000 && address < 0xC000))
        {
            return mbc.Read(address); //Delegate to MBC
        }

        switch (address)
        {
            case 0xFF00:
                // if action or direction buttons are selected
                if ((JOYP & 0x10) == 0)
                { // Action buttons selected
                    return (byte)((joypadState >> 4) | 0x20);
                }
                else if ((JOYP & 0x20) == 0)
                { // Direction buttons selected
                    return (byte)((joypadState & 0x0F) | 0x10);
                }
                return (byte)(JOYP | 0xFF);

            case 0xFF04:
                return DIV;
            case 0xFF05:
                return TIMA;
            case 0xFF06:
                return TMA;
            case 0xFF07:
                return TAC;

            case 0xFF0F:
                return IF;

            case 0xFF10:
            case 0xFF11:
            case 0xFF12:
            case 0xFF13:
            case 0xFF14:
            case 0xFF16:
            case 0xFF17:
            case 0xFF18:
            case 0xFF19:
            case 0xFF1A:
            case 0xFF1B:
            case 0xFF1C:
            case 0xFF1D:
            case 0xFF1E:
            case 0xFF20:
            case 0xFF21:
            case 0xFF22:
            case 0xFF23:
            case 0xFF24:
            case 0xFF25:
            case 0xFF26:
                return Apu != null ? Apu.ReadRegister(address) : io[address - 0xFF00];

            case 0xFF40:
                return LCDC;
            case 0xFF41:
                return STAT;
            case 0xFF42:
                return SCY;
            case 0xFF43:
                return SCX;
            case 0xFF44:
                return LY;
            case 0xFF45:
                return LYC;
            case 0xFF47:
                return BGP;
            case 0xFF48:
                return OBP0;
            case 0xFF49:
                return OBP1;
            case 0xFF4A:
                return WY;
            case 0xFF4B:
                return WX;

            case 0xFFFF:
                return IE;
        }

        if (address >= 0xFF30 && address <= 0xFF3F)
        {
            return Apu != null ? Apu.ReadRegister(address) : io[address - 0xFF00];
        }

        if (address >= 0xC000 && address < 0xE000)
        {
            return wram[address - 0xC000];
        }
        else if (address >= 0x8000 && address < 0xA000)
        {
            return vram[address - 0x8000];
        }
        else if (address >= 0xFE00 && address < 0xFEA0)
        {
            return oam[address - 0xFE00];
        }
        else if (address >= 0xFF80 && address < 0xFFFF)
        {
            return hram[address - 0xFF80];
        }
        else if (address >= 0xFF00 && address < 0xFF80)
        {
            return io[address - 0xFF00]; //Mostly as a fallback
        }

        return 0xFF; //Default values of unknown reads
    }

    public void Write1(ushort address, byte value)
    {
        if (address == 0xFF50)
        {
            //Disable boot ROM if written to
            bootEnabled = false;
            return;
        }

        if (address < 0x8000 || (address >= 0xA000 && address < 0xC000))
        {
            mbc.Write(address, value); //Delegate to MBC
            return;
        }

        switch (address)
        {
            case 0xFF00:
                JOYP = (byte)(value & 0x30);
                break;

            case 0xFF04:
                if (Timer != null)
                    Timer.ResetDiv();
                else
                    DIV = 0;
                break;

            case 0xFF05:
                TIMA = value;
                break;

            case 0xFF06:
                TMA = value;
                break;

            case 0xFF07:
                TAC = (byte)(value & 0x07);
                break;

            case 0xFF0F:
                IF = value;
                break;

            case 0xFF10:
            case 0xFF11:
            case 0xFF12:
            case 0xFF13:
            case 0xFF14:
            case 0xFF16:
            case 0xFF17:
            case 0xFF18:
            case 0xFF19:
            case 0xFF1A:
            case 0xFF1B:
            case 0xFF1C:
            case 0xFF1D:
            case 0xFF1E:
            case 0xFF20:
            case 0xFF21:
            case 0xFF22:
            case 0xFF23:
            case 0xFF24:
            case 0xFF25:
            case 0xFF26:
                if (Apu != null)
                    Apu.WriteRegister(address, value);
                io[address - 0xFF00] = value;
                return;

            case 0xFF40:
                LCDC = value;
                if ((value & 0x80) == 0)
                {
                    STAT &= 0x7C;
                    LY = 0x00;
                }
                break;

            case 0xFF41:
                STAT = value;
                break;

            case 0xFF42:
                SCY = value;
                break;

            case 0xFF43:
                SCX = value;
                break;

            case 0xFF44:
                LY = value;
                break;

            case 0xFF45:
                LYC = value;
                break;

            case 0xFF46: //DMA
                ushort sourceAddress = (ushort)(value << 8);
                for (ushort i = 0; i < 0xA0; i++)
                {
                    Write((ushort)(0xFE00 + i), Read((ushort)(sourceAddress + i)));
                }
                break;

            case 0xFF47:
                BGP = value;
                break;

            case 0xFF48:
                OBP0 = value;
                break;

            case 0xFF49:
                OBP1 = value;
                break;

            case 0xFF4A:
                WY = value;
                break;

            case 0xFF4B:
                WX = value;
                break;

            case 0xFFFF:
                IE = value;
                break;
        }

        if (address >= 0xFF30 && address <= 0xFF3F)
        {
            if (Apu != null)
                Apu.WriteRegister(address, value);
            io[address - 0xFF00] = value;
            return;
        }

        if (address >= 0xC000 && address < 0xE000)
        {
            wram[address - 0xC000] = value;
        }
        else if (address >= 0x8000 && address < 0xA000)
        {
            vram[address - 0x8000] = value;
        }
        else if (address >= 0xFE00 && address < 0xFEA0)
        {
            oam[address - 0xFE00] = value;
        }
        else if (address >= 0xFF80 && address < 0xFFFF)
        {
            hram[address - 0xFF80] = value;
        }
        else if (address >= 0xFF00 && address < 0xFF80)
        {
            io[address - 0xFF00] = value; //Mostly as a fallback
        }
        else if (address == 0xFFFF)
        {
            // IE accounted for in switch statement, else if here to prevent "OUT OF RANGE" message
        }
        else
        {
            Console.WriteLine(address.ToString("X4") + " - OUT OF RANGE WRITE");
        }
    }

    private static void WriteByteArray(BinaryWriter writer, byte[] data)
    {
        if (data == null)
        {
            writer.Write(-1);
            return;
        }

        writer.Write(data.Length);
        writer.Write(data);
    }

    private static byte[] ReadByteArray(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0)
            return null;

        return reader.ReadBytes(length);
    }

    public void SaveState(BinaryWriter writer)
    {
        WriteByteArray(writer, wram);
        WriteByteArray(writer, vram);
        WriteByteArray(writer, oam);
        WriteByteArray(writer, hram);
        WriteByteArray(writer, io);
        WriteByteArray(writer, ram);

        writer.Write(bootEnabled);
        writer.Write(mode);

        writer.Write(IE);
        writer.Write(IF);
        writer.Write(JOYP);
        writer.Write(DIV);
        writer.Write(TIMA);
        writer.Write(TMA);
        writer.Write(TAC);
        writer.Write(LCDC);
        writer.Write(STAT);
        writer.Write(SCY);
        writer.Write(SCX);
        writer.Write(LY);
        writer.Write(LYC);
        writer.Write(BGP);
        writer.Write(OBP0);
        writer.Write(OBP1);
        writer.Write(WY);
        writer.Write(WX);
        writer.Write(NR50);
        writer.Write(NR51);
        writer.Write(NR52);
        writer.Write(joypadState);

        mbc.SaveState(writer);
    }

    public void LoadState(BinaryReader reader)
    {
        byte[] loadedWram = ReadByteArray(reader);
        byte[] loadedVram = ReadByteArray(reader);
        byte[] loadedOam = ReadByteArray(reader);
        byte[] loadedHram = ReadByteArray(reader);
        byte[] loadedIo = ReadByteArray(reader);
        byte[] loadedRam = ReadByteArray(reader);

        if (loadedWram == null || loadedWram.Length != wram.Length)
            throw new InvalidOperationException("Invalid WRAM length in savestate.");
        if (loadedVram == null || loadedVram.Length != vram.Length)
            throw new InvalidOperationException("Invalid VRAM length in savestate.");
        if (loadedOam == null || loadedOam.Length != oam.Length)
            throw new InvalidOperationException("Invalid OAM length in savestate.");
        if (loadedHram == null || loadedHram.Length != hram.Length)
            throw new InvalidOperationException("Invalid HRAM length in savestate.");
        if (loadedIo == null || loadedIo.Length != io.Length)
            throw new InvalidOperationException("Invalid IO length in savestate.");
        if (loadedRam == null || loadedRam.Length != ram.Length)
            throw new InvalidOperationException("Invalid RAM length in savestate.");

        Array.Copy(loadedWram, wram, wram.Length);
        Array.Copy(loadedVram, vram, vram.Length);
        Array.Copy(loadedOam, oam, oam.Length);
        Array.Copy(loadedHram, hram, hram.Length);
        Array.Copy(loadedIo, io, io.Length);
        Array.Copy(loadedRam, ram, ram.Length);

        bootEnabled = reader.ReadBoolean();
        mode = reader.ReadBoolean();

        IE = reader.ReadByte();
        IF = reader.ReadByte();
        JOYP = reader.ReadByte();
        DIV = reader.ReadByte();
        TIMA = reader.ReadByte();
        TMA = reader.ReadByte();
        TAC = reader.ReadByte();
        LCDC = reader.ReadByte();
        STAT = reader.ReadByte();
        SCY = reader.ReadByte();
        SCX = reader.ReadByte();
        LY = reader.ReadByte();
        LYC = reader.ReadByte();
        BGP = reader.ReadByte();
        OBP0 = reader.ReadByte();
        OBP1 = reader.ReadByte();
        WY = reader.ReadByte();
        WX = reader.ReadByte();
        NR50 = reader.ReadByte();
        NR51 = reader.ReadByte();
        NR52 = reader.ReadByte();
        joypadState = reader.ReadByte();

        mbc.LoadState(reader);
    }
}