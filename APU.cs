using System;
using System.IO;

public sealed class APU
{
    private const int CpuClock = 4194304;
    private const int SampleFifoSize = 32768; // interleaved stereo floats

    private readonly MMU mmu;
    private readonly int sampleRate;
    private readonly SquareChannel ch1;
    private readonly SquareChannel ch2;
    private readonly WaveChannel ch3;
    private readonly NoiseChannel ch4;

    // FIX: The frame sequencer step now uses a "check-THEN-increment" model
    // so that step 0 fires a length clock on the very first DIV edge after power-on.
    // The old pre-increment model caused the first length clock to be missed,
    // shifting the entire frame sequencer schedule by one period (≈2 ms).
    private int frameSequencerStep;

    private double sampleCycleCounter;

    private readonly object sampleLock = new object();
    private readonly float[] sampleFifo = new float[SampleFifoSize];
    private int sampleReadIndex;
    private int sampleWriteIndex;
    private int sampleCount;

    // DMG capacitor HPF
    private double capacitorL;
    private double capacitorR;
    private readonly double hpfChargeFactor;

    private bool enabled = true;

    public APU(MMU mmu, int sampleRate = 48000)
    {
        this.mmu = mmu;
        this.sampleRate = sampleRate > 0 ? sampleRate : 48000;
        ch1 = new SquareChannel(true);
        ch2 = new SquareChannel(false);
        ch3 = new WaveChannel();
        ch4 = new NoiseChannel();

        // Pan Docs DMG factor adjusted to output sample rate
        hpfChargeFactor = Math.Pow(0.999958, (double)CpuClock / this.sampleRate);
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

        // FIX: "Check then increment" — hardware fires components at the CURRENT step,
        // then advances.  The old code incremented first, which meant step 0 (a length
        // clock) was never executed after a power-on reset (the first real clock ended
        // up at step 1 which does nothing), offsetting all timing by one period.
        //
        // Frame sequencer schedule (512 Hz, 8 steps):
        //   Step 0: Length
        //   Step 1: (nothing)
        //   Step 2: Length + Sweep
        //   Step 3: (nothing)
        //   Step 4: Length
        //   Step 5: (nothing)
        //   Step 6: Length + Sweep
        //   Step 7: Envelope

        // Even steps (0, 2, 4, 6) clock the length counters.
        if ((frameSequencerStep & 1) == 0)
        {
            ch1.ClockLength();
            ch2.ClockLength();
            ch3.ClockLength();
            ch4.ClockLength();
        }

        // Steps 2 and 6 also clock the sweep unit on CH1.
        if (frameSequencerStep == 2 || frameSequencerStep == 6)
        {
            ch1.ClockSweep();
        }

        // Step 7 clocks the envelope units.
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
                    (mmu.NR52 & 0x80) |
                    0x70 |
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
            // On DMG, when the APU is powered off:
            //   - Wave RAM is always accessible.
            //   - Length counter registers are still writable (DMG-specific).
            //   - All other registers are blocked.
            if (address >= 0xFF30 && address <= 0xFF3F)
                ch3.WriteWaveRam(address, value);

            switch (address)
            {
                case 0xFF11: ch1.WriteNR11(value); break;
                case 0xFF16: ch2.WriteNR11(value); break;
                case 0xFF1B: ch3.WriteNR31(value); break;
                case 0xFF20: ch4.WriteNR41(value); break;
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
                // FIX: Extra-length-clock obscure behavior on NRx4 writes.
                //
                // Two hardware quirks are emulated here:
                //
                // (A) Trigger + length enable, length counter was 0:
                //     After the trigger reloads the counter from 0→64, if the frame
                //     sequencer's NEXT step is odd (won't clock length), the freshly
                //     loaded counter is immediately decremented by one.
                //
                // (B) Enabling length counter without triggering (bit 6: 0→1):
                //     If the next step is odd (won't clock length), one extra clock fires.
                //
                // "Next step" = current value of frameSequencerStep (because we already
                // incremented it at the end of ClockDivApu).  Odd values (1,3,5,7) do
                // NOT clock length, so the extra clock compensates.
                bool prevLenEnabled = (ch1.NR14 & 0x40) != 0;
                bool newLenEnabled  = (value  & 0x40) != 0;
                bool trigger        = (value  & 0x80) != 0;

                ch1.WriteNR14(value);

                if (newLenEnabled && (frameSequencerStep & 1) == 1)
                {
                    if (trigger && ch1.LengthWasZeroOnTrigger)
                        ch1.ExtraLengthClock();
                    else if (!trigger && !prevLenEnabled)
                        ch1.ExtraLengthClock();
                }
                break;
            }

            case 0xFF16: ch2.WriteNR11(value); break;
            case 0xFF17: ch2.WriteNR12(value); break;
            case 0xFF18: ch2.WriteNR13(value); break;

            case 0xFF19:
            {
                bool prevLenEnabled = (ch2.NR14 & 0x40) != 0;
                bool newLenEnabled  = (value  & 0x40) != 0;
                bool trigger        = (value  & 0x80) != 0;

                ch2.WriteNR14(value);

                if (newLenEnabled && (frameSequencerStep & 1) == 1)
                {
                    if (trigger && ch2.LengthWasZeroOnTrigger)
                        ch2.ExtraLengthClock();
                    else if (!trigger && !prevLenEnabled)
                        ch2.ExtraLengthClock();
                }
                break;
            }

            case 0xFF1A: ch3.WriteNR30(value); break;
            case 0xFF1B: ch3.WriteNR31(value); break;
            case 0xFF1C: ch3.WriteNR32(value); break;
            case 0xFF1D: ch3.WriteNR33(value); break;

            case 0xFF1E:
            {
                bool prevLenEnabled = (ch3.NR34 & 0x40) != 0;
                bool newLenEnabled  = (value  & 0x40) != 0;
                bool trigger        = (value  & 0x80) != 0;

                ch3.WriteNR34(value);

                if (newLenEnabled && (frameSequencerStep & 1) == 1)
                {
                    if (trigger && ch3.LengthWasZeroOnTrigger)
                        ch3.ExtraLengthClock();
                    else if (!trigger && !prevLenEnabled)
                        ch3.ExtraLengthClock();
                }
                break;
            }

            case 0xFF20: ch4.WriteNR41(value); break;
            case 0xFF21: ch4.WriteNR42(value); break;
            case 0xFF22: ch4.WriteNR43(value); break;

            case 0xFF23:
            {
                bool prevLenEnabled = (ch4.NR44 & 0x40) != 0;
                bool newLenEnabled  = (value  & 0x40) != 0;
                bool trigger        = (value  & 0x80) != 0;

                ch4.WriteNR44(value);

                if (newLenEnabled && (frameSequencerStep & 1) == 1)
                {
                    if (trigger && ch4.LengthWasZeroOnTrigger)
                        ch4.ExtraLengthClock();
                    else if (!trigger && !prevLenEnabled)
                        ch4.ExtraLengthClock();
                }
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

        if (!newEnabled)
        {
            mmu.NR52 = 0x00;
            mmu.NR50 = 0x00;
            mmu.NR51 = 0x00;
            // FIX: Reset frame sequencer to 0 so the very first clock after power-on
            // will fire step 0 (a length step).
            frameSequencerStep = 0;
            ch1.PowerOff();
            ch2.PowerOff();
            ch3.PowerOff();
            ch4.PowerOff();
            capacitorL = 0.0;
            capacitorR = 0.0;
        }
        else if (!oldEnabled)
        {
            mmu.NR52 = 0x80;
            // FIX: Same — start at step 0 so the first clock is a length clock.
            frameSequencerStep = 0;
            ch1.ResetDutyStep();
            ch2.ResetDutyStep();
            ch3.ResetAfterPowerOn();
            ch4.Reset();
            capacitorL = 0.0;
            capacitorR = 0.0;
        }
    }

    private static float DigitalToAnalog(int digital)
    {
        // Pan Docs: digital 0 -> analog +1, digital 15 -> analog -1
        return 1.0f - (digital / 7.5f);
    }

    private float Channel1Analog() => ch1.DacEnabled ? DigitalToAnalog(ch1.GetDigitalOutput()) : 0f;
    private float Channel2Analog() => ch2.DacEnabled ? DigitalToAnalog(ch2.GetDigitalOutput()) : 0f;
    private float Channel3Analog() => ch3.DacEnabled ? DigitalToAnalog(ch3.GetDigitalOutput()) : 0f;
    private float Channel4Analog() => ch4.DacEnabled ? DigitalToAnalog(ch4.GetDigitalOutput()) : 0f;

    private void MixAndPushSample()
    {
        double left  = 0.0;
        double right = 0.0;
        bool leftAnyDac  = false;
        bool rightAnyDac = false;

        float s1 = Channel1Analog();
        float s2 = Channel2Analog();
        float s3 = Channel3Analog();
        float s4 = Channel4Analog();

        if ((mmu.NR51 & 0x10) != 0) { left  += s1; leftAnyDac  |= ch1.DacEnabled; }
        if ((mmu.NR51 & 0x20) != 0) { left  += s2; leftAnyDac  |= ch2.DacEnabled; }
        if ((mmu.NR51 & 0x40) != 0) { left  += s3; leftAnyDac  |= ch3.DacEnabled; }
        if ((mmu.NR51 & 0x80) != 0) { left  += s4; leftAnyDac  |= ch4.DacEnabled; }

        if ((mmu.NR51 & 0x01) != 0) { right += s1; rightAnyDac |= ch1.DacEnabled; }
        if ((mmu.NR51 & 0x02) != 0) { right += s2; rightAnyDac |= ch2.DacEnabled; }
        if ((mmu.NR51 & 0x04) != 0) { right += s3; rightAnyDac |= ch3.DacEnabled; }
        if ((mmu.NR51 & 0x08) != 0) { right += s4; rightAnyDac |= ch4.DacEnabled; }

        double leftVol  = (((mmu.NR50 >> 4) & 0x07) + 1) / 8.0;
        double rightVol = ((mmu.NR50 & 0x07) + 1) / 8.0;

        left  *= leftVol  * 0.25;
        right *= rightVol * 0.25;

        left  = HighPassDMG(left,  ref capacitorL, leftAnyDac);
        right = HighPassDMG(right, ref capacitorR, rightAnyDac);

        PushStereoSample(Clamp((float)left), Clamp((float)right));
    }

    private double HighPassDMG(double input, ref double capacitor, bool dacsEnabled)
    {
        if (!dacsEnabled)
            return 0.0;
        double output = input - capacitor;
        capacitor = input - output * hpfChargeFactor;
        return output;
    }

    private static float Clamp(float v)
    {
        if (v < -1f) return -1f;
        if (v >  1f) return  1f;
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
        }
    }

    private void EnsureSpaceNoLock(int needed)
    {
        while (sampleCount + needed > sampleFifo.Length)
        {
            sampleReadIndex = (sampleReadIndex + 1) % sampleFifo.Length;
            sampleCount--;
        }
    }

    public int ReadSamples(float[] dest, int offset, int count)
    {
        lock (sampleLock)
        {
            int read = Math.Min(count, sampleCount);
            for (int i = 0; i < read; i++)
            {
                dest[offset + i] = sampleFifo[sampleReadIndex];
                sampleReadIndex = (sampleReadIndex + 1) % sampleFifo.Length;
            }
            sampleCount -= read;
            return read;
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
            sampleReadIndex  = 0;
            sampleWriteIndex = 0;
            sampleCount      = 0;
        }
    }
}
