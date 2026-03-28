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
    private bool envelopeEnabled;
    private ushort lfsr;

    public bool LengthWasZeroOnTrigger { get; private set; }
    public int LengthCounter => lengthCounter;
    public bool DacEnabled => (NR42 & 0xF8) != 0;

    public void PowerOff()
    {
        Enabled = false;
        NR41 = NR42 = NR43 = NR44 = 0;
        timer = 0;
        volume = 0;
        envelopeTimer = 0;
        envelopeEnabled = false;
        lfsr = 0x7FFF;
    }

    public void ResetAfterPowerOn(bool isCgbMode)
    {
        Enabled = false;
        timer = 0;
        volume = 0;
        envelopeTimer = 0;
        envelopeEnabled = false;
        lfsr = 0x7FFF;

        if (isCgbMode)
            lengthCounter = 0;
    }

    public void Reset()
    {
        Enabled = false;
        timer = 0;
        lengthCounter = 0;
        volume = 0;
        envelopeTimer = 0;
        envelopeEnabled = false;
        lfsr = 0x7FFF;
    }

    public void WriteNR41(byte value)
    {
        NR41 = value;
        lengthCounter = 64 - (value & 0x3F);
    }

    public void WriteLengthOnly(byte value)
    {
        lengthCounter = 64 - (value & 0x3F);
    }

    public void WriteNR42(byte value)
    {
        byte old = NR42;
        NR42 = value;

        if (!DacEnabled)
            Enabled = false;

        if (Enabled)
            ApplyZombieEnvelopeWrite(old, value);
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
        if ((NR44 & 0x40) == 0)
            return;

        if (lengthCounter > 0)
        {
            lengthCounter--;
            if (lengthCounter == 0)
                Enabled = false;
        }
    }

    public void ExtraLengthClock()
    {
        if (lengthCounter > 0)
        {
            lengthCounter--;
            if (lengthCounter == 0)
                Enabled = false;
        }
    }

    public void ClockEnvelope()
    {
        if (!Enabled || !envelopeEnabled)
            return;

        envelopeTimer--;
        if (envelopeTimer > 0)
            return;

        envelopeTimer = GetEnvelopePeriod();

        bool increase = (NR42 & 0x08) != 0;
        int newVolume = increase ? (volume + 1) : (volume - 1);

        if (newVolume >= 0 && newVolume <= 15)
        {
            volume = newVolume;
        }
        else
        {
            envelopeEnabled = false;
        }
    }

    public void DelayEnvelopeTimerForObscureTrigger()
    {
        if (envelopeTimer > 0)
            envelopeTimer++;
    }

    public int GetDigitalOutput()
    {
        if (!Enabled || !DacEnabled)
            return 0;

        return (((~lfsr) & 1) != 0) ? volume : 0;
    }

    private void Trigger()
    {
        LengthWasZeroOnTrigger = lengthCounter == 0;
        if (lengthCounter == 0)
            lengthCounter = 64;

        volume = (NR42 >> 4) & 0x0F;
        envelopeTimer = GetEnvelopePeriod();
        envelopeEnabled = true;
        lfsr = 0x7FFF;
        timer = GetNoisePeriod();
        Enabled = DacEnabled;
    }

    private void ApplyZombieEnvelopeWrite(byte oldValue, byte newValue)
    {
        int oldPeriod = oldValue & 0x07;
        bool oldSubtract = (oldValue & 0x08) == 0;
        bool oldAdd = !oldSubtract;
        bool newAdd = (newValue & 0x08) != 0;

        int newVolume = volume;

        if (oldPeriod == 0 && envelopeEnabled)
            newVolume++;
        else if (oldSubtract)
            newVolume += 2;

        if (oldAdd != newAdd)
            newVolume = 16 - newVolume;

        volume = newVolume & 0x0F;
    }

    private int GetNoisePeriod()
    {
        int divisorCode = NR43 & 0x07;
        int shift = (NR43 >> 4) & 0x0F;

        if (shift >= 14)
            return int.MaxValue;

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

    private int GetEnvelopePeriod()
    {
        int period = NR42 & 0x07;
        return period == 0 ? 8 : period;
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
        writer.Write(envelopeEnabled);
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
        envelopeEnabled = reader.ReadBoolean();
        lfsr = reader.ReadUInt16();
    }
}
