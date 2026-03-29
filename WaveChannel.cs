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
    private int position;
    private byte sampleBuffer;
    private int timer;

    private long apuCycle;
    private readonly long[] fetchCycles = new long[4];
    private readonly int[] fetchIndices = new int[4];
    private int fetchHistoryCount;

    private int currentWaveByteIndex;

    public bool LengthWasZeroOnTrigger { get; private set; }
    public int LengthCounter => lengthCounter;
    public bool DacEnabled => (NR30 & 0x80) != 0;
    public long CurrentApuCycle => apuCycle;

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
        position = 0;
        sampleBuffer = 0;
        timer = 0;
        apuCycle = 0;
        fetchHistoryCount = 0;
        currentWaveByteIndex = 0;
    }

    public void ResetAfterPowerOn(bool isCgbMode)
    {
        Enabled = false;
        position = 0;
        sampleBuffer = 0;
        timer = 0;
        fetchHistoryCount = 0;
        currentWaveByteIndex = 0;

        if (isCgbMode)
            lengthCounter = 0;
    }

    public void Reset()
    {
        Enabled = false;
        lengthCounter = 0;
        position = 0;
        sampleBuffer = 0;
        timer = 0;
        fetchHistoryCount = 0;
        currentWaveByteIndex = 0;
    }

    public void WriteNR30(byte value)
    {
        NR30 = value;
        if (!DacEnabled)
            Enabled = false;
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
        WriteNR34(value, apuCycle);
    }

    public void WriteNR34(byte value, long triggerWriteCycle)
    {
        NR34 = value;
        if ((value & 0x80) != 0)
            Trigger(triggerWriteCycle);
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

        if (isCgbMode)
        {
            waveRam[(position >> 1) & 0x0F] = value;
            return;
        }

        if (TryGetDmgCpuAccessibleIndex(out int activeIndex))
            waveRam[activeIndex] = value;
    }

    public byte ReadWaveRam(ushort address)
    {
        int index = address - 0xFF30;
        if ((uint)index >= 16)
            return 0xFF;

        if (!Enabled)
            return waveRam[index];

        if (isCgbMode)
            return waveRam[(position >> 1) & 0x0F];

        return TryGetDmgCpuAccessibleIndex(out int activeIndex) ? waveRam[activeIndex] : (byte)0xFF;
    }

    public void StepTimer(int tCycles)
    {
        if (tCycles <= 0)
            return;

        int remaining = tCycles;
        while (remaining > 0)
        {
            int step = remaining >= 2 ? 2 : remaining;
            remaining -= step;
            apuCycle += step;

            if (!Enabled)
                continue;

            timer -= step;
            while (timer <= 0)
            {
                timer += GetTimerPeriod();

                // CH3 advances first, then fetches the byte for the new position
                position = (position + 1) & 0x1F;
                currentWaveByteIndex = (position >> 1) & 0x0F;
                sampleBuffer = waveRam[currentWaveByteIndex];
                RecordFetch(currentWaveByteIndex, apuCycle);
            }
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

        int sample = ((position & 1) == 0)
            ? ((sampleBuffer >> 4) & 0x0F)
            : (sampleBuffer & 0x0F);

        switch ((NR32 >> 5) & 0x03)
        {
            case 0: return 0;
            case 1: return sample;
            case 2: return sample >> 1;
            case 3: return sample >> 2;
            default: return 0;
        }
    }

    private void Trigger(long triggerWriteCycle)
    {
        // DMG retrigger corruption (yes this is hw accurate)
        // corruption happens when NR34 trigger is written while the next CH3
        // sample read is exactly one APU tick (2 T-cycles) away
        if (!isCgbMode && Enabled && timer == 2)
            ApplyDmgRetriggerCorruptionFromNextPosition();

        LengthWasZeroOnTrigger = lengthCounter == 0;
        if (lengthCounter == 0)
            lengthCounter = 256;

        // trigger resets position to 0 but does not refill the sample buffer
        position = 0;
        currentWaveByteIndex = 0;

        timer = GetTimerPeriod() + 6;
        Enabled = DacEnabled;
    }

    private void RecordFetch(int fetchIndex, long cycle)
    {
        for (int i = fetchCycles.Length - 1; i > 0; i--)
        {
            fetchCycles[i] = fetchCycles[i - 1];
            fetchIndices[i] = fetchIndices[i - 1];
        }

        fetchCycles[0] = cycle;
        fetchIndices[0] = fetchIndex;
        if (fetchHistoryCount < fetchCycles.Length)
            fetchHistoryCount++;
    }

    private bool TryGetDmgCpuAccessibleIndex(out int index)
    {
        long now = apuCycle;
        for (int i = 0; i < fetchHistoryCount; i++)
        {
            long fc = fetchCycles[i];
            if (fc == now)
            {
                index = fetchIndices[i];
                return true;
            }
        }

        index = 0;
        return false;
    }

    private void ApplyDmgRetriggerCorruptionFromNextPosition()
    {
        int nextPosition = (position + 1) & 0x1F;
        int accessedIndex = (nextPosition >> 1) & 0x0F;
        byte accessedByte = waveRam[accessedIndex];

        if (nextPosition < 8)
        {
            waveRam[0] = accessedByte;
            return;
        }

        int blockBase = accessedIndex & 0x0C;
        for (int i = 0; i < 4; i++)
            waveRam[i] = waveRam[blockBase + i];
    }


    private int GetFrequency()
    {
        return ((NR34 & 0x07) << 8) | NR33;
    }

    private int GetTimerPeriod()
    {
        return (2048 - GetFrequency()) * 2;
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
        writer.Write(position);
        writer.Write(sampleBuffer);
        writer.Write(timer);
        writer.Write(apuCycle);
        writer.Write(fetchHistoryCount);
        for (int i = 0; i < fetchCycles.Length; i++)
        {
            writer.Write(fetchCycles[i]);
            writer.Write(fetchIndices[i]);
        }
        writer.Write(currentWaveByteIndex);
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
        position = reader.ReadInt32();
        sampleBuffer = reader.ReadByte();
        timer = reader.ReadInt32();
        apuCycle = reader.ReadInt64();
        fetchHistoryCount = reader.ReadInt32();
        for (int i = 0; i < fetchCycles.Length; i++)
        {
            fetchCycles[i] = reader.ReadInt64();
            fetchIndices[i] = reader.ReadInt32();
        }
        currentWaveByteIndex = reader.ReadInt32();

        int len = reader.ReadInt32();
        byte[] data = reader.ReadBytes(len);
        for (int i = 0; i < waveRam.Length && i < data.Length; i++)
            waveRam[i] = data[i];
    }
}