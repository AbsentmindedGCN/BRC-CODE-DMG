using BRCCodeDmg;
using System;
using System.IO;

class MBC
{
    private byte[] rom;
    public byte[] ramBanks;

    private int romBank = 1;
    private int ramBank = 0;
    private bool ramEnabled = false;

    public int mbcType;
    int romSize;
    int ramSize;
    int romBankCount;
    int ramBankCount;

    // RTC support
    private byte cartType;
    private bool hasRtc;
    private Mbc3Rtc rtc;
    private string rtcSavePath;

    public MBC(byte[] romData)
    {
        rom = romData;
        cartType = romData[0x0147];
        romSize = CalculateRomSize(romData[0x0148]);

        // Use actual file length for bank counting to avoid OOB reads.
        romBankCount = Math.Max(2, rom.Length / (16 * 1024));

        switch (cartType)
        {
            case 0x00:
                mbcType = 0;
                break;

            case 0x01:
            case 0x02:
            case 0x03:
                mbcType = 1;
                break;

            case 0x0F:
            case 0x10:
            case 0x11:
            case 0x12:
            case 0x13:
                mbcType = 3;
                break;

            case 0x19:
            case 0x1A:
            case 0x1B:
            case 0x1C:
            case 0x1D:
            case 0x1E:
                mbcType = 5;
                break;

            default:
                mbcType = 0;
                Console.WriteLine("Error: Unknown/Unsupported MBC, using MBC0/ROM Only");
                break;
        }

        hasRtc = (cartType == 0x0F || cartType == 0x10);
        if (hasRtc)
            rtc = new Mbc3Rtc();

        switch (rom[0x0149])
        {
            case 0x01:
                ramSize = 2 * 1024;
                ramBankCount = 1;
                break;
            case 0x02:
                ramSize = 8 * 1024;
                ramBankCount = 1;
                break;
            case 0x03:
                ramSize = 32 * 1024;
                ramBankCount = 4;
                break;
            case 0x04:
                ramSize = 128 * 1024;
                ramBankCount = 16;
                break;
            case 0x05:
                ramSize = 64 * 1024;
                ramBankCount = 8;
                break;
            default:
                ramSize = 0;
                ramBankCount = 0;
                break;
        }

        ramBanks = new byte[ramSize];
    }

    public void SetRtcSavePath(string path)
    {
        rtcSavePath = path;
    }

    private int CalculateRomSize(byte headerValue)
    {
        return 32 * 1024 * (1 << headerValue);
    }

    public string GetTitle()
    {
        byte[] titleBytes = new byte[16];
        Array.Copy(rom, 0x0134, titleBytes, 0, 16);

        string title = System.Text.Encoding.ASCII.GetString(titleBytes).TrimEnd('\0');

        if (title.Length > 15)
            title = title.Substring(0, 15);

        return "Title: " + title;
    }

    public string GetCartridgeType()
    {
        byte cartridgeTypeByte = rom[0x0147];
        string cartridgeType;

        switch (cartridgeTypeByte)
        {
            case 0x00: cartridgeType = "MBC0/ROM ONLY"; break;
            case 0x01: cartridgeType = "MBC1"; break;
            case 0x02: cartridgeType = "MBC1+RAM"; break;
            case 0x03: cartridgeType = "MBC1+RAM+BATTERY"; break;
            case 0x05: cartridgeType = "MBC2"; break;
            case 0x06: cartridgeType = "MBC2+BATTERY"; break;
            case 0x08: cartridgeType = "ROM+RAM"; break;
            case 0x09: cartridgeType = "ROM+RAM+BATTERY"; break;
            case 0x0B: cartridgeType = "MMM01"; break;
            case 0x0C: cartridgeType = "MMM01+RAM"; break;
            case 0x0D: cartridgeType = "MMM01+RAM+BATTERY"; break;
            case 0x0F: cartridgeType = "MBC3+TIMER+BATTERY"; break;
            case 0x10: cartridgeType = "MBC3+TIMER+RAM+BATTERY"; break;
            case 0x11: cartridgeType = "MBC3"; break;
            case 0x12: cartridgeType = "MBC3+RAM"; break;
            case 0x13: cartridgeType = "MBC3+RAM+BATTERY"; break;
            case 0x19: cartridgeType = "MBC5"; break;
            case 0x1A: cartridgeType = "MBC5+RAM"; break;
            case 0x1B: cartridgeType = "MBC5+RAM+BATTERY"; break;
            case 0x1C: cartridgeType = "MBC5+RUMBLE"; break;
            case 0x1D: cartridgeType = "MBC5+RUMBLE+RAM"; break;
            case 0x1E: cartridgeType = "MBC5+RUMBLE+RAM+BATTERY"; break;
            case 0x20: cartridgeType = "MBC6"; break;
            case 0x22: cartridgeType = "MBC7+SENSOR+RUMBLE+RAM+BATTERY"; break;
            case 0xFC: cartridgeType = "POCKET CAMERA"; break;
            case 0xFD: cartridgeType = "BANDAI TAMA5"; break;
            case 0xFE: cartridgeType = "HuC3"; break;
            case 0xFF: cartridgeType = "HuC1+RAM+BATTERY"; break;
            default: cartridgeType = "Unknown cartridge type"; break;
        }

        return "Cartridge Type: " + cartridgeType;
    }

    public string GetRomSize()
    {
        byte romSizeByte = rom[0x0148];
        string romSizeName;

        switch (romSizeByte)
        {
            case 0x00: romSizeName = "32 KiB (2 ROM banks, No Banking)"; break;
            case 0x01: romSizeName = "64 KiB (4 ROM banks)"; break;
            case 0x02: romSizeName = "128 KiB (8 ROM banks)"; break;
            case 0x03: romSizeName = "256 KiB (16 ROM banks)"; break;
            case 0x04: romSizeName = "512 KiB (32 ROM banks)"; break;
            case 0x05: romSizeName = "1 MiB (64 ROM banks)"; break;
            case 0x06: romSizeName = "2 MiB (128 ROM banks)"; break;
            case 0x07: romSizeName = "4 MiB (256 ROM banks)"; break;
            case 0x08: romSizeName = "8 MiB (512 ROM banks)"; break;
            default: romSizeName = "Unknown ROM size"; break;
        }

        return "ROM Size: " + romSizeName;
    }

    public string GetRamSize()
    {
        byte ramSizeByte = rom[0x0149];
        string ramSizeName;

        switch (ramSizeByte)
        {
            case 0x00: ramSizeName = "No RAM"; break;
            case 0x01: ramSizeName = "Unused (2 KB?)"; break;
            case 0x02: ramSizeName = "8 KiB (1 bank)"; break;
            case 0x03: ramSizeName = "32 KiB (4 banks of 8 KiB each)"; break;
            case 0x04: ramSizeName = "128 KiB (16 banks of 8 KiB each)"; break;
            case 0x05: ramSizeName = "64 KiB (8 banks of 8 KiB each)"; break;
            default: ramSizeName = "Unknown RAM size"; break;
        }

        return "RAM Size: " + ramSizeName;
    }

    public string GetChecksum()
    {
        return "Checksum: " + rom[0x014D].ToString("X2");
    }

    public byte Read(ushort address)
    {
        if (address < 0x4000)
        {
            return address < rom.Length ? rom[address] : (byte)0xFF;
        }
        else if (address < 0x8000)
        {
            int bankOffset = (romBank % romBankCount) * 0x4000;
            int offset = bankOffset + (address - 0x4000);
            return offset < rom.Length ? rom[offset] : (byte)0xFF;
        }
        else if (address >= 0xA000 && address < 0xC000)
        {
            if (!ramEnabled)
                return 0xFF;

            if (mbcType == 3 && hasRtc && ramBank >= 0x08 && ramBank <= 0x0C)
                return rtc.ReadSelected();

            if (ramBankCount > 0)
            {
                int ramOffset = (ramBank % ramBankCount) * 0x2000;
                int offset = ramOffset + (address - 0xA000);
                return offset < ramBanks.Length ? ramBanks[offset] : (byte)0xFF;
            }

            return 0xFF;
        }

        return 0xFF;
    }

    public void Write(ushort address, byte value)
    {
        if (address < 0x2000)
        {
            ramEnabled = (value & 0x0F) == 0x0A;
        }
        else if (address < 0x4000)
        {
            if (mbcType == 1)
            {
                romBank = value & 0x1F;
                if (romBank == 0) romBank = 1;
            }
            else if (mbcType == 3)
            {
                romBank = value & 0x7F;
                if (romBank == 0) romBank = 1;
            }
            else if (mbcType == 5)
            {
                if (address < 0x3000)
                    romBank = (romBank & 0x100) | value;
                else
                    romBank = (romBank & 0xFF) | ((value & 0x01) << 8);
            }
        }
        else if (address < 0x6000)
        {
            if (mbcType == 1)
            {
                ramBank = value & 0x03;
            }
            else if (mbcType == 3 || mbcType == 5)
            {
                ramBank = value & 0x0F;

                if (mbcType == 3 && hasRtc && ramBank >= 0x08 && ramBank <= 0x0C)
                    rtc.SelectRegister((byte)ramBank);
            }
        }
        else if (address < 0x8000)
        {
            if (mbcType == 3 && hasRtc)
                rtc.Latch(value);
        }
        else if (address >= 0xA000 && address < 0xC000)
        {
            if (!ramEnabled)
                return;

            if (mbcType == 3 && hasRtc && ramBank >= 0x08 && ramBank <= 0x0C)
            {
                rtc.WriteSelected(value);
                return;
            }

            if (ramBankCount > 0)
            {
                int ramOffset = (ramBank % ramBankCount) * 0x2000;
                int offset = ramOffset + (address - 0xA000);
                if (offset < ramBanks.Length)
                    ramBanks[offset] = value;
            }
        }
    }

    public void SaveBatteryData(string ramPath)
    {
        if (mbcType != 0 && ramBanks != null)
            File.WriteAllBytes(ramPath, ramBanks);

        if (hasRtc && rtc != null && !string.IsNullOrEmpty(rtcSavePath))
        {
            using (var fs = File.Create(rtcSavePath))
            using (var writer = new BinaryWriter(fs))
            {
                writer.Write(0x52544333); // RTC3
                rtc.Save(writer);
            }
        }
    }

    public void LoadBatteryData(string ramPath)
    {
        if (File.Exists(ramPath) && mbcType != 0)
            ramBanks = File.ReadAllBytes(ramPath);

        if (hasRtc && rtc != null && !string.IsNullOrEmpty(rtcSavePath) && File.Exists(rtcSavePath))
        {
            using (var fs = File.OpenRead(rtcSavePath))
            using (var reader = new BinaryReader(fs))
            {
                int marker = reader.ReadInt32();
                if (marker == 0x52544333)
                    rtc.Load(reader);
            }
        }
        else if (hasRtc && rtc != null)
        {
            rtc.SeedFromHostClock();
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(romBank);
        writer.Write(ramBank);
        writer.Write(ramEnabled);
        writer.Write(mbcType);
        writer.Write(romSize);
        writer.Write(ramSize);
        writer.Write(romBankCount);
        writer.Write(ramBankCount);

        if (ramBanks == null)
        {
            writer.Write(-1);
        }
        else
        {
            writer.Write(ramBanks.Length);
            writer.Write(ramBanks);
        }

        writer.Write(cartType);
        writer.Write(hasRtc);
        writer.Write(rtc != null);

        if (rtc != null)
            rtc.Save(writer);
    }

    public void LoadState(BinaryReader reader)
    {
        romBank = reader.ReadInt32();
        ramBank = reader.ReadInt32();
        ramEnabled = reader.ReadBoolean();
        mbcType = reader.ReadInt32();
        romSize = reader.ReadInt32();
        ramSize = reader.ReadInt32();
        romBankCount = reader.ReadInt32();
        ramBankCount = reader.ReadInt32();

        int ramBanksLength = reader.ReadInt32();
        if (ramBanksLength >= 0)
            ramBanks = reader.ReadBytes(ramBanksLength);
        else
            ramBanks = null;

        cartType = reader.ReadByte();
        hasRtc = reader.ReadBoolean();
        bool hasRtcInstance = reader.ReadBoolean();

        if (hasRtcInstance)
        {
            if (rtc == null)
                rtc = new Mbc3Rtc();

            rtc.Load(reader);
        }
        else
        {
            rtc = null;
        }
    }
}