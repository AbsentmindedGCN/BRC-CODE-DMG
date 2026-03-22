using System;
using System.IO;
using UnityEngine;

namespace BRCCodeDmg
{
    // =========================================================================
    //  PPU — Pixel Processing Unit  (DMG + GBC)
    //
    //  Key changes vs. original:
    //   • GBC background tiles read attributes from VRAM bank 1 (per-tile
    //     palette number 0-7, X/Y flip, tile VRAM bank, BG priority flag).
    //   • Window tiles get the same GBC treatment.
    //   • BG / Window colour comes from mmu.GetCGBBgColor() in GBC mode.
    //   • Sprites in GBC mode select VRAM bank (OAM attr bit 3) and CGB OBJ
    //     palette (OAM attr bits 0-2); OBP0/OBP1 are used only in DMG mode.
    //   • Sprite-to-sprite priority follows OAM order in GBC (first sprite
    //     wins) vs. lowest X in DMG.
    //   • BG-to-sprite priority respects LCDC bit 0 (master priority) and BG
    //     tile attribute bit 7 in GBC mode.
    //   • bgColorIndexBuffer tracks raw 0-3 colour indices per pixel so the
    //     sprite renderer can apply the correct priority rules.
    //   • bgAttrPriorityBuffer tracks per-pixel BG-tile priority flags (GBC).
    //   • mmu.ExecuteHBlankDMA() is called at the start of every HBlank so
    //     H-Blank DMA updates are applied per-scanline as the hardware does it.
    // =========================================================================
    public class PPU
    {
        // PPU modes
        private const int HBLANK        = 0;
        private const int VBLANK        = 1;
        private const int OAM           = 2;
        private const int VRAM_MODE     = 3;
        private const int SCANLINE_CYCLES = 456;
        private const int ScreenWidth   = 160;
        private const int ScreenHeight  = 144;

        private int  mode;
        private int  cycles;
        private readonly MMU mmu;

        // Frame / scanline buffers
        private readonly Color32[] frameBuffer;
        private readonly Color32[] scanlineBuffer = new Color32[ScreenWidth];

        // Per-pixel tracking used by RenderSprites() for priority
        private readonly int[]  bgColorIndexBuffer   = new int[ScreenWidth];  // raw 0-3 index
        private readonly bool[] bgAttrPriorityBuffer = new bool[ScreenWidth]; // GBC tile attr bit 7

        private bool vblankTriggered;
        private int  windowLineCounter;
        private bool lcdPreviouslyOff;

        public bool FrameDirty { get; private set; }

        // =====================================================================
        public PPU(MMU mmu)
        {
            this.mmu = mmu;
            mode     = OAM;
            cycles   = 0;
            frameBuffer = new Color32[ScreenWidth * ScreenHeight];
            ClearToPalette0();
        }

        // =====================================================================
        // Main step
        // =====================================================================
        public void Step(int elapsedCycles)
        {
            cycles += elapsedCycles;

            // Handle LCD being toggled on
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
                        mode = VRAM_MODE;
                        mmu.STAT = (byte)((mmu.STAT & 0xFC) | mode);
                    }
                    break;

                case VRAM_MODE:
                    if (cycles >= 172)
                    {
                        cycles -= 172;
                        mode = HBLANK;
                        mmu.STAT = (byte)((mmu.STAT & 0xFC) | mode);

                        if (mmu.LY < 144)
                            RenderScanline();

                        // Trigger H-Blank DMA one block per HBlank
                        mmu.ExecuteHBlankDMA();

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
                            windowLineCounter = 0;
                            mode = OAM;
                            vblankTriggered = false;
                            mmu.STAT = (byte)((mmu.STAT & 0xFC) | mode);
                            if ((mmu.STAT & 0x20) != 0)
                                mmu.IF = (byte)(mmu.IF | 0x02);
                        }
                    }
                    break;
            }
        }

        public Color32[] GetUnityFrame() => frameBuffer;
        public void ClearDirtyFlag()     => FrameDirty = false;

        // =====================================================================
        // Scanline rendering
        // =====================================================================
        private void RenderScanline()
        {
            // Clear priority tracking buffers for this scanline
            for (int i = 0; i < ScreenWidth; i++)
            {
                bgColorIndexBuffer[i]   = 0;
                bgAttrPriorityBuffer[i] = false;
            }

            RenderBackground();
            RenderWindow();
            RenderSprites();

            Array.Copy(scanlineBuffer, 0, frameBuffer, mmu.LY * ScreenWidth, ScreenWidth);
        }

        // =====================================================================
        // Background
        // =====================================================================
        private void RenderBackground()
        {
            int currentScanline = mmu.LY;
            int scrollX = mmu.SCX;
            int scrollY = mmu.SCY;

            bool cgb = mmu.IsCGBMode;

            // DMG: LCDC bit 0 = 0 → BG disabled (fill with colour 0).
            // CGB: LCDC bit 0 = 0 → Master priority off (BG still renders).
            if (!cgb && (mmu.LCDC & 0x01) == 0)
            {
                // Fill scanline with DMG palette colour 0 and leave index buffer at 0.
                Color32 c0 = ConvertDMGColor(0);
                for (int x = 0; x < ScreenWidth; x++)
                    scanlineBuffer[x] = c0;
                return;
            }

            ushort tileMapBase = ((mmu.LCDC & 0x08) != 0) ? (ushort)0x9C00 : (ushort)0x9800;

            for (int x = 0; x < ScreenWidth; x++)
            {
                int bgX = (scrollX + x) & 0xFF;
                int bgY = (scrollY + currentScanline) & 0xFF;

                int tileCol   = bgX >> 3;
                int tileRow   = bgY >> 3;
                int tileIndex = tileRow * 32 + tileCol;

                // Tile map is in VRAM bank 0; attributes are in VRAM bank 1 (GBC only)
                int  tileMapRel = tileMapBase - 0x8000 + tileIndex;
                byte tileNumber = mmu.ReadVramDirect(tileMapRel, 0);
                byte attrs      = cgb ? mmu.ReadVramDirect(tileMapRel, 1) : (byte)0;

                // Decode GBC tile attributes
                int  attrPalette    = attrs & 0x07;            // BG palette 0-7
                int  attrTileBank   = (attrs >> 3) & 0x01;     // Tile data VRAM bank
                bool attrXFlip      = (attrs & 0x20) != 0;
                bool attrYFlip      = (attrs & 0x40) != 0;
                bool attrBgPriority = (attrs & 0x80) != 0;     // BG over OBJ priority flag

                // Tile data base address
                ushort tileDataBase = ((mmu.LCDC & 0x10) != 0 || tileNumber >= 128)
                    ? (ushort)0x8000
                    : (ushort)0x9000;
                int tileRelBase = tileDataBase - 0x8000 + tileNumber * 16;

                // Y position within the tile, with optional Y-flip
                int lineInTile = bgY & 7;
                if (attrYFlip) lineInTile = 7 - lineInTile;

                // Fetch tile row data from the correct VRAM bank
                int bank = cgb ? attrTileBank : 0;
                byte tileLow  = mmu.ReadVramDirect(tileRelBase + lineInTile * 2,     bank);
                byte tileHigh = mmu.ReadVramDirect(tileRelBase + lineInTile * 2 + 1, bank);

                // Bit index within the tile, with optional X-flip
                int bitIndex = attrXFlip ? (bgX & 7) : (7 - (bgX & 7));
                int colorBit = (((tileHigh >> bitIndex) & 1) << 1) | ((tileLow >> bitIndex) & 1);

                // Store raw colour index for sprite priority checks
                bgColorIndexBuffer[x] = colorBit;
                // BG priority flag is only meaningful in GBC mode and if colour != 0
                bgAttrPriorityBuffer[x] = cgb && attrBgPriority && colorBit != 0;

                // Resolve actual pixel colour
                if (cgb)
                    scanlineBuffer[x] = mmu.GetCGBBgColor(attrPalette, colorBit);
                else
                    scanlineBuffer[x] = ConvertDMGColor((mmu.BGP >> (colorBit * 2)) & 0x03);
            }
        }

        // =====================================================================
        // Window
        // =====================================================================
        private void RenderWindow()
        {
            if ((mmu.LCDC & (1 << 5)) == 0) return;

            int currentScanline = mmu.LY;
            int windowX = mmu.WX - 7;
            int windowY = mmu.WY;

            if (currentScanline < windowY) return;
            if (currentScanline == windowY) windowLineCounter = 0;

            bool cgb = mmu.IsCGBMode;
            ushort tileMapBase = ((mmu.LCDC & (1 << 6)) != 0) ? (ushort)0x9C00 : (ushort)0x9800;

            bool windowRendered = false;

            for (int x = 0; x < ScreenWidth; x++)
            {
                if (x < windowX) continue;

                windowRendered = true;
                int windowCol = x - windowX;
                int tileCol   = windowCol >> 3;
                int tileRow   = windowLineCounter >> 3;
                int tileIndex = tileRow * 32 + tileCol;

                int  tileMapRel = tileMapBase - 0x8000 + tileIndex;
                byte tileNumber = mmu.ReadVramDirect(tileMapRel, 0);
                byte attrs      = cgb ? mmu.ReadVramDirect(tileMapRel, 1) : (byte)0;

                int  attrPalette    = attrs & 0x07;
                int  attrTileBank   = (attrs >> 3) & 0x01;
                bool attrXFlip      = (attrs & 0x20) != 0;
                bool attrYFlip      = (attrs & 0x40) != 0;
                bool attrBgPriority = (attrs & 0x80) != 0;

                ushort tileDataBase = ((mmu.LCDC & (1 << 4)) != 0 || tileNumber >= 128)
                    ? (ushort)0x8000
                    : (ushort)0x9000;
                int tileRelBase = tileDataBase - 0x8000 + tileNumber * 16;

                int lineInTile = windowLineCounter & 7;
                if (attrYFlip) lineInTile = 7 - lineInTile;

                int bank = cgb ? attrTileBank : 0;
                byte tileLow  = mmu.ReadVramDirect(tileRelBase + lineInTile * 2,     bank);
                byte tileHigh = mmu.ReadVramDirect(tileRelBase + lineInTile * 2 + 1, bank);

                int bitIndex = attrXFlip ? (windowCol & 7) : (7 - (windowCol & 7));
                int colorBit = (((tileHigh >> bitIndex) & 1) << 1) | ((tileLow >> bitIndex) & 1);

                bgColorIndexBuffer[x]   = colorBit;
                bgAttrPriorityBuffer[x] = cgb && attrBgPriority && colorBit != 0;

                if (cgb)
                    scanlineBuffer[x] = mmu.GetCGBBgColor(attrPalette, colorBit);
                else
                    scanlineBuffer[x] = ConvertDMGColor((mmu.BGP >> (colorBit * 2)) & 0x03);
            }

            if (windowRendered) windowLineCounter++;
        }

        // =====================================================================
        // Sprites (OBJ)
        // =====================================================================
        private void RenderSprites()
        {
            if ((mmu.LCDC & (1 << 1)) == 0) return;

            int currentScanline = mmu.LY;
            bool cgb = mmu.IsCGBMode;

            // In GBC mode, LCDC bit 0 = 0 means sprites ALWAYS win priority
            // (the BG/Window still renders its colours, but priority bits ignored).
            bool masterBgPriority = (mmu.LCDC & 0x01) != 0;

            int renderedSprites = 0;
            // pixelOwner: DMG = X position of winning sprite, GBC = sprite OAM index
            int[] pixelOwner = new int[ScreenWidth];
            for (int i = 0; i < ScreenWidth; i++) pixelOwner[i] = -1;

            int spriteHeight = ((mmu.LCDC & (1 << 2)) != 0) ? 16 : 8;

            for (int i = 0; i < 40; i++)
            {
                if (renderedSprites >= 10) break;

                int  spriteBase = 0xFE00 + i * 4;
                int  yPos       = mmu.Read((ushort)(spriteBase))     - 16;
                int  xPos       = mmu.Read((ushort)(spriteBase + 1)) - 8;
                byte tileIndex  = mmu.Read((ushort)(spriteBase + 2));
                byte attributes = mmu.Read((ushort)(spriteBase + 3));

                if (currentScanline < yPos || currentScanline >= yPos + spriteHeight)
                    continue;

                int lineInSprite = currentScanline - yPos;
                bool yFlip = (attributes & (1 << 6)) != 0;
                if (yFlip) lineInSprite = spriteHeight - 1 - lineInSprite;

                // 8×16 sprites: use even tile for top half, odd for bottom
                if (spriteHeight == 16)
                {
                    tileIndex &= 0xFE;
                    if (lineInSprite >= 8) { tileIndex++; lineInSprite -= 8; }
                }

                // GBC: bit 3 of attributes selects VRAM bank for tile data
                int tileBank = (cgb && (attributes & (1 << 3)) != 0) ? 1 : 0;

                int tileRelBase = tileIndex * 16 + lineInSprite * 2;
                byte tileLow  = mmu.ReadVramDirect(tileRelBase,     tileBank);
                byte tileHigh = mmu.ReadVramDirect(tileRelBase + 1, tileBank);

                bool xFlip = (attributes & (1 << 5)) != 0;

                // GBC: bits 0-2 of attributes are the CGB OBJ palette number
                // DMG: bit 4 selects OBP0 or OBP1
                int cgbPalette  = attributes & 0x07;
                bool useOBP1_dmg = (attributes & (1 << 4)) != 0;
                byte dmgPalette = useOBP1_dmg ? mmu.OBP1 : mmu.OBP0;

                for (int px = 0; px < 8; px++)
                {
                    int bitIndex = xFlip ? px : (7 - px);
                    int colorBit = (((tileHigh >> bitIndex) & 1) << 1) | ((tileLow >> bitIndex) & 1);

                    // Colour 0 is always transparent for sprites
                    if (colorBit == 0) continue;

                    int screenX = xPos + px;
                    if (screenX < 0 || screenX >= ScreenWidth) continue;

                    // --------------- Priority logic ---------------
                    // GBC: first sprite in OAM order wins over later sprites
                    // DMG: sprite with lower X position wins
                    bool canDraw;
                    if (cgb)
                        canDraw = (pixelOwner[screenX] == -1);
                    else
                        canDraw = (pixelOwner[screenX] == -1 || xPos < pixelOwner[screenX]);

                    if (!canDraw) continue;

                    // --------------- BG-to-OBJ priority ---------------
                    if (cgb)
                    {
                        // masterBgPriority = LCDC bit 0.
                        // If 0: sprites always on top (no further checks needed).
                        // If 1: BG tile attr bit 7 or OBJ attr bit 7 can make BG win
                        //        when the BG pixel colour index != 0.
                        if (masterBgPriority)
                        {
                            bool objAttrBgPriority = (attributes & (1 << 7)) != 0;
                            if ((bgAttrPriorityBuffer[screenX] || objAttrBgPriority)
                                && bgColorIndexBuffer[screenX] != 0)
                                continue; // BG wins
                        }
                    }
                    else
                    {
                        // DMG: OBJ attr bit 7 = 1 → sprite behind BG colours 1-3
                        bool bgOverObj = (attributes & (1 << 7)) != 0;
                        if (bgOverObj && bgColorIndexBuffer[screenX] != 0)
                            continue;
                    }

                    // Record winner
                    pixelOwner[screenX] = cgb ? i : xPos;

                    // Resolve pixel colour
                    Color32 pixelColor;
                    if (cgb)
                        pixelColor = mmu.GetCGBObjColor(cgbPalette, colorBit);
                    else
                        pixelColor = ConvertDMGColor((dmgPalette >> (colorBit * 2)) & 0x03);

                    scanlineBuffer[screenX] = pixelColor;
                }

                renderedSprites++;
            }
        }

        // =====================================================================
        // Colour helpers
        // =====================================================================

        // DMG: map a 0-3 palette colour index through the active DMG palette table
        private Color32 ConvertDMGColor(int paletteColor)
        {
            return Helper.palettes[Helper.paletteName][paletteColor];
        }

        // =====================================================================
        // LYC / STAT
        // =====================================================================
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
                mmu.STAT = (byte)(mmu.STAT & ~0x04);
            }
        }

        private void ClearToPalette0()
        {
            Color32 c = ConvertDMGColor(0);
            for (int i = 0; i < frameBuffer.Length; i++)
                frameBuffer[i] = c;
        }

        // =====================================================================
        // Save State
        // =====================================================================
        private static void WriteColor32Array(BinaryWriter writer, Color32[] data)
        {
            if (data == null) { writer.Write(-1); return; }
            writer.Write(data.Length);
            foreach (var c in data)
            {
                writer.Write(c.r); writer.Write(c.g);
                writer.Write(c.b); writer.Write(c.a);
            }
        }

        private static Color32[] ReadColor32Array(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0) return null;
            var data = new Color32[length];
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
            mode               = reader.ReadInt32();
            cycles             = reader.ReadInt32();
            vblankTriggered    = reader.ReadBoolean();
            windowLineCounter  = reader.ReadInt32();
            lcdPreviouslyOff   = reader.ReadBoolean();

            Color32[] fb = ReadColor32Array(reader);
            Color32[] sb = ReadColor32Array(reader);

            if (fb == null || fb.Length != frameBuffer.Length)
                throw new InvalidOperationException("Invalid frameBuffer in savestate.");
            if (sb == null || sb.Length != scanlineBuffer.Length)
                throw new InvalidOperationException("Invalid scanlineBuffer in savestate.");

            Array.Copy(fb, frameBuffer,    frameBuffer.Length);
            Array.Copy(sb, scanlineBuffer, scanlineBuffer.Length);

            FrameDirty = true;
        }
    }
}
