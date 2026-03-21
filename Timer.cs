using System.IO;

// ─────────────────────────────────────────────────────────────────────────────
// FIX (Critical): The APU frame sequencer must fire on a falling edge of bit 12
// of the internal 16-bit div counter, NOT bit 4.
//
// divCounter is incremented every T-cycle (4,194,304 Hz).
// Bit 12 flips every 4096 cycles → falling edge every 8192 cycles
//     → 4,194,304 / 8192 ≈ 512 Hz  ✓ (matches Pan Docs)
//
// The old code used (1 << 4) which fires at 131,072 Hz — 256× too fast.
// This caused:
//   • Length counters to expire almost instantly  → channels sounded "missing"
//   • Envelopes to tick 256× too fast             → sudden volume jumps
// ─────────────────────────────────────────────────────────────────────────────

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

            // APU frame sequencer: falling edge of bit 12 of the internal counter.
            // Bit 12 = 0x1000. This gives the correct 512 Hz clock for the sequencer.
            if (((oldDiv & 0x1000) != 0) && ((divCounter & 0x1000) == 0))
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

        // If bit 12 was high when DIV was reset, the falling edge fires now.
        if ((oldDiv & 0x1000) != 0)
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
