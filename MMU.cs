using System;
using System.IO;

// ═══════════════════════════════════════════════════════════════════════════════
// MMU.cs — Game Boy / Game Boy Color Memory Management Unit
//
// GBC additions vs the original DMG-only file:
//
//  • isGbc           – auto-detected from ROM header byte 0x143 (0x80/0xC0)
//  • CgbDoubleSpeed  – set by CPU after a KEY1-triggered STOP
//  • KEY1   (0xFF4D) – speed-switch prepare + current-speed read
//  • VBK    (0xFF4F) – VRAM bank select (0 or 1)
//  • HDMA   (0xFF51-0xFF55) – General/H-Blank DMA (General-purpose stub)
//  • RP     (0xFF56) – Infrared port stub (reads 0xC0, writes ignored)
//  • BCPS   (0xFF68) – BG colour-palette specification
//  • BCPD   (0xFF69) – BG colour-palette data (64 bytes)
//  • OCPS   (0xFF6A) – OBJ colour-palette specification
//  • OCPD   (0xFF6B) – OBJ colour-palette data (64 bytes)
//  • SVBK   (0xFF70) – WRAM bank select (1-7, 0 treated as 1)
//  • WRAM   expanded to 32 KB (8 × 4 KB banks)
//  • VRAM   expanded to 16 KB (2 × 8 KB banks)
// ═══════════════════════════════════════════════════════════════════════════════

public class MMU
{
    // ── Memory arrays ─────────────────────────────────────────────────────────
    private readonly byte[] rom;
    private byte[] wram;          // 8 KB (DMG) or 32 KB (GBC)
    private byte[] vram;          // 8 KB (DMG) or 16 KB (GBC)
    private readonly byte[] oam   = new byte[0x00A0];
    private readonly byte[] hram  = new byte[0x007F];
    private readonly byte[] io    = new byte[0x0080];
    private byte[] bootRom;
    private bool bootEnabled;
    private MBC mbc;

    private const int BOOT_ROM_SIZE_DMG = 0x0100;   // 256 bytes
    private const int BOOT_ROM_SIZE_GBC = 0x0900;   // 2304 bytes

    // ── GBC colour-palette RAM ─────────────────────────────────────────────────
    // 8 palettes × 4 colours × 2 bytes = 64 bytes each
    private readonly byte[] bgPalRam  = new byte[64];
    private readonly byte[] objPalRam = new byte[64];

    // ── Hardware registers ─────────────────────────────────────────────────────
    public byte IE;         // 0xFFFF
    public byte IF;         // 0xFF0F
    public byte JOYP;       // 0xFF00
    public byte DIV;        // 0xFF04
    public byte TIMA;       // 0xFF05
    public byte TMA;        // 0xFF06
    public byte TAC;        // 0xFF07
    public byte LCDC;       // 0xFF40
    public byte STAT;       // 0xFF41
    public byte SCY;        // 0xFF42
    public byte SCX;        // 0xFF43
    public byte LY;         // 0xFF44
    public byte LYC;        // 0xFF45
    public byte BGP;        // 0xFF47  (DMG BG palette)
    public byte OBP0;       // 0xFF48  (DMG OBJ palette 0)
    public byte OBP1;       // 0xFF49  (DMG OBJ palette 1)
    public byte WY;         // 0xFF4A
    public byte WX;         // 0xFF4B
    public byte NR50;       // 0xFF24
    public byte NR51;       // 0xFF25
    public byte NR52;       // 0xFF26

    // GBC-specific registers
    private byte key1;              // 0xFF4D – speed switch (bit0=prepare, bit7=current)
    private byte vbk;               // 0xFF4F – VRAM bank (bit0)
    private byte svbk;              // 0xFF70 – WRAM bank (0-7, 0→1)
    private byte bcps;              // 0xFF68 – BG palette spec
    private byte ocps;              // 0xFF6A – OBJ palette spec

    // HDMA (0xFF51-FF55) – we implement General-purpose DMA only;
    // H-Blank DMA is a stub (writes are accepted, no per-scanline transfer)
    private ushort hdmaSrc;
    private ushort hdmaDst;
    private bool   hdmaActive;
    private int    hdmaRemaining;   // bytes left (multiples of 16)

    // ── Mode flags ─────────────────────────────────────────────────────────────
    /// <summary>True when the loaded ROM is a GBC title (header byte 0x143 = 0x80 or 0xC0).</summary>
    public bool IsGbc { get; private set; }

    /// <summary>True when the GBC CPU is running in double-speed mode (set by CPU on STOP after KEY1 prepare).</summary>
    public bool CgbDoubleSpeed { get; private set; }

    /// <summary>
    /// Legacy "flat RAM" mode from the original CODE-DMG.
    /// When true, all reads/writes go directly into the flat <see cref="ram"/> array.
    /// </summary>
    public bool mode;

    // ── Flat RAM (legacy flat-mode only) ──────────────────────────────────────
    public byte[] ram;

    // ── Shared subsystem references ────────────────────────────────────────────
    public APU Apu;
    public Timer Timer;
    public byte joypadState = 0xFF;

    // ── Constructor ───────────────────────────────────────────────────────────
    public MMU(byte[] gameRom, byte[] bootRomData, bool legacyFlatMode)
    {
        rom      = gameRom;
        bootRom  = bootRomData ?? new byte[BOOT_ROM_SIZE_DMG];
        mode     = legacyFlatMode;
        ram      = new byte[65536];

        // Detect GBC from the ROM header (byte 0x143).
        // 0x80 = CGB compatible (can also run on DMG)
        // 0xC0 = CGB only
        IsGbc = (rom.Length > 0x143) &&
                (rom[0x143] == 0x80 || rom[0x143] == 0xC0);

        // Allocate the correct WRAM / VRAM sizes.
        wram = IsGbc ? new byte[0x8000] : new byte[0x2000];   // 32 KB / 8 KB
        vram = IsGbc ? new byte[0x4000] : new byte[0x2000];   // 16 KB / 8 KB

        bootEnabled = true;
        mbc = new MBC(rom);

        Console.WriteLine($"MMU init | GBC={IsGbc}");
    }

    // ── Bank helpers ──────────────────────────────────────────────────────────

    /// <summary>Current VRAM bank index (0 or 1). DMG is always 0.</summary>
    private int VramBank => IsGbc ? (vbk & 0x01) : 0;

    /// <summary>Current WRAM bank index for 0xD000–0xDFFF (1-7). DMG is always 1 (i.e., offset 4 KB into 8 KB WRAM).</summary>
    private int WramBank => IsGbc ? Math.Max(1, svbk & 0x07) : 1;

    // ── Public palette-RAM accessors (used by PPU) ────────────────────────────

    /// <summary>Returns the 15-bit GBC colour for BG palette <paramref name="pal"/>, colour index <paramref name="col"/>.</summary>
    public ushort GetBgColor(int pal, int col)
    {
        int idx = (pal * 4 + col) * 2;
        return (ushort)(bgPalRam[idx] | (bgPalRam[idx + 1] << 8));
    }

    /// <summary>Returns the 15-bit GBC colour for OBJ palette <paramref name="pal"/>, colour index <paramref name="col"/>.</summary>
    public ushort GetObjColor(int pal, int col)
    {
        int idx = (pal * 4 + col) * 2;
        return (ushort)(objPalRam[idx] | (objPalRam[idx + 1] << 8));
    }

    /// <summary>Reads a byte from VRAM bank 1 (tile attribute data in GBC mode).</summary>
    public byte ReadVramBank1(ushort address)
    {
        if (!IsGbc) return 0xFF;
        int offset = (address - 0x8000) + 0x2000; // bank-1 starts at +8 KB
        if ((uint)offset < (uint)vram.Length) return vram[offset];
        return 0xFF;
    }

    // ── Speed switch (called by CPU on STOP when KEY1 bit0 is set) ────────────
    /// <summary>
    /// Toggle double-speed mode. Should be called by the CPU when it executes
    /// a STOP instruction and KEY1 bit 0 (prepare) is set.
    /// </summary>
    public void ExecuteSpeedSwitch()
    {
        if (!IsGbc) return;
        CgbDoubleSpeed = !CgbDoubleSpeed;
        key1 = (byte)(CgbDoubleSpeed ? 0x80 : 0x00); // clear prepare bit, update current-speed bit
        Console.WriteLine($"GBC speed switch → {(CgbDoubleSpeed ? "double" : "normal")}");
    }

    // ── Save / Load ───────────────────────────────────────────────────────────
    public void Save(string path)
    {
        if (mbc.mbcType != 0)
        {
            Console.WriteLine("Writing save to: " + path);
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

    public string HeaderInfo() =>
        mbc.GetTitle() + "\n" + mbc.GetCartridgeType() + "\n" +
        mbc.GetRomSize()  + "\n" + mbc.GetRamSize() + "\n" + mbc.GetChecksum();

    // ── Read / Write dispatch ─────────────────────────────────────────────────
    public byte Read(ushort address)
    {
        return mode ? Read2(address) : Read1(address);
    }

    public void Write(ushort address, byte value)
    {
        if (mode) Write2(address, value);
        else      Write1(address, value);
    }

    // Flat-RAM mode (legacy)
    private byte Read2(ushort address)  => ram[address];
    private void Write2(ushort address, byte value) => ram[address] = value;

    // ── Main Read ─────────────────────────────────────────────────────────────
    private byte Read1(ushort address)
    {
        // Boot ROM
        if (bootEnabled)
        {
            int bootSize = (bootRom.Length >= BOOT_ROM_SIZE_GBC) ? BOOT_ROM_SIZE_GBC : BOOT_ROM_SIZE_DMG;
            // GBC boot ROM has a hole at 0x0100-0x01FF (cartridge header is visible there)
            if (address < 0x0100 && address < bootSize)
                return bootRom[address];
            if (IsGbc && address >= 0x0200 && address < bootSize)
                return bootRom[address];
        }

        // ROM / MBC / Cartridge RAM
        if (address < 0x8000 || (address >= 0xA000 && address < 0xC000))
            return mbc.Read(address);

        // VRAM 0x8000–0x9FFF  (banked on GBC)
        if (address >= 0x8000 && address < 0xA000)
        {
            int offset = (address - 0x8000) + VramBank * 0x2000;
            return ((uint)offset < (uint)vram.Length) ? vram[offset] : (byte)0xFF;
        }

        // WRAM bank 0: 0xC000–0xCFFF
        if (address >= 0xC000 && address < 0xD000)
            return wram[address - 0xC000];

        // WRAM banked: 0xD000–0xDFFF
        if (address >= 0xD000 && address < 0xE000)
        {
            int offset = WramBank * 0x1000 + (address - 0xD000);
            return ((uint)offset < (uint)wram.Length) ? wram[offset] : (byte)0xFF;
        }

        // Echo RAM 0xE000–0xFDFF → maps to 0xC000–0xDDFF
        if (address >= 0xE000 && address < 0xFE00)
            return Read1((ushort)(address - 0x2000));

        // OAM 0xFE00–0xFE9F
        if (address >= 0xFE00 && address < 0xFEA0)
            return oam[address - 0xFE00];

        // Unusable 0xFEA0–0xFEFF
        if (address >= 0xFEA0 && address < 0xFF00)
            return 0xFF;

        // HRAM 0xFF80–0xFFFE
        if (address >= 0xFF80 && address < 0xFFFF)
            return hram[address - 0xFF80];

        // IE
        if (address == 0xFFFF) return IE;

        // I/O 0xFF00–0xFF7F
        return ReadIO(address);
    }

    // ── I/O Read ──────────────────────────────────────────────────────────────
    private byte ReadIO(ushort address)
    {
        switch (address)
        {
            case 0xFF00: // JOYP
                if ((JOYP & 0x10) == 0) return (byte)((joypadState >> 4) | 0x20);
                if ((JOYP & 0x20) == 0) return (byte)((joypadState & 0x0F) | 0x10);
                return (byte)(JOYP | 0xFF);

            case 0xFF04: return DIV;
            case 0xFF05: return TIMA;
            case 0xFF06: return TMA;
            case 0xFF07: return TAC;
            case 0xFF0F: return IF;

            // APU
            case 0xFF10: case 0xFF11: case 0xFF12: case 0xFF13: case 0xFF14:
            case 0xFF16: case 0xFF17: case 0xFF18: case 0xFF19:
            case 0xFF1A: case 0xFF1B: case 0xFF1C: case 0xFF1D: case 0xFF1E:
            case 0xFF20: case 0xFF21: case 0xFF22: case 0xFF23:
            case 0xFF24: case 0xFF25: case 0xFF26:
                return Apu != null ? Apu.ReadRegister(address) : io[address - 0xFF00];

            // PPU
            case 0xFF40: return LCDC;
            case 0xFF41: return STAT;
            case 0xFF42: return SCY;
            case 0xFF43: return SCX;
            case 0xFF44: return LY;
            case 0xFF45: return LYC;
            case 0xFF47: return BGP;
            case 0xFF48: return OBP0;
            case 0xFF49: return OBP1;
            case 0xFF4A: return WY;
            case 0xFF4B: return WX;

            // GBC – KEY1
            case 0xFF4D:
                if (!IsGbc) return 0xFF;
                return (byte)(key1 | 0x7E); // unused bits return 1

            // GBC – VBK
            case 0xFF4F:
                if (!IsGbc) return 0xFF;
                return (byte)(vbk | 0xFE); // only bit 0 is meaningful; bits 1-7 return 1

            // GBC – HDMA status
            case 0xFF55:
                if (!IsGbc) return 0xFF;
                // bit7=0: transfer active (h-blank), bit7=1: no active h-blank transfer
                // For our general DMA stub we report inactive when idle
                return hdmaActive ? (byte)((hdmaRemaining / 16 - 1) & 0x7F) : (byte)0xFF;

            // GBC – RP (infrared)
            case 0xFF56:
                return IsGbc ? (byte)0xC0 : (byte)0xFF;

            // GBC – BG palette
            case 0xFF68: return IsGbc ? bcps : (byte)0xFF;
            case 0xFF69:
                if (!IsGbc) return 0xFF;
                return bgPalRam[bcps & 0x3F];

            // GBC – OBJ palette
            case 0xFF6A: return IsGbc ? ocps : (byte)0xFF;
            case 0xFF6B:
                if (!IsGbc) return 0xFF;
                return objPalRam[ocps & 0x3F];

            // GBC – SVBK
            case 0xFF70:
                return IsGbc ? (byte)(svbk | 0xF8) : (byte)0xFF;

            // Wave RAM
            default:
                if (address >= 0xFF30 && address <= 0xFF3F)
                    return Apu != null ? Apu.ReadRegister(address) : io[address - 0xFF00];
                if (address >= 0xFF00 && address < 0xFF80)
                    return io[address - 0xFF00];
                return 0xFF;
        }
    }

    // ── Main Write ────────────────────────────────────────────────────────────
    private void Write1(ushort address, byte value)
    {
        // Boot ROM disable
        if (address == 0xFF50)
        {
            bootEnabled = false;
            return;
        }

        // ROM / MBC / Cartridge RAM
        if (address < 0x8000 || (address >= 0xA000 && address < 0xC000))
        {
            mbc.Write(address, value);
            return;
        }

        // VRAM 0x8000–0x9FFF (banked on GBC)
        if (address >= 0x8000 && address < 0xA000)
        {
            int offset = (address - 0x8000) + VramBank * 0x2000;
            if ((uint)offset < (uint)vram.Length)
                vram[offset] = value;
            return;
        }

        // WRAM bank 0: 0xC000–0xCFFF
        if (address >= 0xC000 && address < 0xD000)
        {
            wram[address - 0xC000] = value;
            return;
        }

        // WRAM banked: 0xD000–0xDFFF
        if (address >= 0xD000 && address < 0xE000)
        {
            int offset = WramBank * 0x1000 + (address - 0xD000);
            if ((uint)offset < (uint)wram.Length)
                wram[offset] = value;
            return;
        }

        // Echo RAM
        if (address >= 0xE000 && address < 0xFE00)
        {
            Write1((ushort)(address - 0x2000), value);
            return;
        }

        // OAM
        if (address >= 0xFE00 && address < 0xFEA0)
        {
            oam[address - 0xFE00] = value;
            return;
        }

        // HRAM
        if (address >= 0xFF80 && address < 0xFFFF)
        {
            hram[address - 0xFF80] = value;
            return;
        }

        // IE
        if (address == 0xFFFF) { IE = value; return; }

        // I/O
        WriteIO(address, value);
    }

    // ── I/O Write ─────────────────────────────────────────────────────────────
    private void WriteIO(ushort address, byte value)
    {
        switch (address)
        {
            case 0xFF00: JOYP = (byte)(value & 0x30); return;

            case 0xFF04:
                if (Timer != null) Timer.ResetDiv();
                else DIV = 0;
                return;

            case 0xFF05: TIMA = value; return;
            case 0xFF06: TMA  = value; return;
            case 0xFF07: TAC  = (byte)(value & 0x07); return;
            case 0xFF0F: IF   = value; return;

            // APU
            case 0xFF10: case 0xFF11: case 0xFF12: case 0xFF13: case 0xFF14:
            case 0xFF16: case 0xFF17: case 0xFF18: case 0xFF19:
            case 0xFF1A: case 0xFF1B: case 0xFF1C: case 0xFF1D: case 0xFF1E:
            case 0xFF20: case 0xFF21: case 0xFF22: case 0xFF23:
            case 0xFF24: case 0xFF25: case 0xFF26:
                Apu?.WriteRegister(address, value);
                io[address - 0xFF00] = value;
                return;

            // PPU
            case 0xFF40:
                LCDC = value;
                if ((value & 0x80) == 0) { STAT &= 0x7C; LY = 0x00; }
                return;

            case 0xFF41: STAT = value; return;
            case 0xFF42: SCY  = value; return;
            case 0xFF43: SCX  = value; return;
            case 0xFF44: LY   = value; return;
            case 0xFF45: LYC  = value; return;

            case 0xFF46: // OAM DMA
                ushort src = (ushort)(value << 8);
                for (ushort i = 0; i < 0xA0; i++)
                    Write((ushort)(0xFE00 + i), Read((ushort)(src + i)));
                return;

            case 0xFF47: BGP  = value; return;
            case 0xFF48: OBP0 = value; return;
            case 0xFF49: OBP1 = value; return;
            case 0xFF4A: WY   = value; return;
            case 0xFF4B: WX   = value; return;

            // GBC – KEY1 (speed-switch prepare)
            case 0xFF4D:
                if (!IsGbc) return;
                key1 = (byte)((key1 & 0x80) | (value & 0x01)); // only bit0 is writable
                return;

            // GBC – VBK (VRAM bank)
            case 0xFF4F:
                if (!IsGbc) return;
                vbk = (byte)(value & 0x01);
                return;

            // GBC – HDMA source high (0xFF51)
            case 0xFF51:
                if (!IsGbc) return;
                hdmaSrc = (ushort)((value << 8) | (hdmaSrc & 0x00FF));
                return;

            // GBC – HDMA source low (0xFF52)
            case 0xFF52:
                if (!IsGbc) return;
                hdmaSrc = (ushort)((hdmaSrc & 0xFF00) | (value & 0xF0)); // low 4 bits ignored
                return;

            // GBC – HDMA dest high (0xFF53)
            case 0xFF53:
                if (!IsGbc) return;
                hdmaDst = (ushort)(((value & 0x1F) << 8) | (hdmaDst & 0x00FF)); // only bits 12-8
                return;

            // GBC – HDMA dest low (0xFF54)
            case 0xFF54:
                if (!IsGbc) return;
                hdmaDst = (ushort)((hdmaDst & 0xFF00) | (value & 0xF0)); // low 4 bits ignored
                return;

            // GBC – HDMA control / start (0xFF55)
            case 0xFF55:
            {
                if (!IsGbc) return;

                if (hdmaActive && (value & 0x80) == 0)
                {
                    // Writing 0 to bit7 while H-Blank DMA is active cancels it
                    hdmaActive    = false;
                    hdmaRemaining = 0;
                    return;
                }

                int length = ((value & 0x7F) + 1) * 16; // 16–2048 bytes
                bool hblankMode = (value & 0x80) != 0;

                if (!hblankMode)
                {
                    // General-purpose DMA: transfer immediately
                    ushort s = (ushort)(hdmaSrc & 0xFFF0);
                    ushort d = (ushort)(0x8000 | (hdmaDst & 0x1FF0));
                    for (int i = 0; i < length; i++)
                        Write((ushort)(d + i), Read((ushort)(s + i)));
                    hdmaActive    = false;
                    hdmaRemaining = 0;
                }
                else
                {
                    // H-Blank DMA: store parameters, PPU will call StepHdma() each HBlank
                    hdmaActive    = true;
                    hdmaRemaining = length;
                }
                return;
            }

            // GBC – RP (infrared) – stub, writes ignored
            case 0xFF56:
                return;

            // GBC – BG palette spec
            case 0xFF68:
                if (!IsGbc) return;
                bcps = (byte)(value & 0xBF); // bit6 is always 0 when read back (auto-increment flag kept)
                return;

            // GBC – BG palette data
            case 0xFF69:
                if (!IsGbc) return;
            {
                int idx = bcps & 0x3F;
                bgPalRam[idx] = value;
                if ((bcps & 0x80) != 0)                        // auto-increment
                    bcps = (byte)((bcps & 0x80) | ((idx + 1) & 0x3F));
                return;
            }

            // GBC – OBJ palette spec
            case 0xFF6A:
                if (!IsGbc) return;
                ocps = (byte)(value & 0xBF);
                return;

            // GBC – OBJ palette data
            case 0xFF6B:
                if (!IsGbc) return;
            {
                int idx = ocps & 0x3F;
                objPalRam[idx] = value;
                if ((ocps & 0x80) != 0)
                    ocps = (byte)((ocps & 0x80) | ((idx + 1) & 0x3F));
                return;
            }

            // GBC – SVBK (WRAM bank)
            case 0xFF70:
                if (!IsGbc) return;
                svbk = (byte)(value & 0x07);
                return;

            // Wave RAM
            default:
                if (address >= 0xFF30 && address <= 0xFF3F)
                {
                    Apu?.WriteRegister(address, value);
                    io[address - 0xFF00] = value;
                    return;
                }
                if (address >= 0xFF00 && address < 0xFF80)
                    io[address - 0xFF00] = value;
                return;
        }
    }

    // ── H-Blank HDMA step (called by PPU at the start of each HBlank) ─────────
    /// <summary>
    /// Transfers one 16-byte block of H-Blank DMA. Call this once per HBlank
    /// when <see cref="IsGbc"/> is true.
    /// </summary>
    public void StepHdma()
    {
        if (!IsGbc || !hdmaActive || hdmaRemaining <= 0) return;

        ushort s = (ushort)(hdmaSrc & 0xFFF0);
        ushort d = (ushort)(0x8000 | (hdmaDst & 0x1FF0));

        for (int i = 0; i < 16; i++)
            Write((ushort)(d + i), Read((ushort)(s + i)));

        hdmaSrc = (ushort)(hdmaSrc + 16);
        hdmaDst = (ushort)(hdmaDst + 16);
        hdmaRemaining -= 16;

        if (hdmaRemaining <= 0)
        {
            hdmaActive    = false;
            hdmaRemaining = 0;
        }
    }

    // ── State serialisation ───────────────────────────────────────────────────
    private static void WriteByteArray(BinaryWriter w, byte[] data)
    {
        if (data == null) { w.Write(-1); return; }
        w.Write(data.Length);
        w.Write(data);
    }

    private static byte[] ReadByteArray(BinaryReader r)
    {
        int len = r.ReadInt32();
        return len < 0 ? null : r.ReadBytes(len);
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
        writer.Write(IsGbc);
        writer.Write(CgbDoubleSpeed);
        writer.Write(IE); writer.Write(IF); writer.Write(JOYP);
        writer.Write(DIV); writer.Write(TIMA); writer.Write(TMA); writer.Write(TAC);
        writer.Write(LCDC); writer.Write(STAT);
        writer.Write(SCY); writer.Write(SCX); writer.Write(LY); writer.Write(LYC);
        writer.Write(BGP); writer.Write(OBP0); writer.Write(OBP1);
        writer.Write(WY); writer.Write(WX);
        writer.Write(NR50); writer.Write(NR51); writer.Write(NR52);
        writer.Write(joypadState);
        // GBC extras
        writer.Write(key1); writer.Write(vbk); writer.Write(svbk);
        writer.Write(bcps); writer.Write(ocps);
        writer.Write(bgPalRam); writer.Write(objPalRam);
        writer.Write(hdmaSrc); writer.Write(hdmaDst);
        writer.Write(hdmaActive); writer.Write(hdmaRemaining);
        mbc.SaveState(writer);
    }

    public void LoadState(BinaryReader reader)
    {
        byte[] loadedWram  = ReadByteArray(reader);
        byte[] loadedVram  = ReadByteArray(reader);
        byte[] loadedOam   = ReadByteArray(reader);
        byte[] loadedHram  = ReadByteArray(reader);
        byte[] loadedIo    = ReadByteArray(reader);
        byte[] loadedRam   = ReadByteArray(reader);

        // Resize if GBC state was saved with larger banks
        if (loadedWram != null)
        {
            if (loadedWram.Length != wram.Length) wram = new byte[loadedWram.Length];
            Array.Copy(loadedWram, wram, wram.Length);
        }
        if (loadedVram != null)
        {
            if (loadedVram.Length != vram.Length) vram = new byte[loadedVram.Length];
            Array.Copy(loadedVram, vram, vram.Length);
        }
        if (loadedOam  != null) Array.Copy(loadedOam,  oam,  Math.Min(oam.Length,  loadedOam.Length));
        if (loadedHram != null) Array.Copy(loadedHram, hram, Math.Min(hram.Length, loadedHram.Length));
        if (loadedIo   != null) Array.Copy(loadedIo,   io,   Math.Min(io.Length,   loadedIo.Length));
        if (loadedRam  != null && loadedRam.Length == ram.Length) Array.Copy(loadedRam, ram, ram.Length);

        bootEnabled    = reader.ReadBoolean();
        mode           = reader.ReadBoolean();
        // IsGbc and CgbDoubleSpeed are stored in the state
        bool savedIsGbc = reader.ReadBoolean();
        CgbDoubleSpeed  = reader.ReadBoolean();

        IE = reader.ReadByte(); IF = reader.ReadByte(); JOYP = reader.ReadByte();
        DIV = reader.ReadByte(); TIMA = reader.ReadByte(); TMA = reader.ReadByte(); TAC = reader.ReadByte();
        LCDC = reader.ReadByte(); STAT = reader.ReadByte();
        SCY = reader.ReadByte(); SCX = reader.ReadByte(); LY = reader.ReadByte(); LYC = reader.ReadByte();
        BGP = reader.ReadByte(); OBP0 = reader.ReadByte(); OBP1 = reader.ReadByte();
        WY = reader.ReadByte(); WX = reader.ReadByte();
        NR50 = reader.ReadByte(); NR51 = reader.ReadByte(); NR52 = reader.ReadByte();
        joypadState = reader.ReadByte();

        // GBC extras
        key1 = reader.ReadByte(); vbk = reader.ReadByte(); svbk = reader.ReadByte();
        bcps = reader.ReadByte(); ocps = reader.ReadByte();

        byte[] bgPal  = reader.ReadBytes(64);
        byte[] objPal = reader.ReadBytes(64);
        Array.Copy(bgPal,  bgPalRam,  64);
        Array.Copy(objPal, objPalRam, 64);

        hdmaSrc       = reader.ReadUInt16();
        hdmaDst       = reader.ReadUInt16();
        hdmaActive    = reader.ReadBoolean();
        hdmaRemaining = reader.ReadInt32();

        mbc.LoadState(reader);
    }
}
