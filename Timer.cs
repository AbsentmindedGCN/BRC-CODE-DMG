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

            // FIX: APU frame sequencer clocks at 512 Hz on DMG.
            // The internal divCounter counts T-cycles at 4,194,304 Hz.
            // 4,194,304 / 512 = 8,192 = 2^13  →  falling edge of bit 12.
            // Previously this used (1 << 4) which clocked at 131,072 Hz (256× too fast),
            // making length counters, sweep, and envelope all expire/advance way too quickly.
            if (((oldDiv & (1 << 12)) != 0) && ((divCounter & (1 << 12)) == 0))
                apu.ClockDivApu();

            // TIMA increments on falling edge of selected divider bit
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

        // FIX: Same bit-12 falling-edge check for the APU on a forced DIV reset.
        if ((oldDiv & (1 << 12)) != 0)
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

    private static int GetTimerBitMask(int tacClock)
    {
        switch (tacClock)
        {
            case 0: return 1 << 9;  // 4096 Hz
            case 1: return 1 << 3;  // 262144 Hz
            case 2: return 1 << 5;  // 65536 Hz
            case 3: return 1 << 7;  // 16384 Hz
            default: return 1 << 9;
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
