using System.IO;

public sealed class WaveChannel
{
    private readonly bool isCgbMode;

    public bool Enabled { get; private set; }
    public byte NR30 { get; private set; }
    public byte NR31 { get; private set; }
    public byte NR32 { get; private set; }
    public byte NR33 { get; private set; }
    public byte NR34 { get; private set; }

    private readonly byte[] waveRam = new byte[16];

    private int lengthCounter;
    private int currentSampleIndex;
    private int currentSampleByte;
    private int sampleBuffer;
    private int sampleCountdown;
    private bool waveFormJustRead;
    private bool pulsed;

    public bool LengthWasZeroOnTrigger { get; private set; }
    public int LengthCounter => lengthCounter;
    public bool DacEnabled => (NR30 & 0x80) != 0;

    public WaveChannel(bool isCgbMode)
    {
        this.isCgbMode = isCgbMode;
        PowerOff();
    }

    public void PowerOff()
    {
        Enabled = false;
        NR30 = NR31 = NR32 = NR33 = NR34 = 0;
        lengthCounter = 0;
        currentSampleIndex = 0;
        currentSampleByte = 0;
        sampleBuffer = 0;
        sampleCountdown = 0;
        waveFormJustRead = false;
        pulsed = false;
    }

    public void ResetAfterPowerOn(bool isCgbMode)
    {
        Enabled = false;
        currentSampleIndex = 0;
        currentSampleByte = 0;
        sampleBuffer = 0;
        sampleCountdown = 0;
        waveFormJustRead = false;
        pulsed = false;

        if (isCgbMode)
            lengthCounter = 0;
    }

    public void Reset()
    {
        Enabled = false;
        lengthCounter = 0;
        currentSampleIndex = 0;
        currentSampleByte = 0;
        sampleBuffer = 0;
        sampleCountdown = 0;
        waveFormJustRead = false;
        pulsed = false;
    }

    public void WriteNR30(byte value)
    {
        NR30 = value;
        if (!DacEnabled)
        {
            Enabled = false;
            pulsed = false;
        }
    }

    public void WriteNR31(byte value)
    {
        NR31 = value;
        lengthCounter = 256 - value;
    }

    public void WriteLengthOnly(byte value)
    {
        lengthCounter = 256 - value;
    }

    public void WriteNR32(byte value)
    {
        NR32 = value;
    }

    public void WriteNR33(byte value)
    {
        NR33 = value;
    }

    public void WriteNR34(byte value)
    {
        NR34 = value;
        if ((value & 0x80) != 0)
            Trigger();
    }

    public void WriteWaveRam(ushort address, byte value)
    {
        int index = address - 0xFF30;
        if ((uint)index >= 16)
            return;

        if (!Enabled)
        {
            waveRam[index] = value;
            return;
        }

        int activeIndex = (currentSampleIndex >> 1) & 0x0F;

        if (isCgbMode)
        {
            // On CGB, all wave RAM accesses alias the currently selected byte.
            waveRam[activeIndex] = value;
            return;
        }

        // On DMG/MGB, accesses only work in a tiny window right after the fetch.
        // This flag is a practical approximation that is good enough for blargg's
        // wave-RAM tests without requiring per-T-cycle CPU/APU interleaving.
        if (waveFormJustRead)
            waveRam[activeIndex] = value;
    }

    public byte ReadWaveRam(ushort address)
    {
        int index = address - 0xFF30;
        if ((uint)index >= 16)
            return 0xFF;

        if (!Enabled)
            return waveRam[index];

        int activeIndex = (currentSampleIndex >> 1) & 0x0F;

        if (isCgbMode)
            return waveRam[activeIndex];

        return waveFormJustRead ? waveRam[activeIndex] : (byte)0xFF;
    }

    public void StepTimer(int tCycles)
    {
        waveFormJustRead = false;

        if (!Enabled)
            return;

        int cyclesLeft = tCycles;
        while (cyclesLeft > sampleCountdown)
        {
            cyclesLeft -= sampleCountdown + 1;
            sampleCountdown = CalcTimerPeriod(ReadFrequencyFromRegs());

            currentSampleIndex = (currentSampleIndex + 1) & 0x1F;
            currentSampleByte = waveRam[(currentSampleIndex >> 1) & 0x0F];
            sampleBuffer = ((currentSampleIndex & 1) == 0)
                ? ((currentSampleByte >> 4) & 0x0F)
                : (currentSampleByte & 0x0F);

            waveFormJustRead = true;
        }

        if (cyclesLeft > 0)
        {
            sampleCountdown -= cyclesLeft;
            waveFormJustRead = false;
        }
    }

    public void ClockLength()
    {
        if ((NR34 & 0x40) == 0)
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

    public int GetDigitalOutput()
    {
        if (!Enabled || !DacEnabled)
            return 0;

        int volumeCode = (NR32 >> 5) & 0x03;
        int sample = sampleBuffer & 0x0F;

        switch (volumeCode)
        {
            case 0: return 0;
            case 1: return sample;
            case 2: return sample >> 1;
            case 3: return sample >> 2;
            default: return 0;
        }
    }

    private void Trigger()
    {
        // DMG-only retrigger corruption occurs only when retriggered exactly as the
        // hardware is about to fetch the next wave byte, not on every retrigger.
        if (!isCgbMode && Enabled && sampleCountdown == 0)
        {
            int offset = ((currentSampleIndex + 1) >> 1) & 0x0F;
            if (offset < 4)
            {
                waveRam[0] = waveRam[offset];
            }
            else
            {
                int baseByte = offset & ~0x03;
                for (int i = 0; i < 4; i++)
                    waveRam[i] = waveRam[baseByte + i];
            }
        }

        LengthWasZeroOnTrigger = (lengthCounter == 0);
        if (lengthCounter == 0)
            lengthCounter = 256;

        pulsed = true;
        currentSampleIndex = 0;

        // CH3 keeps its previously latched output until the first post-trigger fetch.
        // If trigger happened exactly on the fetch boundary while already active,
        // hardware may have just loaded byte 0.
        if (Enabled && sampleCountdown == 0)
            currentSampleByte = waveRam[0];

        Enabled = DacEnabled;
        sampleCountdown = CalcTimerPeriod(ReadFrequencyFromRegs()) + 3;
        waveFormJustRead = false;
    }

    private int ReadFrequencyFromRegs()
    {
        return ((NR34 & 0x07) << 8) | NR33;
    }

    private static int CalcTimerPeriod(int frequency)
    {
        return (2048 - frequency) * 2;
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(Enabled);
        writer.Write(NR30);
        writer.Write(NR31);
        writer.Write(NR32);
        writer.Write(NR33);
        writer.Write(NR34);
        writer.Write(lengthCounter);
        writer.Write(currentSampleIndex);
        writer.Write(currentSampleByte);
        writer.Write(sampleBuffer);
        writer.Write(sampleCountdown);
        writer.Write(waveFormJustRead);
        writer.Write(pulsed);
        writer.Write(waveRam.Length);
        writer.Write(waveRam);
    }

    public void LoadState(BinaryReader reader)
    {
        Enabled = reader.ReadBoolean();
        NR30 = reader.ReadByte();
        NR31 = reader.ReadByte();
        NR32 = reader.ReadByte();
        NR33 = reader.ReadByte();
        NR34 = reader.ReadByte();
        lengthCounter = reader.ReadInt32();
        currentSampleIndex = reader.ReadInt32();
        currentSampleByte = reader.ReadInt32();
        sampleBuffer = reader.ReadInt32();
        sampleCountdown = reader.ReadInt32();
        waveFormJustRead = reader.ReadBoolean();
        pulsed = reader.ReadBoolean();

        int len = reader.ReadInt32();
        byte[] data = reader.ReadBytes(len);
        for (int i = 0; i < waveRam.Length && i < data.Length; i++)
            waveRam[i] = data[i];
    }
}
