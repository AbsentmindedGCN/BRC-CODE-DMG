using System.IO;

class Timer {
    private MMU mmu;
    
    int cycles;

    public Timer(MMU mmu) {
        this.mmu = mmu;
        cycles = 0;
    }

    public void Step(int elapsedCycles) {
        cycles += elapsedCycles;

        if (cycles >= 256) {
            cycles -= 256;
            mmu.DIV++;
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(cycles);
    }

    public void LoadState(BinaryReader reader)
    {
        cycles = reader.ReadInt32();
    }
}
