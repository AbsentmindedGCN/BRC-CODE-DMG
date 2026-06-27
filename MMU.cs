using System;
using System.IO;
using UnityEngine;

public class MMU
{
    // -------------------------------------------------------------------------
    // Memory arrays
    // -------------------------------------------------------------------------
    private byte[] rom;
    private byte[] wram;
    private byte[] vram;
    private byte[] vramBank1;
    private byte[] oam;
    private byte[] hram;
    private byte[] io;
    private byte[] bootRom;
    private bool bootEnabled;
    private MBC mbc;

    // Sizes
    private const int BOOT_ROM_SIZE = 0x0100;
    private const int WRAM_SIZE = 0x8000;
    private const int VRAM_SIZE = 0x2000;
    private const int OAM_SIZE = 0x00A0;
    private const int HRAM_SIZE = 0x007F;
    private const int IO_SIZE = 0x0080;

    // -------------------------------------------------------------------------
    // Standard hardware registers
    // -------------------------------------------------------------------------
    public byte IE;
    public byte IF;
    public byte JOYP;
    public byte DIV;
    public byte TIMA;
    public byte TMA;
    public byte TAC;
    public byte LCDC;
    public byte STAT;
    public byte SCY;
    public byte SCX;
    public byte LY;
    public byte LYC;
    public byte BGP;
    public byte OBP0;
    public byte OBP1;
    public byte WY;
    public byte WX;
    public byte NR50;
    public byte NR51;
    public byte NR52;
    public byte joypadState = 0xFF;

    // -------------------------------------------------------------------------
    // GBC mode
    // -------------------------------------------------------------------------
    public bool IsCGBMode { get; private set; }

    public bool IsGbc => IsCGBMode;
    public bool CgbDoubleSpeed => cgbDoubleSpeed;

    public void ExecuteSpeedSwitch()
    {
        if (!IsCGBMode || !cgbSpeedSwitchPending) return;
        cgbDoubleSpeed = !cgbDoubleSpeed;
        cgbSpeedSwitchPending = false;
    }

    private int vramBankIndex;
    private int wramBankIndex;

    private readonly byte[] cgbBgPaletteData = new byte[64];
    private readonly byte[] cgbObjPaletteData = new byte[64];
    private byte cgbBcps;
    private byte cgbOcps;

    private byte hdmaSrcHi;
    private byte hdmaSrcLo;
    private byte hdmaDstHi;
    private byte hdmaDstLo;
    private int hdmaBlocksLeft;
    private bool hdmaHBlankMode;

    private bool cgbDoubleSpeed;
    private bool cgbSpeedSwitchPending;

    // -------------------------------------------------------------------------
    // Legacy flat-RAM mode
    // -------------------------------------------------------------------------
    public byte[] ram;
    public bool mode;

    // -------------------------------------------------------------------------
    // Subsystem references
    // -------------------------------------------------------------------------
    public APU Apu;
    public Timer Timer;

    // Serial link cable — modelled directly after Linkboy's advanceTimer/handleNetwork
    // Master interrupt fires after ~100000 cycles.
    // Linkboy uses 50000 (localhost), but we need headroom for Unity's frame-based
    // network processing: packets may not arrive until the next frame (~16.7ms = ~70224 cycles).
    // 100000 cycles ≈ 24ms, safely covering 1 missed Unity frame + network latency.
    private const int SerialMasterIntThreshold = 75000;   // ~1.1 GB frames — with triple-pump pattern, worst case is 1 frame latency
    // Slave checks for incoming byte every ~5000 cycles (matches Linkboy)
    private const int SerialSlaveCheckInterval = 5000;

    private bool _serialMasterActive;  // SC=0x81 detected, transfer in progress
    private int  _serialMasterCycles;  // cycles since SC=0x81 was written
    private byte _serialOutByte;       // SB captured at SC=0x81 write time

    // Peer byte slot — holds at most one unread byte from the remote peer.
    // Only one byte is consumed per exchange (matches Linkboy's one-at-a-time readGameMessage).
    private int  _linkBitCounter;
    private bool _serialPeerByteReady;
    // When true this MMU is the hidden peer emulator; serial driven by ShiftBit, not StepSerialLink.
    internal bool IsLocalLinked;
    private byte _serialPeerByte;

    private bool _serialSlaveActive;    // SC=0x80 detected
    private int  _serialSlaveInCycles;  // cycles since SC=0x80 was written

    private byte SerialScMask    => IsCGBMode ? (byte)0x83 : (byte)0x81;
    private byte SerialUnusedBits => IsCGBMode ? (byte)0x7C : (byte)0x7E;

    public MMU(byte[] gameRom, byte[] bootRomData, bool flatRamMode)
    {
        rom = gameRom;
        bootRom = bootRomData;
        wram = new byte[WRAM_SIZE];
        vram = new byte[VRAM_SIZE];
        vramBank1 = new byte[VRAM_SIZE];
        oam = new byte[OAM_SIZE];
        hram = new byte[HRAM_SIZE];
        io = new byte[IO_SIZE];
        bootEnabled = true;
        ram = new byte[65536];
        mode = flatRamMode;
        mbc = new MBC(rom);

        IsCGBMode = (rom.Length > 0x0143) &&
                    (rom[0x0143] == 0x80 || rom[0x0143] == 0xC0);

        wramBankIndex = 1;
        vramBankIndex = 0;
        hdmaBlocksLeft = -1;

        Console.WriteLine($"MMU init – CGB mode: {IsCGBMode}");
    }

    private static string GetRtcPath(string path)
    {
        return path + ".rtc";
    }

    public byte ReadVramDirect(int relAddr, int bank)
    {
        int idx = relAddr & 0x1FFF;
        if (bank == 0) return vram[idx];
        if (IsCGBMode) return vramBank1[idx];
        return 0xFF;
    }

    public Color32 GetCGBBgColor(int paletteIndex, int colorIndex)
    {
        int offset = (paletteIndex * 4 + colorIndex) * 2;
        int color15 = cgbBgPaletteData[offset] | (cgbBgPaletteData[offset + 1] << 8);
        return Color15ToColor32(color15);
    }

    public Color32 GetCGBObjColor(int paletteIndex, int colorIndex)
    {
        int offset = (paletteIndex * 4 + colorIndex) * 2;
        int color15 = cgbObjPaletteData[offset] | (cgbObjPaletteData[offset + 1] << 8);
        return Color15ToColor32(color15);
    }

    private static Color32 Color15ToColor32(int c)
    {
        int r5 = (c) & 0x1F;
        int g5 = (c >> 5) & 0x1F;
        int b5 = (c >> 10) & 0x1F;
        byte r = (byte)((r5 << 3) | (r5 >> 2));
        byte g = (byte)((g5 << 3) | (g5 >> 2));
        byte b = (byte)((b5 << 3) | (b5 >> 2));
        return new Color32(r, g, b, 255);
    }

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
    }

    private void WriteVramDirect(ushort address, byte value)
    {
        int idx = (address - 0x8000) & 0x1FFF;
        if (vramBankIndex == 0 || !IsCGBMode)
            vram[idx] = value;
        else
            vramBank1[idx] = value;
    }

    private void StartHDMA(byte value)
    {
        if (hdmaHBlankMode && hdmaBlocksLeft >= 0 && (value & 0x80) == 0)
        {
            hdmaBlocksLeft = -1;
            return;
        }

        hdmaHBlankMode = (value & 0x80) != 0;
        hdmaBlocksLeft = value & 0x7F;

        if (!hdmaHBlankMode)
        {
            ExecuteGeneralDMA(hdmaBlocksLeft + 1);
            hdmaBlocksLeft = -1;
        }
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

    public void CancelSerial()
    {
        ResetSerialState();
        io[0x02] &= (byte)(IsCGBMode ? 0x03 : 0x01);  // clear transfer-start bit 7 only (Linkboy: SC &= ~0x80)
    }

    public byte ReadSerialData() => io[0x01];

    // Accessors used by the local link_step (gbmulator: io_registers[IO_SB/IO_SC])
    internal byte SB { get => io[0x01]; set => io[0x01] = value; }
    internal byte SC => io[0x02];
    internal int SerialClockCycles => IsCGBMode && (io[0x02] & 0x02) != 0 ? 16 : 512;

    // gbmulator gb_link_shift_bit: shift in_bit into SB, count 8 bits, fire interrupt.
    // Returns the bit that was shifted OUT (old MSB of SB).
    internal int ShiftBit(int inBit)
    {
        int outBit = (io[0x01] >> 7) & 1;
        io[0x01] = (byte)((io[0x01] << 1) | (inBit & 1));
        if (++_linkBitCounter >= 8)
        {
            _linkBitCounter = 0;
            io[0x02] = (byte)(io[0x02] & ~0x80);  // clear SC bit 7
            IF |= 0x08;                              // fire serial interrupt
        }
        return outBit;
    }

    internal void LinkReset()
    {
        _linkBitCounter  = 0;
    }

    // Called by LinkCableManager when the remote peer's byte arrives over the network.
    // Mirrors Linkboy's wrByteMMU(0xFF01, gameMessage): store it without firing interrupt.
    // Only stores if the slot is empty — one byte per exchange, FIFO (oldest first),
    // matching Linkboy's readGameMessage which reads one message per handleNetwork call.
    public void DeliverRemoteSerialByte(byte value)
    {
        if (!_serialPeerByteReady)
        {
            _serialPeerByteReady = true;
            _serialPeerByte      = value;
        }
        // If slot occupied, the byte is discarded — PumpSerialPackets will call again
        // next time, keeping the oldest unread byte (correct FIFO ordering).
    }

    private void ResetSerialState()
    {
        _serialMasterActive  = false;
        _serialMasterCycles  = 0;
        _serialOutByte       = 0xFF;
        _serialPeerByteReady = false;
        _serialPeerByte      = 0xFF;
        _serialSlaveActive   = false;
        _serialSlaveInCycles = 0;
    }

    private void BeginSerialTransfer(byte controlValue)
    {
        io[0x02] = (byte)(controlValue & SerialScMask);  // store only valid bits (Linkboy: data & 0x81)

        if ((controlValue & 0x80) == 0)
        {
            _serialMasterActive = false;
            _serialSlaveActive  = false;
            return;
        }

        if ((controlValue & 0x01) != 0)
        {
            // Internal clock (master). Mirrors Linkboy: detect SC=0x81, reset counter.
            // If already active, ignore re-write.
            if (_serialMasterActive)
                return;

            _serialMasterActive  = true;
            _serialMasterCycles  = 0;
            _serialOutByte       = io[0x01];
            _serialPeerByteReady = false;
            _serialPeerByte      = 0xFF;
            _serialSlaveActive   = false;

            // byte will be exchanged locally via LinkStep each 512 cycles
        }
        else
        {
            // External clock (slave). Mirrors Linkboy: detect SC=0x80, reset counter.
            if (_serialSlaveActive)
                return;

            _serialSlaveActive   = true;
            _serialSlaveInCycles = 0;
            _serialMasterActive  = false;
        }
    }

    // Core serial tick — mirrors Linkboy's advanceTimer serial section exactly.
    // Called every CPU instruction with the elapsed cycle count.
    public void StepSerialLink(int cycles)
    {
        if (IsLocalLinked) return;  // serial driven by parent emulator's LocalLinkStep
        // ── Master path ──────────────────────────────────────────────────────
        if (_serialMasterActive)
        {
            _serialMasterCycles += cycles;

            // Complete as soon as the slave response arrives — no need to wait for the
            // full timeout when we already have the byte. This fires in ~100-200 cycles
            // after PumpSerialBytes delivers the response, rather than waiting 140448 cycles.
            if (_serialPeerByteReady)
            {
                io[0x01] = _serialPeerByte;
                io[0x02] &= (byte)(IsCGBMode ? 0x03 : 0x01);
                ResetSerialState();
                IF |= 0x08;
            }
            else if (_serialMasterCycles >= SerialMasterIntThreshold)
            {
                // Timed out — no slave response. Write 0xFF (hardware: line pulled high).
                io[0x01] = 0xFF;
                io[0x02] &= (byte)(IsCGBMode ? 0x03 : 0x01);
                ResetSerialState();
                IF |= 0x08;
            }
        }

        // ── Slave path ───────────────────────────────────────────────────────
        if (_serialSlaveActive)
        {
            _serialSlaveInCycles += cycles;

            if (_serialPeerByteReady)
            {
                // Peer byte delivered by PumpSerialBytes (called once per frame).
                byte received = _serialPeerByte;
                io[0x01] = received;
                io[0x02] &= (byte)(IsCGBMode ? 0x03 : 0x01);
                ResetSerialState();
                IF |= 0x08;
            }
            else if (_serialSlaveInCycles >= SerialSlaveCheckInterval)
            {
                _serialSlaveInCycles = 0;
            }
        }
    }

    public void TickSerialLinkWait(float deltaSeconds) { }

    // =========================================================================
    // MBC / Save
    // =========================================================================
    public void Save(string path)
    {
        if (mbc.mbcType != 0)
        {
            Console.WriteLine("Writing save to: " + path);
            mbc.SetRtcSavePath(GetRtcPath(path));
            mbc.SaveBatteryData(path);
        }
    }

    public void Load(string path)
    {
        if (mbc.mbcType != 0)
        {
            mbc.SetRtcSavePath(GetRtcPath(path));
            mbc.LoadBatteryData(path);

            if (!File.Exists(path))
                Console.WriteLine("Save not found at: " + path);
            else
                Console.WriteLine("Loading save: " + path);
        }
    }

    public string HeaderInfo() =>
        mbc.GetTitle() + "\n" +
        mbc.GetCartridgeType() + "\n" +
        mbc.GetRomSize() + "\n" +
        mbc.GetRamSize() + "\n" +
        mbc.GetChecksum();

    public void InitializeCGBRegisters()
    {
        if (!IsCGBMode) return;

        LCDC = 0x91; STAT = 0x85;
        SCY = 0; SCX = 0; LY = 0; LYC = 0;
        BGP = 0xFC; OBP0 = 0xFF; OBP1 = 0xFF;
        WY = 0; WX = 0;
        NR52 = 0xF1; NR50 = 0x77; NR51 = 0xF3;
        IF = 0xE1; IE = 0x00;

        ushort[] grad = { 0x7FFF, 0x56B5, 0x294A, 0x0000 };
        for (int p = 0; p < 8; p++)
        {
            for (int c = 0; c < 4; c++)
            {
                int offset = (p * 4 + c) * 2;
                ushort col = (p == 0) ? grad[c] : (ushort)0x0000;
                cgbBgPaletteData[offset] = (byte)(col & 0xFF);
                cgbBgPaletteData[offset + 1] = (byte)(col >> 8);
                cgbObjPaletteData[offset] = (byte)(col & 0xFF);
                cgbObjPaletteData[offset + 1] = (byte)(col >> 8);
            }
        }
    }

    public byte Read(ushort address)
    {
        return mode ? Read2(address) : Read1(address);
    }

    public void Write(ushort address, byte value)
    {
        if (mode) Write2(address, value);
        else Write1(address, value);
    }

    public byte Read2(ushort address) => ram[address];
    public void Write2(ushort address, byte value) { ram[address] = value; }

    public byte Read1(ushort address)
    {
        if (bootEnabled && address < BOOT_ROM_SIZE)
            return bootRom[address];

        if (address < 0x8000 || (address >= 0xA000 && address < 0xC000))
            return mbc.Read(address);

        if (address >= 0x8000 && address < 0xA000)
        {
            int idx = address - 0x8000;
            return (vramBankIndex == 0 || !IsCGBMode) ? vram[idx] : vramBank1[idx];
        }

        if (address >= 0xC000 && address < 0xD000)
            return wram[address - 0xC000];

        if (address >= 0xD000 && address < 0xE000)
            return wram[(wramBankIndex << 12) + (address - 0xD000)];

        if (address >= 0xE000 && address < 0xFE00)
        {
            ushort m = (ushort)(address - 0x2000);
            if (m < 0xD000) return wram[m - 0xC000];
            return wram[(wramBankIndex << 12) + (m - 0xD000)];
        }

        if (address >= 0xFE00 && address < 0xFEA0)
            return oam[address - 0xFE00];

        if (address >= 0xFF80 && address < 0xFFFF)
            return hram[address - 0xFF80];

        if (address >= 0xFF30 && address <= 0xFF3F)
            return Apu != null ? Apu.ReadRegister(address) : io[address - 0xFF00];

        switch (address)
        {
            case 0xFF00:
                if ((JOYP & 0x10) == 0) return (byte)((joypadState >> 4) | 0x20);
                if ((JOYP & 0x20) == 0) return (byte)((joypadState & 0x0F) | 0x10);
                return (byte)(JOYP | 0xFF);

            case 0xFF01: return io[0x01];
            case 0xFF02: return (byte)(io[0x02] | SerialUnusedBits);
            case 0xFF04: return DIV;
            case 0xFF05: return TIMA;
            case 0xFF06: return TMA;
            case 0xFF07: return TAC;
            case 0xFF0F: return IF;

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

            case 0xFF15:
            case 0xFF1F:
            case 0xFF27:
            case 0xFF28:
            case 0xFF29:
            case 0xFF2A:
            case 0xFF2B:
            case 0xFF2C:
            case 0xFF2D:
            case 0xFF2E:
            case 0xFF2F:
                return 0xFF;

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

            case 0xFF4F: return (byte)(vramBankIndex | 0xFE);

            case 0xFF4D:
                if (!IsCGBMode) return 0xFF;
                return (byte)(
                    (cgbDoubleSpeed ? 0x80 : 0x00) |
                    (cgbSpeedSwitchPending ? 0x01 : 0x00) |
                    0x7E);

            case 0xFF51: return 0xFF;
            case 0xFF52: return 0xFF;
            case 0xFF53: return 0xFF;
            case 0xFF54: return 0xFF;
            case 0xFF55:
                if (!IsCGBMode) return 0xFF;
                if (hdmaBlocksLeft < 0) return 0xFF;
                return (byte)((hdmaHBlankMode ? 0x00 : 0x80) | (hdmaBlocksLeft & 0x7F));

            case 0xFF68: return IsCGBMode ? cgbBcps : (byte)0xFF;
            case 0xFF69: return IsCGBMode ? cgbBgPaletteData[cgbBcps & 0x3F] : (byte)0xFF;

            case 0xFF6A: return IsCGBMode ? cgbOcps : (byte)0xFF;
            case 0xFF6B: return IsCGBMode ? cgbObjPaletteData[cgbOcps & 0x3F] : (byte)0xFF;

            case 0xFF70: return IsCGBMode ? (byte)(wramBankIndex | 0xF8) : (byte)0xFF;

            case 0xFFFF: return IE;

            default:
                if (address >= 0xFF00 && address < 0xFF80)
                    return io[address - 0xFF00];
                return 0xFF;
        }
    }

    public void Write1(ushort address, byte value)
    {
        if (address == 0xFF50) { bootEnabled = false; return; }

        if (address < 0x8000 || (address >= 0xA000 && address < 0xC000))
        {
            mbc.Write(address, value);
            return;
        }

        if (address >= 0x8000 && address < 0xA000)
        {
            int idx = address - 0x8000;
            if (vramBankIndex == 0 || !IsCGBMode)
                vram[idx] = value;
            else
                vramBank1[idx] = value;
            return;
        }

        if (address >= 0xC000 && address < 0xD000)
        {
            wram[address - 0xC000] = value;
            return;
        }

        if (address >= 0xD000 && address < 0xE000)
        {
            wram[(wramBankIndex << 12) + (address - 0xD000)] = value;
            return;
        }

        if (address >= 0xE000 && address < 0xFE00)
        {
            ushort m = (ushort)(address - 0x2000);
            if (m < 0xD000) wram[m - 0xC000] = value;
            else wram[(wramBankIndex << 12) + (m - 0xD000)] = value;
            return;
        }

        if (address >= 0xFE00 && address < 0xFEA0)
        {
            oam[address - 0xFE00] = value;
            return;
        }

        if (address >= 0xFF80 && address < 0xFFFF)
        {
            hram[address - 0xFF80] = value;
            return;
        }

        if (address >= 0xFF30 && address <= 0xFF3F)
        {
            // APU owns Wave RAM behavior, including power-off and active-access quirks.
            Apu?.WriteRegister(address, value);
            return;
        }

        switch (address)
        {
            case 0xFF00: JOYP = (byte)(value & 0x30); return;

            case 0xFF01:
                io[0x01] = value;
                return;

            case 0xFF02:
                BeginSerialTransfer(value);
                return;

            case 0xFF04: Timer?.ResetDiv(); if (Timer == null) DIV = 0; return;
            case 0xFF05: TIMA = value; return;
            case 0xFF06: TMA = value; return;
            case 0xFF07: TAC = (byte)(value & 0x07); return;

            case 0xFF0F: IF = value; return;

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
                // Let APU own write acceptance/rejection so powered-off CGB semantics match hardware.
                Apu?.WriteRegister(address, value);
                return;

            case 0xFF15:
            case 0xFF1F:
            case 0xFF27:
            case 0xFF28:
            case 0xFF29:
            case 0xFF2A:
            case 0xFF2B:
            case 0xFF2C:
            case 0xFF2D:
            case 0xFF2E:
            case 0xFF2F:
                return;

            case 0xFF40:
                LCDC = value;
                if ((value & 0x80) == 0) { STAT &= 0x7C; LY = 0; }
                return;
            case 0xFF41: STAT = value; return;
            case 0xFF42: SCY = value; return;
            case 0xFF43: SCX = value; return;
            case 0xFF44: LY = value; return;
            case 0xFF45: LYC = value; return;

            case 0xFF46:
                {
                    ushort src = (ushort)(value << 8);
                    for (ushort i = 0; i < 0xA0; i++)
                        Write((ushort)(0xFE00 + i), Read((ushort)(src + i)));
                    return;
                }

            case 0xFF47: BGP = value; return;
            case 0xFF48: OBP0 = value; return;
            case 0xFF49: OBP1 = value; return;
            case 0xFF4A: WY = value; return;
            case 0xFF4B: WX = value; return;

            case 0xFF4F:
                if (IsCGBMode) vramBankIndex = value & 0x01;
                return;

            case 0xFF4D:
                if (IsCGBMode) cgbSpeedSwitchPending = (value & 0x01) != 0;
                return;

            case 0xFF51: hdmaSrcHi = value; return;
            case 0xFF52: hdmaSrcLo = (byte)(value & 0xF0); return;
            case 0xFF53: hdmaDstHi = (byte)(value & 0x1F); return;
            case 0xFF54: hdmaDstLo = (byte)(value & 0xF0); return;
            case 0xFF55: if (IsCGBMode) StartHDMA(value); return;

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

            case 0xFF70:
                if (IsCGBMode)
                {
                    wramBankIndex = value & 0x07;
                    if (wramBankIndex == 0) wramBankIndex = 1;
                }
                return;

            case 0xFFFF: IE = value; return;

            default:
                if (address >= 0xFF00 && address < 0xFF80)
                    io[address - 0xFF00] = value;
                return;
        }
    }

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

        writer.Write(IE); writer.Write(IF);
        writer.Write(JOYP);
        writer.Write(DIV); writer.Write(TIMA); writer.Write(TMA); writer.Write(TAC);
        writer.Write(LCDC); writer.Write(STAT);
        writer.Write(SCY); writer.Write(SCX);
        writer.Write(LY); writer.Write(LYC);
        writer.Write(BGP); writer.Write(OBP0); writer.Write(OBP1);
        writer.Write(WY); writer.Write(WX);
        writer.Write(NR50); writer.Write(NR51); writer.Write(NR52);
        writer.Write(joypadState);

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
        byte[] lWram = ReadByteArray(reader);
        byte[] lVram = ReadByteArray(reader);
        byte[] lVram1 = ReadByteArray(reader);
        byte[] lOam = ReadByteArray(reader);
        byte[] lHram = ReadByteArray(reader);
        byte[] lIo = ReadByteArray(reader);
        byte[] lRam = ReadByteArray(reader);

        if (lWram == null || lWram.Length != wram.Length) throw new InvalidOperationException("Invalid WRAM in savestate.");
        if (lVram == null || lVram.Length != vram.Length) throw new InvalidOperationException("Invalid VRAM in savestate.");
        if (lVram1 == null || lVram1.Length != vramBank1.Length) throw new InvalidOperationException("Invalid VRAM1 in savestate.");
        if (lOam == null || lOam.Length != oam.Length) throw new InvalidOperationException("Invalid OAM in savestate.");
        if (lHram == null || lHram.Length != hram.Length) throw new InvalidOperationException("Invalid HRAM in savestate.");
        if (lIo == null || lIo.Length != io.Length) throw new InvalidOperationException("Invalid IO in savestate.");
        if (lRam == null || lRam.Length != ram.Length) throw new InvalidOperationException("Invalid RAM in savestate.");

        Array.Copy(lWram, wram, wram.Length);
        Array.Copy(lVram, vram, vram.Length);
        Array.Copy(lVram1, vramBank1, vramBank1.Length);
        Array.Copy(lOam, oam, oam.Length);
        Array.Copy(lHram, hram, hram.Length);
        Array.Copy(lIo, io, io.Length);
        Array.Copy(lRam, ram, ram.Length);

        bootEnabled = reader.ReadBoolean();
        mode = reader.ReadBoolean();
        reader.ReadBoolean();

        IE = reader.ReadByte(); IF = reader.ReadByte();
        JOYP = reader.ReadByte();
        DIV = reader.ReadByte(); TIMA = reader.ReadByte(); TMA = reader.ReadByte(); TAC = reader.ReadByte();
        LCDC = reader.ReadByte(); STAT = reader.ReadByte();
        SCY = reader.ReadByte(); SCX = reader.ReadByte();
        LY = reader.ReadByte(); LYC = reader.ReadByte();
        BGP = reader.ReadByte(); OBP0 = reader.ReadByte(); OBP1 = reader.ReadByte();
        WY = reader.ReadByte(); WX = reader.ReadByte();
        NR50 = reader.ReadByte(); NR51 = reader.ReadByte(); NR52 = reader.ReadByte();
        joypadState = reader.ReadByte();

        vramBankIndex = reader.ReadInt32();
        wramBankIndex = reader.ReadInt32();

        byte[] bgPal = ReadByteArray(reader);
        byte[] objPal = ReadByteArray(reader);
        if (bgPal != null && bgPal.Length == 64) Array.Copy(bgPal, cgbBgPaletteData, 64);
        if (objPal != null && objPal.Length == 64) Array.Copy(objPal, cgbObjPaletteData, 64);

        cgbBcps = reader.ReadByte();
        cgbOcps = reader.ReadByte();
        hdmaSrcHi = reader.ReadByte(); hdmaSrcLo = reader.ReadByte();
        hdmaDstHi = reader.ReadByte(); hdmaDstLo = reader.ReadByte();
        hdmaBlocksLeft = reader.ReadInt32();
        hdmaHBlankMode = reader.ReadBoolean();
        cgbDoubleSpeed = reader.ReadBoolean();
        cgbSpeedSwitchPending = reader.ReadBoolean();

        mbc.LoadState(reader);
    }
}