using System;
using System.IO;

public sealed class APU
{
    private const int CpuClock = 4194304;
    private const int SampleRate = 48000;
    private const int SampleFifoSize = 32768; // interleaved stereo floats

    private readonly MMU mmu;

    private readonly SquareChannel ch1;
    private readonly SquareChannel ch2;
    private readonly WaveChannel ch3;
    private readonly NoiseChannel ch4;

    private int frameSequencerStep;
    private double sampleCycleCounter;

    private readonly object sampleLock = new object();
    private readonly float[] sampleFifo = new float[SampleFifoSize];
    private int sampleReadIndex;
    private int sampleWriteIndex;
    private int sampleCount;

    private float hpPrevInL;
    private float hpPrevOutL;
    private float hpPrevInR;
    private float hpPrevOutR;

    // simple DC-blocking / high-pass filter coefficient
    private const float HighPassAlpha = 0.996f;

    public APU(MMU mmu)
    {
        this.mmu = mmu;
        ch1 = new SquareChannel(true);
        ch2 = new SquareChannel(false);
        ch3 = new WaveChannel();
        ch4 = new NoiseChannel();
    }

    public void Step(int tCycles)
    {
        if ((mmu.NR52 & 0x80) == 0)
        {
            sampleCycleCounter += tCycles;
            double cyclesPerSample = (double)CpuClock / SampleRate;
            while (sampleCycleCounter >= cyclesPerSample)
            {
                sampleCycleCounter -= cyclesPerSample;
                PushStereoSample(0f, 0f);
            }
            return;
        }

        ch1.StepTimer(tCycles);
        ch2.StepTimer(tCycles);
        ch3.StepTimer(tCycles, mmu);
        ch4.StepTimer(tCycles);

        sampleCycleCounter += tCycles;
        double cps = (double)CpuClock / SampleRate;

        while (sampleCycleCounter >= cps)
        {
            sampleCycleCounter -= cps;
            MixAndPushSample();
        }
    }

    public void ClockDivApu()
    {
        if ((mmu.NR52 & 0x80) == 0)
            return;

        frameSequencerStep = (frameSequencerStep + 1) & 7;

        // length: 256 Hz on steps 0,2,4,6
        if ((frameSequencerStep & 1) == 0)
        {
            ch1.ClockLength();
            ch2.ClockLength();
            ch3.ClockLength();
            ch4.ClockLength();
        }

        // sweep: 128 Hz on steps 2,6
        if (frameSequencerStep == 2 || frameSequencerStep == 6)
        {
            ch1.ClockSweep();
        }

        // envelope: 64 Hz on step 7
        if (frameSequencerStep == 7)
        {
            ch1.ClockEnvelope();
            ch2.ClockEnvelope();
            ch4.ClockEnvelope();
        }
    }

    public byte ReadRegister(ushort address)
    {
        switch (address)
        {
            case 0xFF10: return ch1.NR10;
            case 0xFF11: return ch1.NR11;
            case 0xFF12: return ch1.NR12;
            case 0xFF13: return ch1.NR13;
            case 0xFF14: return (byte)(ch1.NR14 | 0xBF);

            case 0xFF16: return ch2.NR11;
            case 0xFF17: return ch2.NR12;
            case 0xFF18: return ch2.NR13;
            case 0xFF19: return (byte)(ch2.NR14 | 0xBF);

            case 0xFF1A: return ch3.NR30;
            case 0xFF1B: return ch3.NR31;
            case 0xFF1C: return ch3.NR32;
            case 0xFF1D: return ch3.NR33;
            case 0xFF1E: return ch3.NR34;

            case 0xFF20: return ch4.NR41;
            case 0xFF21: return ch4.NR42;
            case 0xFF22: return ch4.NR43;
            case 0xFF23: return ch4.NR44;

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
            // allow only wave RAM writes and NR52 while off
            if (address >= 0xFF30 && address <= 0xFF3F)
                ch3.WriteWaveRam(address, value);
            return;
        }

        switch (address)
        {
            case 0xFF10: ch1.WriteNR10(value); break;
            case 0xFF11: ch1.WriteNR11(value); break;
            case 0xFF12: ch1.WriteNR12(value); break;
            case 0xFF13: ch1.WriteNR13(value); break;
            case 0xFF14: ch1.WriteNR14(value); break;

            case 0xFF16: ch2.WriteNR11(value); break;
            case 0xFF17: ch2.WriteNR12(value); break;
            case 0xFF18: ch2.WriteNR13(value); break;
            case 0xFF19: ch2.WriteNR14(value); break;

            case 0xFF1A: ch3.WriteNR30(value); break;
            case 0xFF1B: ch3.WriteNR31(value); break;
            case 0xFF1C: ch3.WriteNR32(value); break;
            case 0xFF1D: ch3.WriteNR33(value); break;
            case 0xFF1E: ch3.WriteNR34(value); break;

            case 0xFF20: ch4.WriteNR41(value); break;
            case 0xFF21: ch4.WriteNR42(value); break;
            case 0xFF22: ch4.WriteNR43(value); break;
            case 0xFF23: ch4.WriteNR44(value); break;

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
        bool enable = (value & 0x80) != 0;
        bool wasEnabled = (mmu.NR52 & 0x80) != 0;

        if (!enable)
        {
            mmu.NR52 = 0x00;
            mmu.NR50 = 0x00;
            mmu.NR51 = 0x00;
            frameSequencerStep = 0;

            ch1.PowerOff();
            ch2.PowerOff();
            ch3.PowerOff();
            ch4.PowerOff();
        }
        else if (!wasEnabled)
        {
            mmu.NR52 = 0x80;
            frameSequencerStep = 0;
            ch1.ResetDutyStep();
            ch2.ResetDutyStep();
            ch3.Reset();
            ch4.Reset();
        }
    }

    private void MixAndPushSample()
    {
        float s1 = ch1.GetOutput();
        float s2 = ch2.GetOutput();
        float s3 = ch3.GetOutput();
        float s4 = ch4.GetOutput();

        float left = 0f;
        float right = 0f;

        if ((mmu.NR51 & 0x10) != 0) left += s1;
        if ((mmu.NR51 & 0x20) != 0) left += s2;
        if ((mmu.NR51 & 0x40) != 0) left += s3;
        if ((mmu.NR51 & 0x80) != 0) left += s4;

        if ((mmu.NR51 & 0x01) != 0) right += s1;
        if ((mmu.NR51 & 0x02) != 0) right += s2;
        if ((mmu.NR51 & 0x04) != 0) right += s3;
        if ((mmu.NR51 & 0x08) != 0) right += s4;

        float leftVol = ((mmu.NR50 >> 4) & 0x07) / 7f;
        float rightVol = (mmu.NR50 & 0x07) / 7f;

        // Lower overall gain to reduce harsh clipping
        left *= leftVol * 0.12f;
        right *= rightVol * 0.12f;

        // DC blocking / simple high-pass
        left = HighPassLeft(left);
        right = HighPassRight(right);

        PushStereoSample(Clamp(left), Clamp(right));
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

        ch1.LoadState(reader);
        ch2.LoadState(reader);
        ch3.LoadState(reader);
        ch4.LoadState(reader);

        lock (sampleLock)
        {
            sampleReadIndex = 0;
            sampleWriteIndex = 0;
            sampleCount = 0;
        }

        hpPrevInL = 0f;
        hpPrevOutL = 0f;
        hpPrevInR = 0f;
        hpPrevOutR = 0f;
    }

    private float HighPassLeft(float input)
    {
        float output = HighPassAlpha * (hpPrevOutL + input - hpPrevInL);
        hpPrevInL = input;
        hpPrevOutL = output;
        return output;
    }

    private float HighPassRight(float input)
    {
        float output = HighPassAlpha * (hpPrevOutR + input - hpPrevInR);
        hpPrevInR = input;
        hpPrevOutR = output;
        return output;
    }
}