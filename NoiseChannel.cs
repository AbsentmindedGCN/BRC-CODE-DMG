using System.IO;

public sealed class NoiseChannel
{
    public bool Enabled { get; private set; }

    public byte NR41 { get; private set; }
    public byte NR42 { get; private set; }
    public byte NR43 { get; private set; }
    public byte NR44 { get; private set; }

    private int timer;
    private int lengthCounter;
    private int volume;
    private int envelopeTimer;
    private ushort lfsr;

    public bool DacEnabled => (NR42 & 0xF8) != 0;

    public void PowerOff()
    {
        Enabled = false;
        NR41 = NR42 = NR43 = NR44 = 0;
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
        if (!DacEnabled)
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

        int period = GetNoisePeriod();
        if (period == int.MaxValue)
            return;

        timer -= tCycles;

        while (timer <= 0)
        {
            timer += period;

            int xorBit = (lfsr & 1) ^ ((lfsr >> 1) & 1);
            lfsr >>= 1;
            lfsr |= (ushort)(xorBit << 14);

            if ((NR43 & 0x08) != 0)
            {
                lfsr &= 0xFFBF;
                lfsr |= (ushort)(xorBit << 6);
            }
        }
    }

    public void ClockLength()
    {
        if (!Enabled || (NR44 & 0x40) == 0)
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
            if (volume < 15) volume++;
        }
        else
        {
            if (volume > 0) volume--;
        }
    }

    public int GetDigitalOutput()
    {
        if (!Enabled || !DacEnabled)
            return 0;

        // Hardware output is 0 or current envelope volume
        return (((~lfsr) & 1) != 0) ? volume : 0;
    }

    private void Trigger()
    {
        if (!DacEnabled)
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

        // Pan Docs: shift 14 or 15 => no clocks
        if (shift >= 14)
            return int.MaxValue;

        int divisor = divisorCode == 0 ? 8 : divisorCode * 16;
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