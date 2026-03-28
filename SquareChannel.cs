using System.IO;

public sealed class SquareChannel
{
    private static readonly byte[][] DutyTable =
    {
        new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }, // 12.5%
        new byte[] { 1, 0, 0, 0, 0, 0, 0, 1 }, // 25%
        new byte[] { 1, 0, 0, 0, 0, 1, 1, 1 }, // 50%
        new byte[] { 0, 1, 1, 1, 1, 1, 1, 0 }, // 75%
    };

    private readonly bool hasSweep;

    public bool Enabled { get; private set; }
    public byte NR10 { get; private set; }
    public byte NR11 { get; private set; }
    public byte NR12 { get; private set; }
    public byte NR13 { get; private set; }
    public byte NR14 { get; private set; }

    private int timer;
    private int dutyStep;
    private int lengthCounter;
    private int volume;
    private int envelopeTimer;
    private bool envelopeEnabled;

    private int sweepTimer;
    private int shadowFrequency;
    private bool sweepEnabled;
    private bool sweepNegateUsed;

    public bool LengthWasZeroOnTrigger { get; private set; }
    public int LengthCounter => lengthCounter;
    public bool DacEnabled => (NR12 & 0xF8) != 0;

    public SquareChannel(bool hasSweep)
    {
        this.hasSweep = hasSweep;
        PowerOff();
    }

    public void PowerOff()
    {
        Enabled = false;
        NR10 = NR11 = NR12 = NR13 = NR14 = 0;
        timer = 0;
        dutyStep = 0;
        volume = 0;
        envelopeTimer = 0;
        envelopeEnabled = false;
        sweepTimer = 0;
        shadowFrequency = 0;
        sweepEnabled = false;
        sweepNegateUsed = false;
    }

    public void ResetAfterPowerOn(bool isCgbMode)
    {
        Enabled = false;
        timer = 0;
        dutyStep = 0;
        volume = 0;
        envelopeTimer = 0;
        envelopeEnabled = false;
        sweepTimer = 0;
        shadowFrequency = 0;
        sweepEnabled = false;
        sweepNegateUsed = false;

        if (isCgbMode)
            lengthCounter = 0;
    }

    public void ResetDutyStep()
    {
        dutyStep = 0;
    }

    public void WriteNR10(byte value)
    {
        if (!hasSweep)
            return;

        bool oldNegate = (NR10 & 0x08) != 0;
        bool newNegate = (value & 0x08) != 0;
        if (oldNegate && !newNegate && sweepNegateUsed)
            Enabled = false;

        NR10 = value;
    }

    public void WriteNR11(byte value)
    {
        NR11 = value;
        lengthCounter = 64 - (value & 0x3F);
    }

    public void WriteLengthOnly(byte value)
    {
        lengthCounter = 64 - (value & 0x3F);
    }

    public void WriteNR12(byte value)
    {
        byte old = NR12;
        NR12 = value;

        if (!DacEnabled)
            Enabled = false;

        // zombie mode
        if (Enabled)
            ApplyZombieEnvelopeWrite(old, value);
    }

    public void WriteNR13(byte value)
    {
        NR13 = value;
    }

    public void WriteNR14(byte value)
    {
        NR14 = value;
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
            timer += GetPeriod();
            dutyStep = (dutyStep + 1) & 7;
        }
    }

    public void ClockLength()
    {
        if ((NR14 & 0x40) == 0)
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

        bool increase = (NR12 & 0x08) != 0;
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

    public void ClockSweep()
    {
        if (!hasSweep || !Enabled)
            return;

        sweepTimer--;
        if (sweepTimer > 0)
            return;

        sweepTimer = GetSweepPeriod();

        if (!sweepEnabled)
            return;

        int pace = (NR10 >> 4) & 0x07;
        if (pace == 0)
            return;

        int newFreq = CalculateSweepFrequency();
        if (newFreq > 2047)
        {
            Enabled = false;
            return;
        }

        int shift = NR10 & 0x07;
        if (shift != 0)
        {
            shadowFrequency = newFreq;
            NR13 = (byte)(newFreq & 0xFF);
            NR14 = (byte)((NR14 & 0xF8) | ((newFreq >> 8) & 0x07));

            if (CalculateSweepFrequency() > 2047)
                Enabled = false;
        }
    }

    private int CalculateSweepFrequency()
    {
        int delta = shadowFrequency >> (NR10 & 0x07);
        bool negate = (NR10 & 0x08) != 0;

        if (negate)
            sweepNegateUsed = true;

        return negate ? (shadowFrequency - delta) : (shadowFrequency + delta);
    }

    public int GetDigitalOutput()
    {
        if (!Enabled || !DacEnabled)
            return 0;

        int duty = (NR11 >> 6) & 0x03;
        int bit = DutyTable[duty][dutyStep];
        return bit != 0 ? volume : 0;
    }

    private void Trigger()
    {
        LengthWasZeroOnTrigger = lengthCounter == 0;
        if (lengthCounter == 0)
            lengthCounter = 64;

        volume = (NR12 >> 4) & 0x0F;
        envelopeTimer = GetEnvelopePeriod();
        envelopeEnabled = true;

        // Hardware preserves the low 2 bits of the pulse timer on trigger
        timer = GetPeriod() | (timer & 0x03);
        Enabled = DacEnabled;

        if (!hasSweep)
            return;

        shadowFrequency = GetFrequency();
        sweepTimer = GetSweepPeriod();
        sweepEnabled = (((NR10 >> 4) & 0x07) != 0) || ((NR10 & 0x07) != 0);
        sweepNegateUsed = false;

        if ((NR10 & 0x07) != 0 && CalculateSweepFrequency() > 2047)
            Enabled = false;
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

    private int GetFrequency()
    {
        return ((NR14 & 0x07) << 8) | NR13;
    }

    private int GetPeriod()
    {
        return (2048 - GetFrequency()) * 4;
    }

    private int GetEnvelopePeriod()
    {
        int period = NR12 & 0x07;
        return period == 0 ? 8 : period;
    }

    private int GetSweepPeriod()
    {
        int period = (NR10 >> 4) & 0x07;
        return period == 0 ? 8 : period;
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(Enabled);
        writer.Write(NR10);
        writer.Write(NR11);
        writer.Write(NR12);
        writer.Write(NR13);
        writer.Write(NR14);
        writer.Write(timer);
        writer.Write(dutyStep);
        writer.Write(lengthCounter);
        writer.Write(volume);
        writer.Write(envelopeTimer);
        writer.Write(envelopeEnabled);
        writer.Write(sweepTimer);
        writer.Write(shadowFrequency);
        writer.Write(sweepEnabled);
        writer.Write(sweepNegateUsed);
    }

    public void LoadState(BinaryReader reader)
    {
        Enabled = reader.ReadBoolean();
        NR10 = reader.ReadByte();
        NR11 = reader.ReadByte();
        NR12 = reader.ReadByte();
        NR13 = reader.ReadByte();
        NR14 = reader.ReadByte();
        timer = reader.ReadInt32();
        dutyStep = reader.ReadInt32();
        lengthCounter = reader.ReadInt32();
        volume = reader.ReadInt32();
        envelopeTimer = reader.ReadInt32();
        envelopeEnabled = reader.ReadBoolean();
        sweepTimer = reader.ReadInt32();
        shadowFrequency = reader.ReadInt32();
        sweepEnabled = reader.ReadBoolean();
        sweepNegateUsed = reader.ReadBoolean();
    }
}
