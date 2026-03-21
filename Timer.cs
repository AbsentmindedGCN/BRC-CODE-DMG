using System.IO;

// ─────────────────────────────────────────────────────────────────────────────
// Timer.cs — Game Boy / Game Boy Color hardware timer
//
// GBC double-speed change:
//   In double-speed mode the CPU (and therefore the internal div counter) runs
//   at 8.388608 MHz instead of 4.194304 MHz.  The APU frame sequencer must
//   still fire at 512 Hz, so we monitor bit 13 of the div counter instead of
//   bit 12.  Both conditions produce a falling edge at 512 Hz:
//
//     Normal speed:  bit 12 falling edge at  4.194 MHz  →  512 Hz
//     Double speed:  bit 13 falling edge at  8.388 MHz  →  512 Hz
//
// The TIMA timer bit-masks are similarly doubled in double-speed mode so that
// TIMA increments at the same wall-clock rates as on DMG.
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

            // APU frame sequencer — falling edge of the appropriate div bit.
            // Bit 12 at normal speed, bit 13 at double speed → always 512 Hz.
            int apuBit = mmu.CgbDoubleSpeed ? 0x2000 : 0x1000;
            if (((oldDiv & apuBit) != 0) && ((divCounter & apuBit) == 0))
                apu.ClockDivApu();

            // TIMA — falling edge of the selected divider bit.
            // In double-speed mode each bit-mask doubles to keep the same rates.
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

        // Treat a falling edge caused by the reset just as we do during normal ticking.
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

    // Returns the div-counter bit-mask for the selected TAC clock rate.
    // In double-speed mode the div counter runs 2x faster, so we need to
    // shift each mask left by one to maintain the original Hz rates.
    private int GetTimerBitMask(int tacClock)
    {
        int shift = mmu.CgbDoubleSpeed ? 1 : 0;
        switch (tacClock)
        {
            case 0: return (1 << 9)  << shift;   // 4096 Hz
            case 1: return (1 << 3)  << shift;   // 262144 Hz
            case 2: return (1 << 5)  << shift;   // 65536 Hz
            case 3: return (1 << 7)  << shift;   // 16384 Hz
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
