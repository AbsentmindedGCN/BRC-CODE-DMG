using System.IO;

public sealed class SquareChannel
{
    private static readonly byte[][] DutyTable =
    {
        new byte[] { 0,0,0,0,0,0,0,1 }, // 12.5%
        new byte[] { 1,0,0,0,0,0,0,1 }, // 25%
        new byte[] { 1,0,0,0,0,1,1,1 }, // 50%
        new byte[] { 0,1,1,1,1,1,1,0 }  // 75%
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
    private int sweepTimer;
    private int shadowFrequency;
    private bool sweepEnabled;
    private bool sweepNegateUsed;

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
        lengthCounter = 0;
        volume = 0;
        envelopeTimer = 0;
        sweepTimer = 0;
        shadowFrequency = 0;
        sweepEnabled = false;
        sweepNegateUsed = false;
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

    public void WriteNR12(byte value)
    {
        NR12 = value;

        if (!DacEnabled)
            Enabled = false;
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
        if (!Enabled || (NR14 & 0x40) == 0)
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

        int pace = NR12 & 0x07;
        if (pace == 0)
            return;

        envelopeTimer--;
        if (envelopeTimer > 0)
            return;

        envelopeTimer = pace;

        bool increase = (NR12 & 0x08) != 0;
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

    public void ClockSweep()
    {
        if (!hasSweep || !sweepEnabled || !Enabled)
            return;

        int pace = (NR10 >> 4) & 0x07;

        sweepTimer--;
        if (sweepTimer > 0)
            return;

        sweepTimer = (pace == 0) ? 8 : pace;

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

        return negate ? shadowFrequency - delta : shadowFrequency + delta;
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
        if (!DacEnabled)
        {
            Enabled = false;
            return;
        }

        Enabled = true;

        if (lengthCounter == 0)
            lengthCounter = 64;

        volume = (NR12 >> 4) & 0x0F;

        int envPeriod = NR12 & 0x07;
        envelopeTimer = (envPeriod == 0) ? 8 : envPeriod;

        // Hardware quirk:
        // trigger resets the frequency timer, but NOT the duty-step counter,
        // and the low 2 bits of the timer are preserved.
        int low2 = timer & 0x03;
        timer = GetPeriod() | low2;

        if (hasSweep)
        {
            shadowFrequency = GetFrequency();

            int pace = (NR10 >> 4) & 0x07;
            sweepTimer = (pace == 0) ? 8 : pace;
            sweepEnabled = pace != 0 || (NR10 & 0x07) != 0;
            sweepNegateUsed = false;

            if ((NR10 & 0x07) != 0 && CalculateSweepFrequency() > 2047)
                Enabled = false;
        }
    }

    private int GetFrequency()
    {
        return ((NR14 & 0x07) << 8) | NR13;
    }

    private int GetPeriod()
    {
        return (2048 - GetFrequency()) * 4;
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
        sweepTimer = reader.ReadInt32();
        shadowFrequency = reader.ReadInt32();
        sweepEnabled = reader.ReadBoolean();
        sweepNegateUsed = reader.ReadBoolean();
    }
}