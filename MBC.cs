using System;
using System.IO;
class MBC {
    //MBC is currently only experimental
    //MBC0/ROM Only should be good
    //MBC1, MBC3, MBC5 are currently basic and experimental
    private byte[] rom; //Game ROM
    public byte[] ramBanks; //Cartridge RAM
    private int romBank = 1; //Current ROM bank
    private int ramBank = 0; //Current RAM bank
    private bool ramEnabled = false; //RAM enable flag
    public int mbcType; //MBC type
    int romSize;
    int ramSize;
    int romBankCount;
    int ramBankCount;

    public MBC(byte[] romData)
    {
        rom = romData;
        romSize = CalculateRomSize(romData[0x0148]);

        // Use the actual file length for bank counting — the header value can
        // report more banks than are physically present, causing out-of-bounds reads.
        romBankCount = Math.Max(2, rom.Length / (16 * 1024));

        switch (rom[0x0147]) {
            case 0x00:
                mbcType = 0;
                break;
            case 0x01:
                mbcType = 1;
                break;
            case 0x02:
                mbcType = 1;
                break;
            case 0x03:
                mbcType = 1;
                break;
            case 0x0F:
                mbcType = 3;
                break;
            case 0x10:
                mbcType = 3;
                break;
            case 0x11:
                mbcType = 3;
                break;
            case 0x12:
                mbcType = 3;
                break;
            case 0x13:
                mbcType = 3;
                break;
            case 0x19:
                mbcType = 5;
                break;
            case 0x1A:
                mbcType = 5;
                break;
            case 0x1B:
                mbcType = 5;
                break;
            case 0x1C:
                mbcType = 5;
                break;
            case 0x1D:
                mbcType = 5;
                break;
            case 0x1E:
                mbcType = 5;
                break;
            default:
                mbcType = 0;
                Console.WriteLine("Error: Unknown/Unsupported MBC, using MBC0/ROM Only");
                break;
        }

        switch (rom[0x0149]) {
            case 0x01:
                ramSize = 2 * 1024; //2 KB (Unoffical?)
                ramBankCount = 1;
                break;
            case 0x02:
                ramSize = 8 * 1024; //8 KB
                ramBankCount = 1;
                break;
            case 0x03:
                ramSize = 32 * 1024; //32 KB
                ramBankCount = 4;
                break;
            case 0x04:
                ramSize = 128 * 1024; //128 KB
                ramBankCount = 16;
                break;
            case 0x05:
                ramSize = 64 * 1024; //64 KB
                ramBankCount = 8;
                break;
            default:
                ramSize = 0; //No RAM
                ramBankCount = 0;
                break;
        }

        ramBanks = new byte[ramSize];
    }

    private int CalculateRomSize(byte headerValue) {
        return 32 * 1024 * (1 << headerValue); //32 KiB × (1 << <value>)
    }

    public string GetTitle() {
        byte[] titleBytes = new byte[16];
        Array.Copy(rom, 0x0134, titleBytes, 0, 16);

        string title = System.Text.Encoding.ASCII.GetString(titleBytes).TrimEnd(null);

        if (title.Length > 15)
        {
            title = title.Substring(0, 15);
        }

        return "Title: " + title;
    }

    public string GetCartridgeType() {
        byte cartridgeTypeByte = rom[0x0147];
        string cartridgeType;

        switch (cartridgeTypeByte) {
            case 0x00:
                cartridgeType = "MBC0/ROM ONLY";
                break;
            case 0x01:
                cartridgeType = "MBC1";
                break;
            case 0x02:
                cartridgeType = "MBC1+RAM";
                break;
            case 0x03:
                cartridgeType = "MBC1+RAM+BATTERY";
                break;
            case 0x05:
                cartridgeType = "MBC2";
                break;
            case 0x06:
                cartridgeType = "MBC2+BATTERY";
                break;
            case 0x08:
                cartridgeType = "ROM+RAM";
                break;
            case 0x09:
                cartridgeType = "ROM+RAM+BATTERY";
                break;
            case 0x0B:
                cartridgeType = "MMM01";
                break;
            case 0x0C:
                cartridgeType = "MMM01+RAM";
                break;
            case 0x0D:
                cartridgeType = "MMM01+RAM+BATTERY";
                break;
            case 0x0F:
                cartridgeType = "MBC3+TIMER+BATTERY";
                break;
            case 0x10:
                cartridgeType = "MBC3+TIMER+RAM+BATTERY";
                break;
            case 0x11:
                cartridgeType = "MBC3";
                break;
            case 0x12:
                cartridgeType = "MBC3+RAM";
                break;
            case 0x13:
                cartridgeType = "MBC3+RAM+BATTERY";
                break;
            case 0x19:
                cartridgeType = "MBC5";
                break;
            case 0x1A:
                cartridgeType = "MBC5+RAM";
                break;
            case 0x1B:
                cartridgeType = "MBC5+RAM+BATTERY";
                break;
            case 0x1C:
                cartridgeType = "MBC5+RUMBLE";
                break;
            case 0x1D:
                cartridgeType = "MBC5+RUMBLE+RAM";
                break;
            case 0x1E:
                cartridgeType = "MBC5+RUMBLE+RAM+BATTERY";
                break;
            case 0x20:
                cartridgeType = "MBC6";
                break;
            case 0x22:
                cartridgeType = "MBC7+SENSOR+RUMBLE+RAM+BATTERY";
                break;
            case 0xFC:
                cartridgeType = "POCKET CAMERA";
                break;
            case 0xFD:
                cartridgeType = "BANDAI TAMA5";
                break;
            case 0xFE:
                cartridgeType = "HuC3";
                break;
            case 0xFF:
                cartridgeType = "HuC1+RAM+BATTERY";
                break;
            default:
                cartridgeType = "Unknown cartridge type";
                break;
        }
        return "Cartridge Type: " + cartridgeType;
    }

    public string GetRomSize() {
        byte romSizeByte = rom[0x0148];
        string romSizeName;

        switch (romSizeByte) {
            case 0x00:
                romSizeName = "32 KiB (2 ROM banks, No Banking)";
                break;
            case 0x01:
                romSizeName = "64 KiB (4 ROM banks)";
                break;
            case 0x02:
                romSizeName = "128 KiB (8 ROM banks)";
                break;
            case 0x03:
                romSizeName = "256 KiB (16 ROM banks)";
                break;
            case 0x04:
                romSizeName = "512 KiB (32 ROM banks)";
                break;
            case 0x05:
                romSizeName = "1 MiB (64 ROM banks)";
                break;
            case 0x06:
                romSizeName = "2 MiB (128 ROM banks)";
                break;
            case 0x07:
                romSizeName = "4 MiB (256 ROM banks)";
                break;
            case 0x08:
                romSizeName = "8 MiB (512 ROM banks)";
                break;
            default:
                romSizeName = "Unknown ROM size";
                break;
        }
        return "ROM Size: " + romSizeName;
    }

    public string GetRamSize() {
        byte ramSizeByte = rom[0x0149];
        string ramSizeName;

        switch (ramSizeByte) {
            case 0x00:
                ramSizeName = "No RAM";
                break;
            case 0x01:
                ramSizeName = "Unused (2 KB?)";
                break;
            case 0x02:
                ramSizeName = "8 KiB (1 bank)";
                break;
            case 0x03:
                ramSizeName = "32 KiB (4 banks of 8 KiB each)";
                break;
            case 0x04:
                ramSizeName = "128 KiB (16 banks of 8 KiB each)";
                break;
            case 0x05:
                ramSizeName = "64 KiB (8 banks of 8 KiB each)";
                break;
            default:
                ramSizeName = "Unknown RAM size";
                break;
        }
        return "RAM Size: " + ramSizeName;
    }
    
    public string GetChecksum() {
        return "Checksum: " + rom[0x014D].ToString("X2");
    }

    public byte Read(ushort address) {
        if (address < 0x4000) {
            // Fixed ROM bank 0
            return address < rom.Length ? rom[address] : (byte)0xFF;
        } else if (address < 0x8000) {
            // Switchable ROM bank — clamp to actual file length
            int bankOffset = (romBank % romBankCount) * 0x4000;
            int offset = bankOffset + (address - 0x4000);
            return offset < rom.Length ? rom[offset] : (byte)0xFF;
        } else if (address >= 0xA000 && address < 0xC000) {
            // Cartridge RAM
            if (ramEnabled && ramBankCount > 0) {
                int ramOffset = (ramBank % ramBankCount) * 0x2000;
                int offset = ramOffset + (address - 0xA000);
                return offset < ramBanks.Length ? ramBanks[offset] : (byte)0xFF;
            }
            return 0xFF;
        }
        return 0xFF;
    }

    public void Write(ushort address, byte value) {
        if (address < 0x2000) {
            //Enable or disable RAM (common)
            ramEnabled = (value & 0x0F) == 0x0A;
        } else if (address < 0x4000) {
            //ROM Bank Switching
            if (mbcType == 1) {
                romBank = value & 0x1F;
                if (romBank == 0) romBank = 1;
            } else if (mbcType == 3) {
                romBank = value & 0x7F;
                if (romBank == 0) romBank = 1;
            } else if (mbcType == 5) {
                if (address < 0x3000) {
                    romBank = (romBank & 0x100) | value;
                } else {
                    romBank = (romBank & 0xFF) | ((value & 0x01) << 8);
                }
            }
        } else if (address < 0x6000) {
            //RAM Bank Switching
            if (mbcType == 1) {
                ramBank = value & 0x03;
            } else if (mbcType == 5 || mbcType == 3) {
                //ramBank = value & 0x03; MBC3 2 bits for no RTC
                ramBank = value & 0x0F;
            }
            //No banking mode for MBC1
        } else if (address >= 0xA000 && address < 0xC000) {
            //RAM Write (common)
            if (ramEnabled && ramBankCount > 0) {
                int ramOffset = (ramBank % ramBankCount) * 0x2000;
                int offset = ramOffset + (address - 0xA000);
                if (offset < ramBanks.Length)
                    ramBanks[offset] = value;
            }
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
        {
            ramBanks = reader.ReadBytes(ramBanksLength);
        }
        else
        {
            ramBanks = null;
        }
    }

}
