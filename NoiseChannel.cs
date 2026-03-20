using System.IO;

public sealed class NoiseChannel
{
    public bool Enabled { get; private set; }

    public byte NR41 { get; private set; } // length
    public byte NR42 { get; private set; } // envelope
    public byte NR43 { get; private set; } // clock / width / divisor
    public byte NR44 { get; private set; } // trigger / length enable

    private int timer;
    private int lengthCounter;
    private int volume;
    private int envelopeTimer;
    private ushort lfsr;

    public void PowerOff()
    {
        Enabled = false;
        NR41 = 0;
        NR42 = 0;
        NR43 = 0;
        NR44 = 0;

        timer = 0;
        lengthCounter = 0;
        volume = 0;
        envelopeTimer = 0;
        lfsr = 0x7FFF;
    }

    public void Reset()
    {
        Enabled = false;
        timer = 0;
        lengthCounter = 0;
        volume = 0;
        envelopeTimer = 0;
        lfsr = 0x7FFF;
    }

    public void WriteNR41(byte value)
    {
        NR41 = value;
        lengthCounter = 64 - (value & 0x3F);
    }

    public void WriteNR42(byte value)
    {
        NR42 = value;

        // DAC off disables channel
        if ((NR42 & 0xF8) == 0)
            Enabled = false;
    }

    public void WriteNR43(byte value)
    {
        NR43 = value;
    }

    public void WriteNR44(byte value)
    {
        NR44 = value;

        if ((value & 0x80) != 0)
            Trigger();
    }

    public void StepTimer(int tCycles)
    {
        if (!Enabled)
            return;

        timer -= tCycles;

        while (timer <= 0)
        {
            timer += GetNoisePeriod();

            int xorBit = ((lfsr & 0x0001) ^ ((lfsr >> 1) & 0x0001));
            lfsr >>= 1;
            lfsr |= (ushort)(xorBit << 14);

            // width mode: also copy into bit 6
            if ((NR43 & 0x08) != 0)
            {
                lfsr &= 0xFFBF;
                lfsr |= (ushort)(xorBit << 6);
            }
        }
    }

    public void ClockLength()
    {
        if (!Enabled)
            return;

        if ((NR44 & 0x40) == 0)
            return;

        if (lengthCounter > 0)
        {
            lengthCounter--;
            if (lengthCounter == 0)
                Enabled = false;
        }
    }

    public void ClockEnvelope()
    {
        if (!Enabled)
            return;

        int pace = NR42 & 0x07;
        if (pace == 0)
            return;

        envelopeTimer--;
        if (envelopeTimer > 0)
            return;

        envelopeTimer = pace;

        bool increase = (NR42 & 0x08) != 0;
        if (increase)
        {
            if (volume < 15)
                volume++;
        }
        else
        {
            if (volume > 0)
                volume--;
        }
    }

    public float GetOutput()
    {
        if (!Enabled)
            return 0f;

        if ((NR42 & 0xF8) == 0)
            return 0f;

        int bit = (~lfsr) & 1;
        float amp = volume / 15f;
        return bit != 0 ? amp : -amp;
    }

    private void Trigger()
    {
        if ((NR42 & 0xF8) == 0)
        {
            Enabled = false;
            return;
        }

        Enabled = true;

        if (lengthCounter == 0)
            lengthCounter = 64;

        volume = (NR42 >> 4) & 0x0F;

        envelopeTimer = NR42 & 0x07;
        if (envelopeTimer == 0)
            envelopeTimer = 8;

        lfsr = 0x7FFF;
        timer = GetNoisePeriod();
    }

    private int GetNoisePeriod()
    {
        int divisorCode = NR43 & 0x07;
        int shift = (NR43 >> 4) & 0x0F;

        int divisor;

        switch (divisorCode)
        {
            case 0: divisor = 8; break;
            case 1: divisor = 16; break;
            case 2: divisor = 32; break;
            case 3: divisor = 48; break;
            case 4: divisor = 64; break;
            case 5: divisor = 80; break;
            case 6: divisor = 96; break;
            case 7: divisor = 112; break;
            default: divisor = 8; break;
        }

        return divisor << shift;
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(Enabled);
        writer.Write(NR41);
        writer.Write(NR42);
        writer.Write(NR43);
        writer.Write(NR44);

        writer.Write(timer);
        writer.Write(lengthCounter);
        writer.Write(volume);
        writer.Write(envelopeTimer);
        writer.Write(lfsr);
    }

    public void LoadState(BinaryReader reader)
    {
        Enabled = reader.ReadBoolean();
        NR41 = reader.ReadByte();
        NR42 = reader.ReadByte();
        NR43 = reader.ReadByte();
        NR44 = reader.ReadByte();

        timer = reader.ReadInt32();
        lengthCounter = reader.ReadInt32();
        volume = reader.ReadInt32();
        envelopeTimer = reader.ReadInt32();
        lfsr = reader.ReadUInt16();
    }
}