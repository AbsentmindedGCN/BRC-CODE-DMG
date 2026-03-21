using System;
using System.IO;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
// PPU.cs — Game Boy / Game Boy Color Picture Processing Unit
//
// GBC additions vs the original DMG-only file:
//
//  • HDMA hook  — mmu.StepHdma() called on every HBlank
//  • BG / Window rendering
//      – Reads tile *attributes* from VRAM bank 1 (same map address)
//      – Attribute bits: palette(0-2), bank(3), flipX(5), flipY(6), priority(7)
//      – Fetches tile row bytes from VRAM bank 0 or 1 depending on attr bit 3
//      – Looks up the 15-bit GBC colour from mmu.GetBgColor(palette, colIdx)
//      – Converts 5-5-5 GBC colour to Unity Color32
//  • Sprite rendering
//      – Reads tile bytes from VRAM bank 1 when OAM attr bit 3 is set
//      – Uses OBJ colour palettes (0-7) from mmu.GetObjColor()
//      – Applies GBC BG-to-OBJ priority (attr bit 7 + LCDC bit 0 master)
//  • Stores per-pixel bg-colour-index for priority evaluation
//  • Falls back gracefully to DMG rendering when mmu.IsGbc == false
// ═══════════════════════════════════════════════════════════════════════════════

namespace BRCCodeDmg
{
    public class PPU
    {
        private const int HBLANK = 0;
        private const int VBLANK = 1;
        private const int OAM    = 2;
        private const int VRAM   = 3;

        private const int SCANLINE_CYCLES = 456;
        private const int ScreenWidth  = 160;
        private const int ScreenHeight = 144;

        private int mode;
        private int cycles;

        private readonly MMU mmu;

        private readonly Color32[] frameBuffer;
        private readonly Color32[] scanlineBuffer = new Color32[ScreenWidth];

        // Per-pixel background colour index (0-3) and BG-priority flag.
        // Used by the sprite renderer to respect BG-over-OBJ priority.
        private readonly int[]  bgColorIndex   = new int[ScreenWidth];
        private readonly bool[] bgHasPriority  = new bool[ScreenWidth]; // GBC attr bit 7

        private bool vblankTriggered;
        private int  windowLineCounter;
        private bool lcdPreviouslyOff;

        public bool FrameDirty { get; private set; }

        // ── Constructor ───────────────────────────────────────────────────────
        public PPU(MMU mmu)
        {
            this.mmu  = mmu;
            mode      = OAM;
            cycles    = 0;
            frameBuffer = new Color32[ScreenWidth * ScreenHeight];
            ClearToPalette0();
        }

        // ── Main step ─────────────────────────────────────────────────────────
        public void Step(int elapsedCycles)
        {
            cycles += elapsedCycles;

            if ((mmu.LCDC & 0x80) != 0 && lcdPreviouslyOff)
            {
                cycles = 0;
                lcdPreviouslyOff = false;
            }
            else if ((mmu.LCDC & 0x80) == 0)
            {
                lcdPreviouslyOff = true;
                mode = HBLANK;
                return;
            }

            switch (mode)
            {
                case OAM:
                    if (cycles >= 80)
                    {
                        cycles -= 80;
                        mode = VRAM;
                        mmu.STAT = (byte)((mmu.STAT & 0xFC) | mode);
                    }
                    break;

                case VRAM:
                    if (cycles >= 172)
                    {
                        cycles -= 172;
                        mode = HBLANK;
                        mmu.STAT = (byte)((mmu.STAT & 0xFC) | mode);

                        if (mmu.LY < 144)
                            RenderScanline();

                        // GBC HDMA: transfer one 16-byte block at every HBlank
                        if (mmu.IsGbc)
                            mmu.StepHdma();

                        if ((mmu.STAT & 0x08) != 0)
                            mmu.IF = (byte)(mmu.IF | 0x02);
                    }
                    break;

                case HBLANK:
                    if (cycles >= 204)
                    {
                        cycles -= 204;
                        mmu.LY++;
                        SetLYCFlag();

                        if (mmu.LY == 144)
                        {
                            mode = VBLANK;
                            vblankTriggered = false;
                            FrameDirty = true;

                            if ((mmu.STAT & 0x10) != 0)
                                mmu.IF = (byte)(mmu.IF | 0x02);
                        }
                        else
                        {
                            if ((mmu.STAT & 0x20) != 0)
                                mmu.IF = (byte)(mmu.IF | 0x02);

                            mode = OAM;
                        }

                        mmu.STAT = (byte)((mmu.STAT & 0xFC) | mode);
                    }
                    break;

                case VBLANK:
                    if (!vblankTriggered && mmu.LY == 144)
                    {
                        if ((mmu.LCDC & 0x80) != 0)
                        {
                            mmu.IF = (byte)(mmu.IF | 0x01);
                            vblankTriggered = true;
                        }
                    }

                    if (cycles >= SCANLINE_CYCLES)
                    {
                        cycles -= SCANLINE_CYCLES;
                        mmu.LY++;
                        SetLYCFlag();

                        if (mmu.LY == 153)
                        {
                            mmu.LY = 0;
                            mode = OAM;
                            vblankTriggered = false;
                            windowLineCounter = 0;
                            mmu.STAT = (byte)((mmu.STAT & 0xFC) | mode);

                            if ((mmu.STAT & 0x20) != 0)
                                mmu.IF = (byte)(mmu.IF | 0x02);
                        }
                    }
                    break;
            }
        }

        public Color32[] GetUnityFrame() => frameBuffer;
        public void ClearDirtyFlag() => FrameDirty = false;

        // ── Scanline rendering ────────────────────────────────────────────────
        private void RenderScanline()
        {
            for (int i = 0; i < ScreenWidth; i++)
            {
                bgColorIndex[i]  = 0;
                bgHasPriority[i] = false;
            }

            RenderBackground();
            RenderWindow();
            RenderSprites();
            Array.Copy(scanlineBuffer, 0, frameBuffer, mmu.LY * ScreenWidth, ScreenWidth);
        }

        // ── Background ────────────────────────────────────────────────────────
        private void RenderBackground()
        {
            // On DMG, LCDC bit 0 = 0 blanks BG.
            // On GBC, LCDC bit 0 = 0 only disables BG-to-OBJ priority (BG still shows).
            if (!mmu.IsGbc && (mmu.LCDC & 0x01) == 0)
                return;

            int scanline = mmu.LY;
            int scrollX  = mmu.SCX;
            int scrollY  = mmu.SCY;

            ushort tileMapBase = (mmu.LCDC & 0x08) != 0 ? (ushort)0x9C00 : (ushort)0x9800;

            for (int x = 0; x < ScreenWidth; x++)
            {
                int bgX = (scrollX + x) & 0xFF;
                int bgY = (scrollY + scanline) & 0xFF;

                ushort mapAddr = (ushort)(tileMapBase + (bgY / 8) * 32 + (bgX / 8));
                byte tileNum   = mmu.Read(mapAddr);

                // GBC: attribute byte at the same address in VRAM bank 1
                byte attr     = mmu.IsGbc ? mmu.ReadVramBank1(mapAddr) : (byte)0;
                bool useBank1 = mmu.IsGbc && (attr & 0x08) != 0;
                bool flipX    = mmu.IsGbc && (attr & 0x20) != 0;
                bool flipY    = mmu.IsGbc && (attr & 0x40) != 0;
                int  palNum   = mmu.IsGbc ?  (attr & 0x07) : 0;

                ushort tileAddr = TileAddress(tileNum, (mmu.LCDC & 0x10) != 0);

                int lineInTile = bgY % 8;
                if (flipY) lineInTile = 7 - lineInTile;

                ushort rowAddr = (ushort)(tileAddr + lineInTile * 2);
                byte lo = useBank1 ? mmu.ReadVramBank1(rowAddr)                    : mmu.Read(rowAddr);
                byte hi = useBank1 ? mmu.ReadVramBank1((ushort)(rowAddr + 1))      : mmu.Read((ushort)(rowAddr + 1));

                int bit  = flipX ? (bgX & 7) : (7 - (bgX & 7));
                int cidx = (((hi >> bit) & 1) << 1) | ((lo >> bit) & 1);

                bgColorIndex[x]  = cidx;
                bgHasPriority[x] = mmu.IsGbc && (attr & 0x80) != 0;

                scanlineBuffer[x] = mmu.IsGbc
                    ? GbcColorToColor32(mmu.GetBgColor(palNum, cidx))
                    : ConvertDmgPaletteColor((mmu.BGP >> (cidx * 2)) & 0x03);
            }
        }

        // ── Window ────────────────────────────────────────────────────────────
        private void RenderWindow()
        {
            if ((mmu.LCDC & (1 << 5)) == 0)
                return;

            int scanline = mmu.LY;
            int winX     = mmu.WX - 7;
            int winY     = mmu.WY;

            if (scanline < winY || winX >= ScreenWidth)
                return;

            if (scanline == winY)
                windowLineCounter = 0;

            ushort tileMapBase = (mmu.LCDC & (1 << 6)) != 0 ? (ushort)0x9C00 : (ushort)0x9800;
            bool rendered = false;

            for (int x = Math.Max(0, winX); x < ScreenWidth; x++)
            {
                rendered = true;

                int winCol = x - winX;
                int tileRow = windowLineCounter / 8;
                int tileCol = winCol / 8;

                ushort mapAddr = (ushort)(tileMapBase + tileRow * 32 + tileCol);
                byte tileNum   = mmu.Read(mapAddr);

                byte attr     = mmu.IsGbc ? mmu.ReadVramBank1(mapAddr) : (byte)0;
                bool useBank1 = mmu.IsGbc && (attr & 0x08) != 0;
                bool flipX    = mmu.IsGbc && (attr & 0x20) != 0;
                bool flipY    = mmu.IsGbc && (attr & 0x40) != 0;
                int  palNum   = mmu.IsGbc ?  (attr & 0x07) : 0;

                ushort tileAddr = TileAddress(tileNum, (mmu.LCDC & (1 << 4)) != 0);

                int lineInTile = windowLineCounter % 8;
                if (flipY) lineInTile = 7 - lineInTile;

                ushort rowAddr = (ushort)(tileAddr + lineInTile * 2);
                byte lo = useBank1 ? mmu.ReadVramBank1(rowAddr)               : mmu.Read(rowAddr);
                byte hi = useBank1 ? mmu.ReadVramBank1((ushort)(rowAddr + 1)) : mmu.Read((ushort)(rowAddr + 1));

                int bit  = flipX ? (winCol & 7) : (7 - (winCol & 7));
                int cidx = (((hi >> bit) & 1) << 1) | ((lo >> bit) & 1);

                bgColorIndex[x]  = cidx;
                bgHasPriority[x] = mmu.IsGbc && (attr & 0x80) != 0;

                scanlineBuffer[x] = mmu.IsGbc
                    ? GbcColorToColor32(mmu.GetBgColor(palNum, cidx))
                    : ConvertDmgPaletteColor((mmu.BGP >> (cidx * 2)) & 0x03);
            }

            if (rendered)
                windowLineCounter++;
        }

        // ── Sprites ───────────────────────────────────────────────────────────
        private void RenderSprites()
        {
            if ((mmu.LCDC & (1 << 1)) == 0)
                return;

            int scanline  = mmu.LY;
            int sprHeight = (mmu.LCDC & (1 << 2)) != 0 ? 16 : 8;
            // GBC master-priority flag: when LCDC bit 0 = 0, sprites are always on top
            bool masterPriorityOff = mmu.IsGbc && (mmu.LCDC & 0x01) == 0;

            int renderedSprites = 0;
            int[] pixelOwner = new int[ScreenWidth];
            for (int i = 0; i < ScreenWidth; i++) pixelOwner[i] = -1;

            for (int i = 0; i < 40; i++)
            {
                if (renderedSprites >= 10)
                    break;

                int sprBase  = 0xFE00 + i * 4;
                int yPos     = mmu.Read((ushort)(sprBase))     - 16;
                int xPos     = mmu.Read((ushort)(sprBase + 1)) - 8;
                byte tileIdx = mmu.Read((ushort)(sprBase + 2));
                byte attr    = mmu.Read((ushort)(sprBase + 3));

                if (scanline < yPos || scanline >= yPos + sprHeight)
                    continue;

                bool yFlip = (attr & (1 << 6)) != 0;
                bool xFlip = (attr & (1 << 5)) != 0;
                int lineInSprite = scanline - yPos;
                if (yFlip) lineInSprite = sprHeight - 1 - lineInSprite;

                if (sprHeight == 16)
                {
                    tileIdx &= 0xFE;
                    if (lineInSprite >= 8) { tileIdx++; lineInSprite -= 8; }
                }

                bool useBank1 = mmu.IsGbc && (attr & 0x08) != 0;
                ushort tileAddr = (ushort)(0x8000 + tileIdx * 16 + lineInSprite * 2);

                byte lo = useBank1 ? mmu.ReadVramBank1(tileAddr)               : mmu.Read(tileAddr);
                byte hi = useBank1 ? mmu.ReadVramBank1((ushort)(tileAddr + 1)) : mmu.Read((ushort)(tileAddr + 1));

                int sprPalNum = mmu.IsGbc ? (attr & 0x07) : ((attr & (1 << 4)) != 0 ? 1 : 0);
                byte dmgPal   = (attr & (1 << 4)) != 0 ? mmu.OBP1 : mmu.OBP0;
                bool bgOverObj = (attr & (1 << 7)) != 0;

                for (int px = 0; px < 8; px++)
                {
                    int bit  = xFlip ? px : (7 - px);
                    int cidx = (((hi >> bit) & 1) << 1) | ((lo >> bit) & 1);

                    if (cidx == 0) continue; // transparent

                    int screenX = xPos + px;
                    if (screenX < 0 || screenX >= ScreenWidth) continue;

                    // Priority checks
                    if (!masterPriorityOff)
                    {
                        // GBC per-tile BG priority
                        if (bgHasPriority[screenX] && bgColorIndex[screenX] != 0) continue;
                        // OAM bg-over-obj
                        if (bgOverObj && bgColorIndex[screenX] != 0) continue;
                    }

                    // First-sprite-wins (lowest OAM index has priority)
                    if (pixelOwner[screenX] != -1 && xPos >= pixelOwner[screenX]) continue;
                    pixelOwner[screenX] = xPos;

                    scanlineBuffer[screenX] = mmu.IsGbc
                        ? GbcColorToColor32(mmu.GetObjColor(sprPalNum, cidx))
                        : ConvertDmgPaletteColor((dmgPal >> (cidx * 2)) & 0x03);
                }

                renderedSprites++;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Returns the VRAM address of the first byte of a tile's data.</summary>
        private static ushort TileAddress(byte tileNum, bool unsigned)
        {
            if (unsigned)
                return (ushort)(0x8000 + tileNum * 16);
            else
                return (ushort)(0x9000 + (sbyte)tileNum * 16);
        }

        /// <summary>Converts a 15-bit GBC colour (5-5-5 RGB) to Unity Color32.</summary>
        private static Color32 GbcColorToColor32(ushort c)
        {
            int r5 = (c)       & 0x1F;
            int g5 = (c >> 5)  & 0x1F;
            int b5 = (c >> 10) & 0x1F;
            return new Color32(
                (byte)((r5 * 255 + 15) / 31),
                (byte)((g5 * 255 + 15) / 31),
                (byte)((b5 * 255 + 15) / 31),
                255);
        }

        /// <summary>DMG: maps a 2-bit palette index through the active colour palette lookup table.</summary>
        private Color32 ConvertDmgPaletteColor(int paletteColor)
        {
            return Helper.palettes[Helper.paletteName][paletteColor];
        }

        private void SetLYCFlag()
        {
            if (mmu.LY == mmu.LYC)
            {
                mmu.STAT = (byte)(mmu.STAT | 0x04);
                if ((mmu.STAT & 0x40) != 0)
                    mmu.IF = (byte)(mmu.IF | 0x02);
            }
            else
            {
                mmu.STAT = (byte)(mmu.STAT & 0xFB);
            }
        }

        private void ClearToPalette0()
        {
            Color32 c = ConvertDmgPaletteColor(0);
            for (int i = 0; i < frameBuffer.Length; i++)
                frameBuffer[i] = c;
        }

        // ── State serialisation ───────────────────────────────────────────────
        private static void WriteColor32Array(BinaryWriter writer, Color32[] data)
        {
            if (data == null) { writer.Write(-1); return; }
            writer.Write(data.Length);
            for (int i = 0; i < data.Length; i++)
            {
                writer.Write(data[i].r);
                writer.Write(data[i].g);
                writer.Write(data[i].b);
                writer.Write(data[i].a);
            }
        }

        private static Color32[] ReadColor32Array(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0) return null;
            Color32[] data = new Color32[length];
            for (int i = 0; i < length; i++)
            {
                byte r = reader.ReadByte(), g = reader.ReadByte(),
                     b = reader.ReadByte(), a = reader.ReadByte();
                data[i] = new Color32(r, g, b, a);
            }
            return data;
        }

        public void SaveState(BinaryWriter writer)
        {
            writer.Write(mode);
            writer.Write(cycles);
            writer.Write(vblankTriggered);
            writer.Write(windowLineCounter);
            writer.Write(lcdPreviouslyOff);
            WriteColor32Array(writer, frameBuffer);
            WriteColor32Array(writer, scanlineBuffer);
        }

        public void LoadState(BinaryReader reader)
        {
            mode              = reader.ReadInt32();
            cycles            = reader.ReadInt32();
            vblankTriggered   = reader.ReadBoolean();
            windowLineCounter = reader.ReadInt32();
            lcdPreviouslyOff  = reader.ReadBoolean();

            Color32[] loadedFrame = ReadColor32Array(reader);
            Color32[] loadedScan  = ReadColor32Array(reader);

            if (loadedFrame == null || loadedFrame.Length != frameBuffer.Length)
                throw new InvalidOperationException("Invalid frameBuffer savestate data.");
            if (loadedScan == null || loadedScan.Length != scanlineBuffer.Length)
                throw new InvalidOperationException("Invalid scanlineBuffer savestate data.");

            Array.Copy(loadedFrame, frameBuffer,    frameBuffer.Length);
            Array.Copy(loadedScan,  scanlineBuffer, scanlineBuffer.Length);
            FrameDirty = true;
        }
    }
}
