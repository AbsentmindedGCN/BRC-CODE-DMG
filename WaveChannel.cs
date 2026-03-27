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

    private int timer;
    private int lengthCounter;
    private int sampleIndex;
    private int sampleBuffer;
    private int currentSampleByte;
    private int visibleWaveRamByte;
    private bool justTriggered;

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
        timer = 0;
        sampleIndex = 0;
        sampleBuffer = 0;
        currentSampleByte = 0;
        visibleWaveRamByte = 0;
        justTriggered = false;
    }

    public void ResetAfterPowerOn(bool isCgbMode)
    {
        Enabled = false;
        timer = 0;
        sampleIndex = 0;
        sampleBuffer = 0;
        currentSampleByte = 0;
        visibleWaveRamByte = 0;
        justTriggered = false;

        if (isCgbMode)
            lengthCounter = 0;
    }

    public void Reset()
    {
        Enabled = false;
        timer = 0;
        lengthCounter = 0;
        sampleIndex = 0;
        sampleBuffer = 0;
        currentSampleByte = 0;
        visibleWaveRamByte = 0;
        justTriggered = false;
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

        if (isCgbMode)
        {
            waveRam[visibleWaveRamByte & 0x0F] = value;
            return;
        }

        // DMG/MGB: only readable/writeable on the exact fetch timing; ignore as a
        // conservative approximation when not modeling those 2-cycle windows.
    }

    public byte ReadWaveRam(ushort address)
    {
        int index = address - 0xFF30;
        if ((uint)index >= 16)
            return 0xFF;

        if (!Enabled)
            return waveRam[index];

        if (isCgbMode)
            return waveRam[visibleWaveRamByte & 0x0F];

        return 0xFF;
    }

    public void StepTimer(int tCycles)
    {
        if (!Enabled)
            return;

        timer -= tCycles;
        while (timer <= 0)
        {
            timer += (2048 - GetFrequency()) * 2;

            if (justTriggered)
            {
                // CH3 keeps outputting the previous buffered sample until the next fetch.
                // The first fetched nibble after trigger is sample #1 (low nibble of byte 0).
                justTriggered = false;
                sampleIndex = 1;
            }
            else
            {
                sampleIndex = (sampleIndex + 1) & 31;
            }

            visibleWaveRamByte = (sampleIndex >> 1) & 0x0F;
            currentSampleByte = waveRam[visibleWaveRamByte];
            sampleBuffer = ((sampleIndex & 1) == 0) ? ((currentSampleByte >> 4) & 0x0F) : (currentSampleByte & 0x0F);
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
        if (!isCgbMode && Enabled)
        {
            int activeByte = (visibleWaveRamByte & 0x0F);
            if (activeByte < 4)
            {
                waveRam[0] = waveRam[activeByte];
            }
            else
            {
                int baseByte = activeByte & ~0x03;
                for (int i = 0; i < 4; i++)
                    waveRam[i] = waveRam[baseByte + i];
            }
        }

        LengthWasZeroOnTrigger = (lengthCounter == 0);

        if (lengthCounter == 0)
            lengthCounter = 256;

        timer = (2048 - GetFrequency()) * 2;
        sampleIndex = 0;
        visibleWaveRamByte = 0;
        justTriggered = true;

        // Do not refresh sampleBuffer/currentSampleByte here; CH3 keeps outputting
        // the previously latched sample until the next real fetch.
        Enabled = DacEnabled;
    }

    private int GetFrequency()
    {
        return ((NR34 & 0x07) << 8) | NR33;
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(Enabled);
        writer.Write(NR30);
        writer.Write(NR31);
        writer.Write(NR32);
        writer.Write(NR33);
        writer.Write(NR34);
        writer.Write(timer);
        writer.Write(lengthCounter);
        writer.Write(sampleIndex);
        writer.Write(sampleBuffer);
        writer.Write(currentSampleByte);
        writer.Write(visibleWaveRamByte);
        writer.Write(justTriggered);
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
        timer = reader.ReadInt32();
        lengthCounter = reader.ReadInt32();
        sampleIndex = reader.ReadInt32();
        sampleBuffer = reader.ReadInt32();
        currentSampleByte = reader.ReadInt32();
        visibleWaveRamByte = reader.ReadInt32();
        justTriggered = reader.ReadBoolean();

        int len = reader.ReadInt32();
        byte[] data = reader.ReadBytes(len);
        for (int i = 0; i < waveRam.Length && i < data.Length; i++)
            waveRam[i] = data[i];
    }
}
