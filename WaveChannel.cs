using System.IO;

public sealed class WaveChannel
{
    public bool Enabled { get; private set; }

    public byte NR30 { get; private set; }
    public byte NR31 { get; private set; }
    public byte NR32 { get; private set; }
    public byte NR33 { get; private set; }
    public byte NR34 { get; private set; }

    private readonly byte[] waveRam = new byte[16];

    private int timer;
    private int lengthCounter;
    private int sampleIndex;   // 0..31
    private int sampleBuffer;  // current 4-bit sample

    public bool DacEnabled => (NR30 & 0x80) != 0;

    public void PowerOff()
    {
        Enabled = false;
        NR30 = NR31 = NR32 = NR33 = NR34 = 0;
        timer = 0;
        lengthCounter = 0;
        sampleIndex = 0;
        sampleBuffer = 0;
        for (int i = 0; i < waveRam.Length; i++)
            waveRam[i] = 0;
    }

    public void ResetAfterPowerOn()
    {
        Enabled = false;
        timer = 0;
        lengthCounter = 0;
        sampleIndex = 0;
        sampleBuffer = 0;
    }

    public void Reset()
    {
        Enabled = false;
        timer = 0;
        lengthCounter = 0;
        sampleIndex = 0;
        sampleBuffer = 0;
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
        if ((uint)index < 16)
            waveRam[index] = value;
    }

    public byte ReadWaveRam(ushort address)
    {
        int index = address - 0xFF30;
        if ((uint)index < 16)
            return waveRam[index];
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
            sampleIndex = (sampleIndex + 1) & 31;
            sampleBuffer = ReadSampleNibble(sampleIndex);
        }
    }

    public void ClockLength()
    {
        if (!Enabled || (NR34 & 0x40) == 0)
            return;

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
        if (volumeCode == 0)
            return 0;

        int sample = sampleBuffer;
        switch (volumeCode)
        {
            case 1: break;
            case 2: sample >>= 1; break;
            case 3: sample >>= 2; break;
        }

        return sample & 0x0F;
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
            lengthCounter = 256;

        timer = (2048 - GetFrequency()) * 2;

        // Documented behavior: position resets to 0, but sample buffer is not refilled.
        sampleIndex = 0;
    }

    private int GetFrequency()
    {
        return ((NR34 & 0x07) << 8) | NR33;
    }

    private int ReadSampleNibble(int index)
    {
        byte packed = waveRam[index >> 1];
        return (index & 1) == 0 ? ((packed >> 4) & 0x0F) : (packed & 0x0F);
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

        int len = reader.ReadInt32();
        byte[] data = reader.ReadBytes(len);
        for (int i = 0; i < waveRam.Length; i++)
            waveRam[i] = i < data.Length ? data[i] : (byte)0;
    }
}