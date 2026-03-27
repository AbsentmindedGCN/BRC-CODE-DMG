using System.IO;

namespace BRCCodeDmg
{
    // joypadState bit layout — MMU ReadIO 0xFF00:
    //   P14 low (JOYP bit 4 = 0): returns (joypadState >> 4)  → direction buttons in upper nibble
    //   P15 low (JOYP bit 5 = 0): returns (joypadState & 0x0F) → action buttons in lower nibble
    //   bit0 = Right / A
    //   bit1 = Left  / B
    //   bit2 = Up    / Select
    //   bit3 = Down  / Start

    public enum GameBoyButton : byte
    {
        A      = 0x01,
        B      = 0x02,
        Select = 0x04,
        Start  = 0x08,
        Right  = 0x10,
        Left   = 0x20,
        Up     = 0x40,
        Down   = 0x80,
    }

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
