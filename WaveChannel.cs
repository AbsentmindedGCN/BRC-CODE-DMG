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
    private int sampleIndex;  // 0..31, most recently fetched nibble position
    private int sampleBuffer; // currently latched 4-bit sample

    // FIX: Track whether the most recent trigger reloaded the length counter from 0.
    public bool LengthWasZeroOnTrigger { get; private set; }

    public bool DacEnabled => (NR30 & 0x80) != 0;

    public void PowerOff()
    {
        Enabled = false;
        NR30 = NR31 = NR32 = NR33 = NR34 = 0;
        timer = 0;
        // FIX: Do NOT reset lengthCounter — DMG hardware preserves length counters
        // across APU power-off.  Clearing it here caused test 08 failures.
        sampleIndex  = 0;
        sampleBuffer = 0;
        // FIX: Do NOT zero wave RAM here.  On DMG, wave RAM is preserved when the
        // APU is powered off; the test suite writes specific patterns before cycling
        // power and expects them to survive.  (The old loop wiped them out, causing
        // "regs after power" test 11 to fail at the wave-RAM sub-test.)
    }

    public void ResetAfterPowerOn()
    {
        Enabled      = false;
        timer        = 0;
        // FIX: Do not reset lengthCounter or wave RAM on power-on either.
        sampleIndex  = 0;
        sampleBuffer = 0;
    }

    public void Reset()
    {
        Enabled      = false;
        timer        = 0;
        lengthCounter = 0;
        sampleIndex  = 0;
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

    // FIX: DMG "write while on" quirk (blargg test 12).
    // On DMG, when the wave channel is active, any write to the wave RAM region is
    // silently redirected to the byte at the current playback position regardless of
    // the address supplied by the CPU.  When the channel is off, writes behave normally.
    public void WriteWaveRam(ushort address, byte value)
    {
        if (Enabled)
        {
            // Redirect to currently playing byte.
            waveRam[(sampleIndex >> 1) & 0x0F] = value;
        }
        else
        {
            int index = address - 0xFF30;
            if ((uint)index < 16)
                waveRam[index] = value;
        }
    }

    // FIX: DMG "read while on" quirk (blargg test 09).
    // On DMG, reading any wave RAM address while the channel is active returns the byte
    // at the current playback position, not the byte at the requested address.
    // (On CGB the behaviour is different — reads return 0xFF — but this targets DMG.)
    public byte ReadWaveRam(ushort address)
    {
        if (Enabled)
        {
            return waveRam[(sampleIndex >> 1) & 0x0F];
        }
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

            // Increment first, then latch the nibble at the new position.
            sampleIndex  = (sampleIndex + 1) & 31;
            sampleBuffer = ReadSampleNibble(sampleIndex);
        }
    }

    public void ClockLength()
    {
        // FIX: Remove the "!Enabled" early-out.
        // The length counter must be able to tick regardless of the channel's
        // enabled state so its value stays correct for trigger interactions and
        // the "len ctr during power" tests.
        if ((NR34 & 0x40) == 0)
            return;

        if (lengthCounter > 0)
        {
            lengthCounter--;
            if (lengthCounter == 0)
                Enabled = false;
        }
    }

    // FIX: Public method for APU to apply the extra-length-clock obscure behavior.
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
        int sample     = sampleBuffer & 0x0F;

        switch (volumeCode)
        {
            case 0: return 0;          // mute
            case 1: return sample;     // 100%
            case 2: return sample >> 1; // 50%
            case 3: return sample >> 2; // 25%
            default: return 0;
        }
    }

    private void Trigger()
    {
        // FIX: DMG "trigger while on" wave RAM corruption (blargg test 10).
        // On the real DMG, when the wave channel is re-triggered while it is already
        // playing, within the next two T-cycles the hardware reads the wave RAM byte
        // at the current playback position and writes it back to wave RAM[0].  This
        // overwrites the first byte of wave RAM with whatever nibbles were playing.
        // Timing accuracy here is not cycle-perfect but is sufficient for the test.
        if (Enabled)
        {
            waveRam[0] = waveRam[(sampleIndex >> 1) & 0x0F];
        }

        LengthWasZeroOnTrigger = (lengthCounter == 0);

        if (!DacEnabled)
        {
            Enabled = false;
            return;
        }

        Enabled = true;

        if (lengthCounter == 0)
            lengthCounter = 256;

        // Reload timer.
        timer = (2048 - GetFrequency()) * 2;

        // Reset sample position.  StepTimer increments before reading, so the first
        // sample output after a trigger will come from nibble index 1 (nibble 0 is
        // skipped until the position wraps around).
        sampleIndex = 0;
        // Leave sampleBuffer as-is; it reflects whatever was playing before trigger.
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
        Enabled      = reader.ReadBoolean();
        NR30         = reader.ReadByte();
        NR31         = reader.ReadByte();
        NR32         = reader.ReadByte();
        NR33         = reader.ReadByte();
        NR34         = reader.ReadByte();
        timer        = reader.ReadInt32();
        lengthCounter = reader.ReadInt32();
        sampleIndex  = reader.ReadInt32();
        sampleBuffer = reader.ReadInt32();

        int len    = reader.ReadInt32();
        byte[] data = reader.ReadBytes(len);
        for (int i = 0; i < waveRam.Length; i++)
            waveRam[i] = i < data.Length ? data[i] : (byte)0;
    }
}
