using System.IO;

namespace BRCCodeDmg
{
    public class Joypad
    {
        private readonly MMU mmu;

        public Joypad(MMU mmu)
        {
            this.mmu = mmu;
        }

        public void SetButton(GameBoyButton button, bool pressed)
        {
            byte mask = (byte)button;

            // 0 = pressed, 1 = released
            if (pressed)
                mmu.joypadState &= (byte)~mask;
            else
                mmu.joypadState |= mask;
        }

        public void SaveState(BinaryWriter writer)
        {
            // No extra internal state beyond what MMU already stores.
        }

        public void LoadState(BinaryReader reader)
        {
            // No extra internal state beyond what MMU already stores.
        }
    }
}