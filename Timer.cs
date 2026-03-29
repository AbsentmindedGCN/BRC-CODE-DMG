using System.IO;

public class Timer
{
    private readonly MMU mmu;
    private readonly APU apu;
    private ushort divCounter;

    public Timer(MMU mmu, APU apu)
    {
        this.mmu = mmu;
        this.apu = apu;
        divCounter = 0;
    }

    public void Step(int elapsedCycles)
    {
        for (int i = 0; i < elapsedCycles; i++)
        {
            ushort oldDiv = divCounter;
            divCounter++;
            mmu.DIV = (byte)(divCounter >> 8);

            // APU frame sequencer: bit 12 in normal speed, bit 13 in double speed.
            int apuBit = mmu.CgbDoubleSpeed ? 0x2000 : 0x1000;
            if (((oldDiv & apuBit) != 0) && ((divCounter & apuBit) == 0))
                apu.ClockDivApu();

            if ((mmu.TAC & 0x04) != 0)
            {
                int bitMask = GetTimerBitMask(mmu.TAC & 0x03);
                if (((oldDiv & bitMask) != 0) && ((divCounter & bitMask) == 0))
                    IncrementTima();
            }
        }
    }

    public void ResetDiv()
    {
        ushort oldDiv = divCounter;
        divCounter = 0;
        mmu.DIV = 0;

        int apuBit = mmu.CgbDoubleSpeed ? 0x2000 : 0x1000;
        if ((oldDiv & apuBit) != 0)
            apu.ClockDivApu();

        if ((mmu.TAC & 0x04) != 0)
        {
            int bitMask = GetTimerBitMask(mmu.TAC & 0x03);
            if ((oldDiv & bitMask) != 0)
                IncrementTima();
        }
    }

    private void IncrementTima()
    {
        if (mmu.TIMA == 0xFF)
        {
            mmu.TIMA = mmu.TMA;
            mmu.IF |= 0x04;
        }
        else
        {
            mmu.TIMA++;
        }
    }

    private int GetTimerBitMask(int tacClock)
    {
        int shift = mmu.CgbDoubleSpeed ? 1 : 0;
        switch (tacClock)
        {
            case 0: return (1 << 9) << shift;
            case 1: return (1 << 3) << shift;
            case 2: return (1 << 5) << shift;
            case 3: return (1 << 7) << shift;
            default: return (1 << 9) << shift;
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(divCounter);
    }

    public void LoadState(BinaryReader reader)
    {
        divCounter = reader.ReadUInt16();
        mmu.DIV = (byte)(divCounter >> 8);
    }
}
