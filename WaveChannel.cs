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
    private int sampleIndex;   // 0..31, points to most recently fetched nibble
    private int sampleBuffer;  // currently latched 4-bit sample

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
        sampleBuffer = 0; // buffer cleared only when powering APU on
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
        // Output level control only changes how the buffered digital value is shifted.
        // It must not touch timer, position, or buffer.
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

            // Pan Docs behavior:
            // sample index increments, then the corresponding nibble is fetched.
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
        int sample = sampleBuffer & 0x0F;

        // Output level shifts the digital sample, not the analog output.
        switch (volumeCode)
        {
            case 0:
                return 0;            // mute
            case 1:
                return sample;       // 100%
            case 2:
                return sample >> 1;  // 50%
            case 3:
                return sample >> 2;  // 25%
            default:
                return 0;
        }
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

        // Restart timer.
        timer = (2048 - GetFrequency()) * 2;

        // Pan Docs / gbdev wiki behavior:
        // - sample index resets to 0
        // - sample buffer is NOT refilled on trigger
        // - previous buffered sample continues briefly
        // - next fetched sample is sample #1, so sample #0 is skipped until wraparound
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