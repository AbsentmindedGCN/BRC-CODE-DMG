using System;
using System.IO;

public sealed class APU
{
    private const int CpuClock = 4194304;
    //private const int SampleFifoSize = 32768; // interleaved stereo floats
    //private const int SampleFifoSize = 8192; // Latency improvement. 4096 and 2048 too low, causes popping

    public const int DefaultSampleFifoSize = 8192;
    private readonly float[] sampleFifo;

    private readonly MMU mmu;
    private readonly int sampleRate;
    private readonly SquareChannel ch1;
    private readonly SquareChannel ch2;
    private readonly WaveChannel ch3;
    private readonly NoiseChannel ch4;

    // Frame sequencer must check current step then increment.
    private int frameSequencerStep;
    private double sampleCycleCounter;

    private readonly object sampleLock = new object();
    //private readonly float[] sampleFifo = new float[SampleFifoSize];
    private int sampleReadIndex;
    private int sampleWriteIndex;
    private int sampleCount;

    // DMG capacitor HPF.
    private double capacitorL;
    private double capacitorR;
    private readonly double hpfChargeFactor;

    private float lastLeftSample;
    private float lastRightSample;

    private bool enabled = true;

    public int BufferedSamples
    {
        get { lock (sampleLock) return sampleCount; }
    }

    public int UnderrunCount { get; private set; }
    public int OverflowDropCount { get; private set; }

    public APU(MMU mmu, int sampleRate = 48000, int sampleFifoSize = DefaultSampleFifoSize)
    {
        this.mmu = mmu;
        this.sampleRate = sampleRate > 0 ? sampleRate : 48000;

        if (sampleFifoSize <= 0)
            sampleFifoSize = DefaultSampleFifoSize;

        sampleFifo = new float[sampleFifoSize];

        ch1 = new SquareChannel(true);
        ch2 = new SquareChannel(false);
        ch3 = new WaveChannel(mmu.IsCGBMode);
        ch4 = new NoiseChannel();

        double baseCoeff = mmu.IsCGBMode ? 0.998943 : 0.999958;
        hpfChargeFactor = Math.Pow(baseCoeff, (double)CpuClock / this.sampleRate);
    }

    public void SetEnabled(bool enabled)
    {
        this.enabled = enabled;
    }

    public void Step(int tCycles)
    {
        double cyclesPerSample = (double)CpuClock / sampleRate;

        if (!enabled)
        {
            sampleCycleCounter += tCycles;
            while (sampleCycleCounter >= cyclesPerSample)
            {
                sampleCycleCounter -= cyclesPerSample;
                PushStereoSample(0f, 0f);
            }

            return;
        }

        if ((mmu.NR52 & 0x80) == 0)
        {
            sampleCycleCounter += tCycles;
            while (sampleCycleCounter >= cyclesPerSample)
            {
                sampleCycleCounter -= cyclesPerSample;
                PushStereoSample(0f, 0f);
            }

            return;
        }

        ch1.StepTimer(tCycles);
        ch2.StepTimer(tCycles);
        ch3.StepTimer(tCycles);
        ch4.StepTimer(tCycles);

        sampleCycleCounter += tCycles;
        while (sampleCycleCounter >= cyclesPerSample)
        {
            sampleCycleCounter -= cyclesPerSample;
            MixAndPushSample();
        }
    }

    public void ClockDivApu()
    {
        if ((mmu.NR52 & 0x80) == 0)
            return;

        // Frame sequencer schedule (512 Hz, 8 steps):
        // 0: length
        // 2: length + sweep
        // 4: length
        // 6: length + sweep
        // 7: envelope

        if ((frameSequencerStep & 1) == 0)
        {
            ch1.ClockLength();
            ch2.ClockLength();
            ch3.ClockLength();
            ch4.ClockLength();
        }

        if (frameSequencerStep == 2 || frameSequencerStep == 6)
            ch1.ClockSweep();

        if (frameSequencerStep == 7)
        {
            ch1.ClockEnvelope();
            ch2.ClockEnvelope();
            ch4.ClockEnvelope();
        }

        frameSequencerStep = (frameSequencerStep + 1) & 7;
    }

    public byte ReadRegister(ushort address)
    {
        switch (address)
        {
            case 0xFF10: return (byte)(ch1.NR10 | 0x80);
            case 0xFF11: return (byte)(ch1.NR11 | 0x3F);
            case 0xFF12: return ch1.NR12;
            case 0xFF13: return 0xFF;
            case 0xFF14: return (byte)(ch1.NR14 | 0xBF);
            case 0xFF16: return (byte)(ch2.NR11 | 0x3F);
            case 0xFF17: return ch2.NR12;
            case 0xFF18: return 0xFF;
            case 0xFF19: return (byte)(ch2.NR14 | 0xBF);
            case 0xFF1A: return (byte)(ch3.NR30 | 0x7F);
            case 0xFF1B: return 0xFF;
            case 0xFF1C: return (byte)(ch3.NR32 | 0x9F);
            case 0xFF1D: return 0xFF;
            case 0xFF1E: return (byte)(ch3.NR34 | 0xBF);
            case 0xFF20: return 0xFF;
            case 0xFF21: return ch4.NR42;
            case 0xFF22: return ch4.NR43;
            case 0xFF23: return (byte)(ch4.NR44 | 0xBF);
            case 0xFF24: return mmu.NR50;
            case 0xFF25: return mmu.NR51;
            case 0xFF26:
                return (byte)(
                    (mmu.NR52 & 0x80) | 0x70 |
                    (ch1.Enabled ? 0x01 : 0) |
                    (ch2.Enabled ? 0x02 : 0) |
                    (ch3.Enabled ? 0x04 : 0) |
                    (ch4.Enabled ? 0x08 : 0));
            default:
                if (address >= 0xFF30 && address <= 0xFF3F)
                    return ch3.ReadWaveRam(address);
                return 0xFF;
        }
    }

    public void WriteRegister(ushort address, byte value)
    {
        if (address == 0xFF26)
        {
            WriteNR52(value);
            return;
        }

        bool apuOn = (mmu.NR52 & 0x80) != 0;
        if (!apuOn)
        {
            if (address >= 0xFF30 && address <= 0xFF3F)
                ch3.WriteWaveRam(address, value);

            if (!mmu.IsCGBMode)
            {
                switch (address)
                {
                    case 0xFF11: ch1.WriteLengthOnly(value); break;
                    case 0xFF16: ch2.WriteLengthOnly(value); break;
                    case 0xFF1B: ch3.WriteLengthOnly(value); break;
                    case 0xFF20: ch4.WriteLengthOnly(value); break;
                }
            }

            return;
        }

        switch (address)
        {
            case 0xFF10: ch1.WriteNR10(value); break;
            case 0xFF11: ch1.WriteNR11(value); break;
            case 0xFF12: ch1.WriteNR12(value); break;
            case 0xFF13: ch1.WriteNR13(value); break;
            case 0xFF14:
                {
                    bool prevLenEnabled = (ch1.NR14 & 0x40) != 0;
                    bool newLenEnabled = (value & 0x40) != 0;
                    bool trigger = (value & 0x80) != 0;
                    bool oddStep = (frameSequencerStep & 1) == 1;

                    if (oddStep && !prevLenEnabled && newLenEnabled && ch1.LengthCounter > 0)
                        ch1.ExtraLengthClock();

                    ch1.WriteNR14(value);

                    if (oddStep && trigger && newLenEnabled && ch1.LengthWasZeroOnTrigger)
                        ch1.ExtraLengthClock();

                    if (trigger && frameSequencerStep == 7)
                        ch1.DelayEnvelopeTimerForObscureTrigger();

                    break;
                }

            case 0xFF16: ch2.WriteNR11(value); break;
            case 0xFF17: ch2.WriteNR12(value); break;
            case 0xFF18: ch2.WriteNR13(value); break;
            case 0xFF19:
                {
                    bool prevLenEnabled = (ch2.NR14 & 0x40) != 0;
                    bool newLenEnabled = (value & 0x40) != 0;
                    bool trigger = (value & 0x80) != 0;
                    bool oddStep = (frameSequencerStep & 1) == 1;

                    if (oddStep && !prevLenEnabled && newLenEnabled && ch2.LengthCounter > 0)
                        ch2.ExtraLengthClock();

                    ch2.WriteNR14(value);

                    if (oddStep && trigger && newLenEnabled && ch2.LengthWasZeroOnTrigger)
                        ch2.ExtraLengthClock();

                    if (trigger && frameSequencerStep == 7)
                        ch2.DelayEnvelopeTimerForObscureTrigger();

                    break;
                }

            case 0xFF1A: ch3.WriteNR30(value); break;
            case 0xFF1B: ch3.WriteNR31(value); break;
            case 0xFF1C: ch3.WriteNR32(value); break;
            case 0xFF1D: ch3.WriteNR33(value); break;
            case 0xFF1E:
                {
                    bool prevLenEnabled = (ch3.NR34 & 0x40) != 0;
                    bool newLenEnabled = (value & 0x40) != 0;
                    bool trigger = (value & 0x80) != 0;
                    bool oddStep = (frameSequencerStep & 1) == 1;

                    if (oddStep && !prevLenEnabled && newLenEnabled && ch3.LengthCounter > 0)
                        ch3.ExtraLengthClock();

                    // For DMG test 10, CH3 retrigger corruption is keyed to the
                    // channel's internal timer state (next sample read 2 T-cycles away),
                    // not an externally adjusted timestamp.
                    ch3.WriteNR34(value);

                    if (oddStep && trigger && newLenEnabled && ch3.LengthWasZeroOnTrigger)
                        ch3.ExtraLengthClock();

                    break;
                }

            case 0xFF20: ch4.WriteNR41(value); break;
            case 0xFF21: ch4.WriteNR42(value); break;
            case 0xFF22: ch4.WriteNR43(value); break;
            case 0xFF23:
                {
                    bool prevLenEnabled = (ch4.NR44 & 0x40) != 0;
                    bool newLenEnabled = (value & 0x40) != 0;
                    bool trigger = (value & 0x80) != 0;
                    bool oddStep = (frameSequencerStep & 1) == 1;

                    if (oddStep && !prevLenEnabled && newLenEnabled && ch4.LengthCounter > 0)
                        ch4.ExtraLengthClock();

                    ch4.WriteNR44(value);

                    if (oddStep && trigger && newLenEnabled && ch4.LengthWasZeroOnTrigger)
                        ch4.ExtraLengthClock();

                    if (trigger && frameSequencerStep == 7)
                        ch4.DelayEnvelopeTimerForObscureTrigger();

                    break;
                }

            case 0xFF24: mmu.NR50 = value; break;
            case 0xFF25: mmu.NR51 = value; break;

            default:
                if (address >= 0xFF30 && address <= 0xFF3F)
                    ch3.WriteWaveRam(address, value);
                break;
        }
    }

    private void WriteNR52(byte value)
    {
        bool newEnabled = (value & 0x80) != 0;
        bool oldEnabled = (mmu.NR52 & 0x80) != 0;

        if (!newEnabled && oldEnabled)
        {
            mmu.NR52 = 0x00;
            mmu.NR50 = 0x00;
            mmu.NR51 = 0x00;
            frameSequencerStep = 0;

            ch1.PowerOff();
            ch2.PowerOff();
            ch3.PowerOff();
            ch4.PowerOff();

            capacitorL = 0.0;
            capacitorR = 0.0;
        }
        else if (newEnabled && !oldEnabled)
        {
            mmu.NR52 = 0x80;
            frameSequencerStep = 0;

            ch1.ResetAfterPowerOn(mmu.IsCGBMode);
            ch2.ResetAfterPowerOn(mmu.IsCGBMode);
            ch3.ResetAfterPowerOn(mmu.IsCGBMode);
            ch4.ResetAfterPowerOn(mmu.IsCGBMode);

            capacitorL = 0.0;
            capacitorR = 0.0;
        }
    }

    private static float DigitalToAnalog(int digital)
    {
        // Hardware DAC: 0 -> -1.0, 15 -> +1.0.
        return (digital / 7.5f) - 1.0f;
    }

    private float Channel1Analog() => ch1.DacEnabled ? DigitalToAnalog(ch1.GetDigitalOutput()) : 0f;
    private float Channel2Analog() => ch2.DacEnabled ? DigitalToAnalog(ch2.GetDigitalOutput()) : 0f;
    private float Channel3Analog() => ch3.DacEnabled ? DigitalToAnalog(ch3.GetDigitalOutput()) : 0f;
    private float Channel4Analog() => ch4.DacEnabled ? DigitalToAnalog(ch4.GetDigitalOutput()) : 0f;

    private void MixAndPushSample()
    {
        double left = 0.0;
        double right = 0.0;

        float s1 = Channel1Analog();
        float s2 = Channel2Analog();
        float s3 = Channel3Analog();
        float s4 = Channel4Analog();

        if ((mmu.NR51 & 0x10) != 0) left += s1;
        if ((mmu.NR51 & 0x20) != 0) left += s2;
        if ((mmu.NR51 & 0x40) != 0) left += s3;
        if ((mmu.NR51 & 0x80) != 0) left += s4;

        if ((mmu.NR51 & 0x01) != 0) right += s1;
        if ((mmu.NR51 & 0x02) != 0) right += s2;
        if ((mmu.NR51 & 0x04) != 0) right += s3;
        if ((mmu.NR51 & 0x08) != 0) right += s4;

        // Each master output is multiplied by (volume + 1).
        double leftVol = (((mmu.NR50 >> 4) & 0x07) + 1) / 8.0;
        double rightVol = ((mmu.NR50 & 0x07) + 1) / 8.0;

        left *= leftVol * 0.25;
        right *= rightVol * 0.25;

        // The high-pass filter is connected whenever any channel DAC is on, regardless of NR51 routing.
        bool anyDacEnabled = ch1.DacEnabled || ch2.DacEnabled || ch3.DacEnabled || ch4.DacEnabled;
        left = HighPassDMG(left, ref capacitorL, anyDacEnabled);
        right = HighPassDMG(right, ref capacitorR, anyDacEnabled);

        PushStereoSample(Clamp((float)left), Clamp((float)right));
    }

    private double HighPassDMG(double input, ref double capacitor, bool dacsEnabled)
    {
        double output = input - capacitor;

        if (dacsEnabled)
        {
            capacitor = input - output * hpfChargeFactor;
        }
        else
        {
            // Let the capacitor discharge smoothly instead of snapping to silence.
            capacitor *= hpfChargeFactor;
            output = -capacitor;
        }

        return output;
    }

    private static float Clamp(float v)
    {
        if (v < -1f) return -1f;
        if (v > 1f) return 1f;
        return v;
    }

    private void PushStereoSample(float left, float right)
    {
        lock (sampleLock)
        {
            EnsureSpaceNoLock(2);

            sampleFifo[sampleWriteIndex] = left;
            sampleWriteIndex = (sampleWriteIndex + 1) % sampleFifo.Length;

            sampleFifo[sampleWriteIndex] = right;
            sampleWriteIndex = (sampleWriteIndex + 1) % sampleFifo.Length;

            sampleCount += 2;
            lastLeftSample = left;
            lastRightSample = right;
        }
    }

    private void EnsureSpaceNoLock(int needed)
    {
        // Always keep the FIFO aligned to stereo frames.
        while (sampleCount + needed > sampleFifo.Length)
        {
            if (sampleCount >= 2)
            {
                sampleReadIndex = (sampleReadIndex + 2) % sampleFifo.Length;
                sampleCount -= 2;
            }
            else
            {
                sampleReadIndex = 0;
                sampleWriteIndex = 0;
                sampleCount = 0;
            }

            OverflowDropCount++;
        }
    }

    public int ReadSamples(float[] dest, int offset, int count)
    {
        lock (sampleLock)
        {
            int wanted = count & ~1;         // even number only
            int available = sampleCount & ~1;
            int read = Math.Min(wanted, available);

            for (int i = 0; i < read; i++)
            {
                dest[offset + i] = sampleFifo[sampleReadIndex];
                sampleReadIndex = (sampleReadIndex + 1) % sampleFifo.Length;
            }

            sampleCount -= read;

            for (int i = read; i < wanted; i += 2)
            {
                dest[offset + i] = lastLeftSample;
                if (i + 1 < wanted)
                    dest[offset + i + 1] = lastRightSample;
            }

            if (read < wanted)
                UnderrunCount++;

            return wanted;
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(frameSequencerStep);
        writer.Write(sampleCycleCounter);
        writer.Write(mmu.NR50);
        writer.Write(mmu.NR51);
        writer.Write(mmu.NR52);
        writer.Write(capacitorL);
        writer.Write(capacitorR);

        ch1.SaveState(writer);
        ch2.SaveState(writer);
        ch3.SaveState(writer);
        ch4.SaveState(writer);
    }

    public void LoadState(BinaryReader reader)
    {
        frameSequencerStep = reader.ReadInt32();
        sampleCycleCounter = reader.ReadDouble();
        mmu.NR50 = reader.ReadByte();
        mmu.NR51 = reader.ReadByte();
        mmu.NR52 = reader.ReadByte();
        capacitorL = reader.ReadDouble();
        capacitorR = reader.ReadDouble();

        ch1.LoadState(reader);
        ch2.LoadState(reader);
        ch3.LoadState(reader);
        ch4.LoadState(reader);

        lock (sampleLock)
        {
            sampleReadIndex = 0;
            sampleWriteIndex = 0;
            sampleCount = 0;
            UnderrunCount = 0;
            OverflowDropCount = 0;
        }
    }
}
