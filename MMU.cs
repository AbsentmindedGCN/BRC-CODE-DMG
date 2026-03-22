using System;
using System.IO;
using UnityEngine;

// =============================================================================
//  MMU — Memory Management Unit (DMG + GBC)
//
//  Key changes vs. original:
//   • Auto-detects GBC mode from cartridge header byte 0x0143.
//   • Dual VRAM banks (FF4F / VBK).  Bank 0 = vram[], Bank 1 = vramBank1[].
//   • 8 × 4 KB WRAM banks (FF70 / SVBK).  Bank 0 fixed at 0xC000, banks 1–7
//     switchable at 0xD000.  wram[] is now 32 KB to hold all eight banks.
//   • CGB BG palette registers FF68 (BCPS) / FF69 (BCPD), 8 palettes × 4 colors.
//   • CGB OBJ palette registers FF6A (OCPS) / FF6B (OCPD), same layout.
//   • HDMA registers FF51–FF55:  General DMA executes immediately; H-Blank DMA
//     is stepped one 16-byte block per HBlank by the PPU calling ExecuteHBlankDMA().
//   • KEY1 (FF4D) acknowledged so the speed-switch write does not lock up games.
//   • ReadVramDirect / GetCGBBgColor / GetCGBObjColor exposed for the PPU.
//   • SaveStateVersion bumped to 2 – old saves are intentionally incompatible.
// =============================================================================

public class MMU
{
    // -------------------------------------------------------------------------
    // Memory arrays
    // -------------------------------------------------------------------------
    private byte[] rom;
    private byte[] wram;        // 32 KB: 8 banks × 4 KB
    private byte[] vram;        // 8 KB  VRAM bank 0
    private byte[] vramBank1;   // 8 KB  VRAM bank 1 (GBC only)
    private byte[] oam;
    private byte[] hram;
    private byte[] io;          // I/O fallback storage
    private byte[] bootRom;
    private bool   bootEnabled;
    private MBC    mbc;

    // Sizes
    private const int BOOT_ROM_SIZE = 0x0100;
    private const int WRAM_SIZE     = 0x8000; // 32 KB (8 × 4 KB banks)
    private const int VRAM_SIZE     = 0x2000;
    private const int OAM_SIZE      = 0x00A0;
    private const int HRAM_SIZE     = 0x007F;
    private const int IO_SIZE       = 0x0080;

    // -------------------------------------------------------------------------
    // Standard hardware registers (exposed as fields for performance)
    // -------------------------------------------------------------------------
    public byte IE;          // 0xFFFF
    public byte IF;          // 0xFF0F
    public byte JOYP;        // 0xFF00
    public byte DIV;         // 0xFF04
    public byte TIMA;        // 0xFF05
    public byte TMA;         // 0xFF06
    public byte TAC;         // 0xFF07
    public byte LCDC;        // 0xFF40
    public byte STAT;        // 0xFF41
    public byte SCY;         // 0xFF42
    public byte SCX;         // 0xFF43
    public byte LY;          // 0xFF44
    public byte LYC;         // 0xFF45
    public byte BGP;         // 0xFF47
    public byte OBP0;        // 0xFF48
    public byte OBP1;        // 0xFF49
    public byte WY;          // 0xFF4A
    public byte WX;          // 0xFF4B
    public byte NR50;        // 0xFF24
    public byte NR51;        // 0xFF25
    public byte NR52;        // 0xFF26
    public byte joypadState = 0xFF;

    // -------------------------------------------------------------------------
    // GBC mode
    // -------------------------------------------------------------------------
    /// <summary>True when the cartridge header (0x0143) identifies a GBC game.</summary>
    public bool IsCGBMode { get; private set; }

    // Aliases used by CPU.cs and Timer.cs
    public bool IsGbc          => IsCGBMode;
    public bool CgbDoubleSpeed => cgbDoubleSpeed;
    public void ExecuteSpeedSwitch()
    {
        if (!IsCGBMode || !cgbSpeedSwitchPending) return;
        cgbDoubleSpeed        = !cgbDoubleSpeed;
        cgbSpeedSwitchPending = false;
    }

    // VRAM banking – FF4F (VBK)
    private int vramBankIndex;  // 0 or 1

    // WRAM banking – FF70 (SVBK)
    private int wramBankIndex;  // 1–7 (writing 0 maps to 1 per spec)

    // CGB palettes: 64 bytes each (8 pals × 4 colours × 2 bytes, little-endian 15-bit RGB)
    private readonly byte[] cgbBgPaletteData  = new byte[64];
    private readonly byte[] cgbObjPaletteData = new byte[64];
    private byte cgbBcps;   // FF68: BG palette spec  (bits 0-5 = index, bit 7 = auto-inc)
    private byte cgbOcps;   // FF6A: OBJ palette spec

    // HDMA – FF51-FF55
    private byte hdmaSrcHi;
    private byte hdmaSrcLo;
    private byte hdmaDstHi;
    private byte hdmaDstLo;
    private int  hdmaBlocksLeft;   // -1 = inactive, 0..127 = blocks remaining
    private bool hdmaHBlankMode;   // false = General DMA, true = H-Blank DMA

    // Speed switch – FF4D (KEY1)
    private bool cgbDoubleSpeed;
    private bool cgbSpeedSwitchPending;

    // -------------------------------------------------------------------------
    // Legacy flat-RAM mode (mode == true)
    // -------------------------------------------------------------------------
    public byte[] ram;   // 64 KB flat RAM used when mode == true
    public bool   mode;  // false = proper address-decoded, true = flat

    // -------------------------------------------------------------------------
    // Subsystem references (set after construction)
    // -------------------------------------------------------------------------
    public APU   Apu;
    public Timer Timer;

    // =========================================================================
    // Constructor
    // =========================================================================
    public MMU(byte[] gameRom, byte[] bootRomData, bool flatRamMode)
    {
        rom         = gameRom;
        bootRom     = bootRomData;
        wram        = new byte[WRAM_SIZE];
        vram        = new byte[VRAM_SIZE];
        vramBank1   = new byte[VRAM_SIZE];
        oam         = new byte[OAM_SIZE];
        hram        = new byte[HRAM_SIZE];
        io          = new byte[IO_SIZE];
        bootEnabled = true;
        ram         = new byte[65536];
        mode        = flatRamMode;
        mbc         = new MBC(rom);

        // Auto-detect GBC from cartridge header byte 0x0143:
        //   0x80 = "works on GBC and DMG", 0xC0 = "GBC only"
        IsCGBMode = (rom.Length > 0x0143) &&
                    (rom[0x0143] == 0x80 || rom[0x0143] == 0xC0);

        // Default WRAM bank 1 (bank 0 is fixed at 0xC000-0xCFFF;
        // using bank index 1 for 0xD000-0xDFFF is correct for both DMG and GBC).
        wramBankIndex  = 1;
        vramBankIndex  = 0;
        hdmaBlocksLeft = -1;

        Console.WriteLine($"MMU init – CGB mode: {IsCGBMode}");
    }

    // =========================================================================
    // GBC palette helpers for the PPU
    // =========================================================================

    /// <summary>
    /// Read a byte from VRAM at a relative address (0x0000–0x1FFF), from a
    /// specific bank.  Used by the PPU during rendering to read tile attributes
    /// from bank 1 independent of the current VBK register value.
    /// </summary>
    public byte ReadVramDirect(int relAddr, int bank)
    {
        int idx = relAddr & 0x1FFF;
        if (bank == 0)            return vram[idx];
        if (IsCGBMode)            return vramBank1[idx];
        return 0xFF;
    }

    /// <summary>Returns the GBC BG colour for the given palette + colour index as a Unity Color32.</summary>
    public Color32 GetCGBBgColor(int paletteIndex, int colorIndex)
    {
        int offset  = (paletteIndex * 4 + colorIndex) * 2;
        int color15 = cgbBgPaletteData[offset] | (cgbBgPaletteData[offset + 1] << 8);
        return Color15ToColor32(color15);
    }

    /// <summary>Returns the GBC OBJ colour for the given palette + colour index as a Unity Color32.</summary>
    public Color32 GetCGBObjColor(int paletteIndex, int colorIndex)
    {
        int offset  = (paletteIndex * 4 + colorIndex) * 2;
        int color15 = cgbObjPaletteData[offset] | (cgbObjPaletteData[offset + 1] << 8);
        return Color15ToColor32(color15);
    }

    // GBC uses 15-bit BGR555.  Scale each 5-bit channel to 8-bit.
    private static Color32 Color15ToColor32(int c)
    {
        int r5 = (c)       & 0x1F;
        int g5 = (c >>  5) & 0x1F;
        int b5 = (c >> 10) & 0x1F;
        byte r = (byte)((r5 << 3) | (r5 >> 2));
        byte g = (byte)((g5 << 3) | (g5 >> 2));
        byte b = (byte)((b5 << 3) | (b5 >> 2));
        return new Color32(r, g, b, 255);
    }

    // =========================================================================
    // H-Blank DMA – called by the PPU once per HBlank when active
    // =========================================================================
    /// <summary>
    /// Copies one 16-byte block of H-Blank DMA into the current VRAM bank.
    /// Must be called by the PPU at the start of each HBlank period.
    /// </summary>
    public void ExecuteHBlankDMA()
    {
        if (!IsCGBMode || !hdmaHBlankMode || hdmaBlocksLeft < 0)
            return;

        ushort src = (ushort)((hdmaSrcHi << 8) | hdmaSrcLo);
        ushort dst = (ushort)(0x8000 | ((hdmaDstHi & 0x1F) << 8) | hdmaDstLo);

        for (int i = 0; i < 16; i++)
        {
            byte val = Read((ushort)((src + i) & 0xFFFF));
            WriteVramDirect((ushort)((dst + i) & 0x9FFF), val);
        }

        src = (ushort)((src + 16) & 0xFFFF);
        dst = (ushort)((dst + 16) & 0xFFFF);

        hdmaSrcHi = (byte)(src >> 8);
        hdmaSrcLo = (byte)(src & 0xF0);
        hdmaDstHi = (byte)((dst >> 8) & 0x1F);
        hdmaDstLo = (byte)(dst & 0xF0);

        hdmaBlocksLeft--;
        // When hdmaBlocksLeft reaches -1, DMA is complete.
    }

    // Write a byte directly into the currently selected VRAM bank (used by DMA).
    private void WriteVramDirect(ushort address, byte value)
    {
        int idx = (address - 0x8000) & 0x1FFF;
        if (vramBankIndex == 0 || !IsCGBMode)
            vram[idx] = value;
        else
            vramBank1[idx] = value;
    }

    // =========================================================================
    // HDMA private helpers
    // =========================================================================
    private void StartHDMA(byte value)
    {
        // Writing to FF55 while H-Blank DMA is in progress with bit 7 = 0 cancels it.
        if (hdmaHBlankMode && hdmaBlocksLeft >= 0 && (value & 0x80) == 0)
        {
            hdmaBlocksLeft = -1;
            return;
        }

        hdmaHBlankMode = (value & 0x80) != 0;
        hdmaBlocksLeft = value & 0x7F; // 0..127 → 1..128 blocks transferred

        if (!hdmaHBlankMode)
        {
            // General DMA: transfer everything immediately.
            ExecuteGeneralDMA(hdmaBlocksLeft + 1);
            hdmaBlocksLeft = -1;
        }
        // H-Blank DMA is processed one block per HBlank by the PPU.
    }

    private void ExecuteGeneralDMA(int blockCount)
    {
        ushort src = (ushort)((hdmaSrcHi << 8) | hdmaSrcLo);
        ushort dst = (ushort)(0x8000 | ((hdmaDstHi & 0x1F) << 8) | hdmaDstLo);
        int length = blockCount * 16;

        for (int i = 0; i < length; i++)
        {
            byte val = Read((ushort)((src + i) & 0xFFFF));
            WriteVramDirect((ushort)((dst + i) & 0x9FFF), val);
        }
    }

    // =========================================================================
    // MBC / Save
    // =========================================================================
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
        mbc.GetTitle()         + "\n" +
        mbc.GetCartridgeType() + "\n" +
        mbc.GetRomSize()       + "\n" +
        mbc.GetRamSize()       + "\n" +
        mbc.GetChecksum();

    // =========================================================================
    // GBC register initialisation (call when skipping the CGB boot ROM)
    // =========================================================================
    /// <summary>
    /// Seeds GBC hardware registers with post-boot-ROM values.
    /// Call this when no CGB boot ROM is available so the game starts in a
    /// valid state (correct LCDC, audio registers, palette defaults, etc.).
    /// </summary>
    public void InitializeCGBRegisters()
    {
        if (!IsCGBMode) return;

        LCDC = 0x91; STAT = 0x85;
        SCY = 0; SCX = 0; LY = 0; LYC = 0;
        BGP = 0xFC; OBP0 = 0xFF; OBP1 = 0xFF;
        WY = 0; WX = 0;
        NR52 = 0xF1; NR50 = 0x77; NR51 = 0xF3;
        IF = 0xE1; IE = 0x00;

        // Palette 0 → white-to-black gradient (matching boot ROM output)
        ushort[] grad = { 0x7FFF, 0x56B5, 0x294A, 0x0000 };
        for (int p = 0; p < 8; p++)
        {
            for (int c = 0; c < 4; c++)
            {
                int    offset = (p * 4 + c) * 2;
                ushort col    = (p == 0) ? grad[c] : (ushort)0x0000;
                cgbBgPaletteData [offset]     = (byte)(col & 0xFF);
                cgbBgPaletteData [offset + 1] = (byte)(col >> 8);
                cgbObjPaletteData[offset]     = (byte)(col & 0xFF);
                cgbObjPaletteData[offset + 1] = (byte)(col >> 8);
            }
        }
    }

    // =========================================================================
    // Read / Write dispatch
    // =========================================================================
    public byte Read(ushort address)
    {
        return mode ? Read2(address) : Read1(address);
    }

    public void Write(ushort address, byte value)
    {
        if (mode) Write2(address, value);
        else      Write1(address, value);
    }

    // Flat-RAM mode (legacy / testing only)
    public byte Read2(ushort address)              => ram[address];
    public void Write2(ushort address, byte value) { ram[address] = value; }

    // =========================================================================
    // Read1 – full address-decoded read (DMG + GBC)
    // =========================================================================
    public byte Read1(ushort address)
    {
        // Boot ROM
        if (bootEnabled && address < BOOT_ROM_SIZE)
            return bootRom[address];

        // ROM / external RAM (handled by MBC)
        if (address < 0x8000 || (address >= 0xA000 && address < 0xC000))
            return mbc.Read(address);

        // VRAM – 0x8000-0x9FFF, bank-switched in GBC
        if (address >= 0x8000 && address < 0xA000)
        {
            int idx = address - 0x8000;
            return (vramBankIndex == 0 || !IsCGBMode) ? vram[idx] : vramBank1[idx];
        }

        // WRAM bank 0 – 0xC000-0xCFFF (always fixed)
        if (address >= 0xC000 && address < 0xD000)
            return wram[address - 0xC000];

        // WRAM switchable bank – 0xD000-0xDFFF
        if (address >= 0xD000 && address < 0xE000)
            return wram[(wramBankIndex << 12) + (address - 0xD000)];

        // Echo RAM – 0xE000-0xFDFF mirrors 0xC000-0xDDFF
        if (address >= 0xE000 && address < 0xFE00)
        {
            ushort m = (ushort)(address - 0x2000);
            if (m < 0xD000) return wram[m - 0xC000];
            return wram[(wramBankIndex << 12) + (m - 0xD000)];
        }

        // OAM – 0xFE00-0xFE9F
        if (address >= 0xFE00 && address < 0xFEA0)
            return oam[address - 0xFE00];

        // HRAM – 0xFF80-0xFFFE
        if (address >= 0xFF80 && address < 0xFFFF)
            return hram[address - 0xFF80];

        // Wave RAM – 0xFF30-0xFF3F
        if (address >= 0xFF30 && address <= 0xFF3F)
            return Apu != null ? Apu.ReadRegister(address) : io[address - 0xFF00];

        // I/O registers
        switch (address)
        {
            case 0xFF00: // Joypad
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

            // LCD
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

            // GBC – VBK (VRAM bank, bits 1-7 always read as 1)
            case 0xFF4F: return (byte)(vramBankIndex | 0xFE);

            // GBC – KEY1 (speed switch)
            case 0xFF4D:
                if (!IsCGBMode) return 0xFF;
                return (byte)(
                    (cgbDoubleSpeed        ? 0x80 : 0x00) |
                    (cgbSpeedSwitchPending ? 0x01 : 0x00) |
                    0x7E);

            // GBC – HDMA (source/dest regs are write-only, FF55 shows remaining)
            case 0xFF51: return 0xFF;
            case 0xFF52: return 0xFF;
            case 0xFF53: return 0xFF;
            case 0xFF54: return 0xFF;
            case 0xFF55:
                if (!IsCGBMode) return 0xFF;
                if (hdmaBlocksLeft < 0) return 0xFF;
                return (byte)((hdmaHBlankMode ? 0x00 : 0x80) | (hdmaBlocksLeft & 0x7F));

            // GBC – BG palettes
            case 0xFF68: return IsCGBMode ? cgbBcps          : (byte)0xFF;
            case 0xFF69: return IsCGBMode ? cgbBgPaletteData [cgbBcps & 0x3F] : (byte)0xFF;

            // GBC – OBJ palettes
            case 0xFF6A: return IsCGBMode ? cgbOcps          : (byte)0xFF;
            case 0xFF6B: return IsCGBMode ? cgbObjPaletteData[cgbOcps & 0x3F] : (byte)0xFF;

            // GBC – SVBK (WRAM bank, bits 3-7 always 1)
            case 0xFF70: return IsCGBMode ? (byte)(wramBankIndex | 0xF8) : (byte)0xFF;

            case 0xFFFF: return IE;

            default:
                if (address >= 0xFF00 && address < 0xFF80)
                    return io[address - 0xFF00];
                return 0xFF;
        }
    }

    // =========================================================================
    // Write1 – full address-decoded write (DMG + GBC)
    // =========================================================================
    public void Write1(ushort address, byte value)
    {
        // Boot ROM disable
        if (address == 0xFF50) { bootEnabled = false; return; }

        // ROM / external RAM (handled by MBC)
        if (address < 0x8000 || (address >= 0xA000 && address < 0xC000))
        { mbc.Write(address, value); return; }

        // VRAM – 0x8000-0x9FFF, bank-switched in GBC
        if (address >= 0x8000 && address < 0xA000)
        {
            int idx = address - 0x8000;
            if (vramBankIndex == 0 || !IsCGBMode)
                vram[idx] = value;
            else
                vramBank1[idx] = value;
            return;
        }

        // WRAM bank 0 – 0xC000-0xCFFF
        if (address >= 0xC000 && address < 0xD000)
        { wram[address - 0xC000] = value; return; }

        // WRAM switchable bank – 0xD000-0xDFFF
        if (address >= 0xD000 && address < 0xE000)
        { wram[(wramBankIndex << 12) + (address - 0xD000)] = value; return; }

        // Echo RAM – ignore writes (mirrors to WRAM but writes rarely matter)
        if (address >= 0xE000 && address < 0xFE00)
        {
            ushort m = (ushort)(address - 0x2000);
            if (m < 0xD000) wram[m - 0xC000] = value;
            else            wram[(wramBankIndex << 12) + (m - 0xD000)] = value;
            return;
        }

        // OAM
        if (address >= 0xFE00 && address < 0xFEA0)
        { oam[address - 0xFE00] = value; return; }

        // HRAM
        if (address >= 0xFF80 && address < 0xFFFF)
        { hram[address - 0xFF80] = value; return; }

        // Wave RAM
        if (address >= 0xFF30 && address <= 0xFF3F)
        {
            Apu?.WriteRegister(address, value);
            io[address - 0xFF00] = value;
            return;
        }

        // I/O registers
        switch (address)
        {
            case 0xFF00: JOYP = (byte)(value & 0x30); return;

            case 0xFF04: Timer?.ResetDiv(); if (Timer == null) DIV = 0; return;
            case 0xFF05: TIMA = value; return;
            case 0xFF06: TMA  = value; return;
            case 0xFF07: TAC  = (byte)(value & 0x07); return;

            case 0xFF0F: IF = value; return;

            // APU
            case 0xFF10: case 0xFF11: case 0xFF12: case 0xFF13: case 0xFF14:
            case 0xFF16: case 0xFF17: case 0xFF18: case 0xFF19:
            case 0xFF1A: case 0xFF1B: case 0xFF1C: case 0xFF1D: case 0xFF1E:
            case 0xFF20: case 0xFF21: case 0xFF22: case 0xFF23:
            case 0xFF24: case 0xFF25: case 0xFF26:
                Apu?.WriteRegister(address, value);
                io[address - 0xFF00] = value;
                return;

            // LCD
            case 0xFF40:
                LCDC = value;
                if ((value & 0x80) == 0) { STAT &= 0x7C; LY = 0; }
                return;
            case 0xFF41: STAT = value; return;
            case 0xFF42: SCY  = value; return;
            case 0xFF43: SCX  = value; return;
            case 0xFF44: LY   = value; return;
            case 0xFF45: LYC  = value; return;

            // OAM DMA
            case 0xFF46:
            {
                ushort src = (ushort)(value << 8);
                for (ushort i = 0; i < 0xA0; i++)
                    Write((ushort)(0xFE00 + i), Read((ushort)(src + i)));
                return;
            }

            // DMG palettes
            case 0xFF47: BGP  = value; return;
            case 0xFF48: OBP0 = value; return;
            case 0xFF49: OBP1 = value; return;
            case 0xFF4A: WY   = value; return;
            case 0xFF4B: WX   = value; return;

            // GBC – VBK (VRAM bank select)
            case 0xFF4F:
                if (IsCGBMode) vramBankIndex = value & 0x01;
                return;

            // GBC – KEY1 (speed switch arm/execute)
            case 0xFF4D:
                if (IsCGBMode) cgbSpeedSwitchPending = (value & 0x01) != 0;
                return;

            // GBC – HDMA source / dest
            case 0xFF51: hdmaSrcHi = value;              return;
            case 0xFF52: hdmaSrcLo = (byte)(value & 0xF0); return;
            case 0xFF53: hdmaDstHi = (byte)(value & 0x1F); return;
            case 0xFF54: hdmaDstLo = (byte)(value & 0xF0); return;
            case 0xFF55: if (IsCGBMode) StartHDMA(value); return;

            // GBC – BG palette
            case 0xFF68:
                if (IsCGBMode) cgbBcps = (byte)(value & 0xBF);
                return;
            case 0xFF69:
                if (IsCGBMode)
                {
                    int idx = cgbBcps & 0x3F;
                    cgbBgPaletteData[idx] = value;
                    if ((cgbBcps & 0x80) != 0)
                        cgbBcps = (byte)((cgbBcps & 0x80) | ((idx + 1) & 0x3F));
                }
                return;

            // GBC – OBJ palette
            case 0xFF6A:
                if (IsCGBMode) cgbOcps = (byte)(value & 0xBF);
                return;
            case 0xFF6B:
                if (IsCGBMode)
                {
                    int idx = cgbOcps & 0x3F;
                    cgbObjPaletteData[idx] = value;
                    if ((cgbOcps & 0x80) != 0)
                        cgbOcps = (byte)((cgbOcps & 0x80) | ((idx + 1) & 0x3F));
                }
                return;

            // GBC – SVBK (WRAM bank select)
            case 0xFF70:
                if (IsCGBMode)
                {
                    wramBankIndex = value & 0x07;
                    if (wramBankIndex == 0) wramBankIndex = 1; // per spec
                }
                return;

            case 0xFFFF: IE = value; return;

            default:
                if (address >= 0xFF00 && address < 0xFF80)
                    io[address - 0xFF00] = value;
                return;
        }
    }

    // =========================================================================
    // Save State
    // =========================================================================
    private static void WriteByteArray(BinaryWriter w, byte[] data)
    {
        if (data == null) { w.Write(-1); return; }
        w.Write(data.Length);
        w.Write(data);
    }

    private static byte[] ReadByteArray(BinaryReader r)
    {
        int len = r.ReadInt32();
        if (len < 0) return null;
        return r.ReadBytes(len);
    }

    public void SaveState(BinaryWriter writer)
    {
        WriteByteArray(writer, wram);
        WriteByteArray(writer, vram);
        WriteByteArray(writer, vramBank1);
        WriteByteArray(writer, oam);
        WriteByteArray(writer, hram);
        WriteByteArray(writer, io);
        WriteByteArray(writer, ram);

        writer.Write(bootEnabled);
        writer.Write(mode);
        writer.Write(IsCGBMode);

        writer.Write(IE);    writer.Write(IF);
        writer.Write(JOYP);
        writer.Write(DIV);   writer.Write(TIMA); writer.Write(TMA); writer.Write(TAC);
        writer.Write(LCDC);  writer.Write(STAT);
        writer.Write(SCY);   writer.Write(SCX);
        writer.Write(LY);    writer.Write(LYC);
        writer.Write(BGP);   writer.Write(OBP0); writer.Write(OBP1);
        writer.Write(WY);    writer.Write(WX);
        writer.Write(NR50);  writer.Write(NR51); writer.Write(NR52);
        writer.Write(joypadState);

        // GBC state
        writer.Write(vramBankIndex);
        writer.Write(wramBankIndex);
        WriteByteArray(writer, cgbBgPaletteData);
        WriteByteArray(writer, cgbObjPaletteData);
        writer.Write(cgbBcps);
        writer.Write(cgbOcps);
        writer.Write(hdmaSrcHi); writer.Write(hdmaSrcLo);
        writer.Write(hdmaDstHi); writer.Write(hdmaDstLo);
        writer.Write(hdmaBlocksLeft);
        writer.Write(hdmaHBlankMode);
        writer.Write(cgbDoubleSpeed);
        writer.Write(cgbSpeedSwitchPending);

        mbc.SaveState(writer);
    }

    public void LoadState(BinaryReader reader)
    {
        byte[] lWram  = ReadByteArray(reader);
        byte[] lVram  = ReadByteArray(reader);
        byte[] lVram1 = ReadByteArray(reader);
        byte[] lOam   = ReadByteArray(reader);
        byte[] lHram  = ReadByteArray(reader);
        byte[] lIo    = ReadByteArray(reader);
        byte[] lRam   = ReadByteArray(reader);

        if (lWram  == null || lWram.Length  != wram.Length)     throw new InvalidOperationException("Invalid WRAM in savestate.");
        if (lVram  == null || lVram.Length  != vram.Length)     throw new InvalidOperationException("Invalid VRAM in savestate.");
        if (lVram1 == null || lVram1.Length != vramBank1.Length) throw new InvalidOperationException("Invalid VRAM1 in savestate.");
        if (lOam   == null || lOam.Length   != oam.Length)      throw new InvalidOperationException("Invalid OAM in savestate.");
        if (lHram  == null || lHram.Length  != hram.Length)     throw new InvalidOperationException("Invalid HRAM in savestate.");
        if (lIo    == null || lIo.Length    != io.Length)       throw new InvalidOperationException("Invalid IO in savestate.");
        if (lRam   == null || lRam.Length   != ram.Length)      throw new InvalidOperationException("Invalid RAM in savestate.");

        Array.Copy(lWram,  wram,      wram.Length);
        Array.Copy(lVram,  vram,      vram.Length);
        Array.Copy(lVram1, vramBank1, vramBank1.Length);
        Array.Copy(lOam,   oam,       oam.Length);
        Array.Copy(lHram,  hram,      hram.Length);
        Array.Copy(lIo,    io,        io.Length);
        Array.Copy(lRam,   ram,       ram.Length);

        bootEnabled = reader.ReadBoolean();
        mode        = reader.ReadBoolean();
        reader.ReadBoolean(); // IsCGBMode is always derived from ROM header; skip saved copy

        IE = reader.ReadByte(); IF = reader.ReadByte();
        JOYP = reader.ReadByte();
        DIV  = reader.ReadByte(); TIMA = reader.ReadByte(); TMA = reader.ReadByte(); TAC = reader.ReadByte();
        LCDC = reader.ReadByte(); STAT = reader.ReadByte();
        SCY  = reader.ReadByte(); SCX  = reader.ReadByte();
        LY   = reader.ReadByte(); LYC  = reader.ReadByte();
        BGP  = reader.ReadByte(); OBP0 = reader.ReadByte(); OBP1 = reader.ReadByte();
        WY   = reader.ReadByte(); WX   = reader.ReadByte();
        NR50 = reader.ReadByte(); NR51 = reader.ReadByte(); NR52 = reader.ReadByte();
        joypadState = reader.ReadByte();

        vramBankIndex = reader.ReadInt32();
        wramBankIndex = reader.ReadInt32();

        byte[] bgPal  = ReadByteArray(reader);
        byte[] objPal = ReadByteArray(reader);
        if (bgPal  != null && bgPal.Length  == 64) Array.Copy(bgPal,  cgbBgPaletteData,  64);
        if (objPal != null && objPal.Length == 64) Array.Copy(objPal, cgbObjPaletteData, 64);

        cgbBcps = reader.ReadByte();
        cgbOcps = reader.ReadByte();
        hdmaSrcHi = reader.ReadByte(); hdmaSrcLo = reader.ReadByte();
        hdmaDstHi = reader.ReadByte(); hdmaDstLo = reader.ReadByte();
        hdmaBlocksLeft        = reader.ReadInt32();
        hdmaHBlankMode        = reader.ReadBoolean();
        cgbDoubleSpeed        = reader.ReadBoolean();
        cgbSpeedSwitchPending = reader.ReadBoolean();

        mbc.LoadState(reader);
    }
}
