using System;
using System.IO;
using UnityEngine;

namespace BRCCodeDmg
{
    public class PPU
    {
        private const int HBLANK = 0;
        private const int VBLANK = 1;
        private const int OAM = 2;
        private const int VRAM_MODE = 3;

        private const int SCANLINE_CYCLES = 456;
        private const int OAM_CYCLES = 80;
        private const int VRAM_CYCLES = 172;
        private const int HBLANK_CYCLES = 204;

        private const int ScreenWidth = 160;
        private const int ScreenHeight = 144;

        private readonly MMU mmu;

        private int mode;
        private int cycles;

        private bool vblankTriggered;
        private bool lcdPreviouslyOff;
        private bool lcdBlankFramePending;
        private bool lcdOffFramePushed;

        // Window state
        private bool windowWyTriggeredThisFrame;
        private int windowLineCounter;

        // Frame / scanline buffers
        private readonly Color32[] frameBuffer;
        private readonly Color32[] scanlineBuffer = new Color32[ScreenWidth];

        // Per-pixel BG info used by sprite priority
        private readonly int[] bgColorIndexBuffer = new int[ScreenWidth];
        private readonly bool[] bgAttrPriorityBuffer = new bool[ScreenWidth];

        public bool FrameDirty { get; private set; }

        public PPU(MMU mmu)
        {
            this.mmu = mmu;
            mode = OAM;
            cycles = 0;
            frameBuffer = new Color32[ScreenWidth * ScreenHeight];
            ClearToPalette0();
        }

        public Color32[] GetUnityFrame() => frameBuffer;
        public void ClearDirtyFlag() => FrameDirty = false;

        public void Step(int elapsedCycles)
        {
            cycles += elapsedCycles;

            bool lcdEnabled = (mmu.LCDC & 0x80) != 0;

            if (lcdEnabled && lcdPreviouslyOff)
            {
                lcdPreviouslyOff = false;
                cycles = 0;
                mode = OAM;
                mmu.LY = 0;
                windowWyTriggeredThisFrame = false;
                windowLineCounter = 0;
                vblankTriggered = false;
                lcdBlankFramePending = true;
                lcdOffFramePushed = false;
                mmu.STAT = (byte)((mmu.STAT & 0xFC) | mode);
                SetLYCFlag();
            }
            else if (!lcdEnabled)
            {
                // Only do the LCD-off transition work once.
                if (!lcdPreviouslyOff)
                {
                    lcdPreviouslyOff = true;
                    mode = HBLANK;
                    cycles = 0;
                    mmu.LY = 0;
                    windowWyTriggeredThisFrame = false;
                    windowLineCounter = 0;
                    vblankTriggered = false;

                    // Optional: Push one blank frame once when LCD turns off, quick fix!!
                    //ClearToLcdOffBlank(); // NO LONGER NEEDED
                    FrameDirty = true;
                    lcdOffFramePushed = true;
                }

                return;
            }

            switch (mode)
            {
                case OAM:
                    if (cycles >= OAM_CYCLES)
                    {
                        cycles -= OAM_CYCLES;

                        // WY is evaluated at the start of a visible scanline and then latched.
                        if (mmu.LY == mmu.WY && mmu.WY <= 143)
                            windowWyTriggeredThisFrame = true;

                        mode = VRAM_MODE;
                        mmu.STAT = (byte)((mmu.STAT & 0xFC) | mode);
                    }
                    break;

                case VRAM_MODE:
                    if (cycles >= VRAM_CYCLES)
                    {
                        cycles -= VRAM_CYCLES;

                        if (mmu.LY < ScreenHeight)
                        {
                            if (lcdBlankFramePending)
                            {
                                RenderBlankScanline(mmu.LY);
                            }
                            else
                            {
                                RenderScanline();
                            }
                        }

                        mode = HBLANK;
                        mmu.STAT = (byte)((mmu.STAT & 0xFC) | mode);

                        mmu.ExecuteHBlankDMA();

                        if ((mmu.STAT & 0x08) != 0)
                            mmu.IF = (byte)(mmu.IF | 0x02);
                    }
                    break;

                case HBLANK:
                    if (cycles >= HBLANK_CYCLES)
                    {
                        cycles -= HBLANK_CYCLES;
                        mmu.LY++;
                        SetLYCFlag();

                        if (mmu.LY == 144)
                        {
                            mode = VBLANK;
                            mmu.STAT = (byte)((mmu.STAT & 0xFC) | mode);
                            FrameDirty = true;
                            vblankTriggered = false;
                            lcdBlankFramePending = false;

                            if ((mmu.STAT & 0x10) != 0)
                                mmu.IF = (byte)(mmu.IF | 0x02);
                        }
                        else
                        {
                            mode = OAM;
                            mmu.STAT = (byte)((mmu.STAT & 0xFC) | mode);

                            if ((mmu.STAT & 0x20) != 0)
                                mmu.IF = (byte)(mmu.IF | 0x02);
                        }
                    }
                    break;

                case VBLANK:
                    if (!vblankTriggered && mmu.LY == 144)
                    {
                        mmu.IF = (byte)(mmu.IF | 0x01);
                        vblankTriggered = true;
                    }

                    if (cycles >= SCANLINE_CYCLES)
                    {
                        cycles -= SCANLINE_CYCLES;
                        mmu.LY++;
                        SetLYCFlag();

                        if (mmu.LY > 153)
                        {
                            mmu.LY = 0;
                            SetLYCFlag();

                            mode = OAM;
                            mmu.STAT = (byte)((mmu.STAT & 0xFC) | mode);

                            vblankTriggered = false;
                            windowWyTriggeredThisFrame = false;
                            windowLineCounter = 0;

                            if ((mmu.STAT & 0x20) != 0)
                                mmu.IF = (byte)(mmu.IF | 0x02);
                        }
                    }
                    break;
            }
        }

        private void RenderScanline()
        {
            bool cgb = mmu.IsCGBMode;
            int ly = mmu.LY;

            for (int i = 0; i < ScreenWidth; i++)
            {
                bgColorIndexBuffer[i] = 0;
                bgAttrPriorityBuffer[i] = false;
            }

            // DMG: BG off means blank color 0.
            // CGB: BG still renders; LCDC bit 0 mainly affects priority behavior.
            if (!cgb && (mmu.LCDC & 0x01) == 0)
            {
                Color32 c0 = ConvertDMGColor(0);
                for (int x = 0; x < ScreenWidth; x++)
                    scanlineBuffer[x] = c0;
            }
            else
            {
                RenderBackgroundAndWindowScanline(ly, cgb);
            }

            RenderSprites();
            Array.Copy(scanlineBuffer, 0, frameBuffer, ly * ScreenWidth, ScreenWidth);
        }

        private void RenderBackgroundAndWindowScanline(int ly, bool cgb)
        {
            int scxLowLatch = mmu.SCX & 0x07;

            bool windowEnabled = (mmu.LCDC & (1 << 5)) != 0;
            bool windowVisibleCoords = mmu.WX <= 166 && mmu.WY <= 143;
            bool windowUsable = windowEnabled && windowVisibleCoords && windowWyTriggeredThisFrame;

            int windowStartX = mmu.WX - 7;
            bool windowRenderedThisLine = false;
            int currentWindowLine = windowLineCounter;

            for (int x = 0; x < ScreenWidth; x++)
            {
                bool useWindow = windowUsable && x >= windowStartX;

                if (useWindow)
                {
                    windowRenderedThisLine = true;
                    RenderWindowPixel(x, currentWindowLine, cgb);
                }
                else
                {
                    // Keep low 3 bits latched for the line; reread upper SCX bits.
                    int effectiveScx = (mmu.SCX & 0xF8) | scxLowLatch;
                    RenderBackgroundPixel(x, ly, effectiveScx, cgb);
                }
            }

            if (windowRenderedThisLine)
                windowLineCounter++;
        }

        private void RenderBackgroundPixel(int screenX, int ly, int effectiveScx, bool cgb)
        {
            int bgX = (effectiveScx + screenX) & 0xFF;
            int bgY = (mmu.SCY + ly) & 0xFF;

            ushort tileMapBase = ((mmu.LCDC & 0x08) != 0) ? (ushort)0x9C00 : (ushort)0x9800;

            int tileCol = bgX >> 3;
            int tileRow = bgY >> 3;
            int tileIndex = tileRow * 32 + tileCol;
            int tileMapRel = (tileMapBase - 0x8000) + tileIndex;

            byte tileNumber = mmu.ReadVramDirect(tileMapRel, 0);
            byte attrs = cgb ? mmu.ReadVramDirect(tileMapRel, 1) : (byte)0;

            int attrPalette = attrs & 0x07;
            int attrTileBank = (attrs >> 3) & 0x01;
            bool attrXFlip = (attrs & 0x20) != 0;
            bool attrYFlip = (attrs & 0x40) != 0;
            bool attrBgPriority = (attrs & 0x80) != 0;

            int lineInTile = bgY & 7;
            if (attrYFlip) lineInTile = 7 - lineInTile;

            int tileDataRelBase = GetBgWindowTileDataRelBase(tileNumber, (mmu.LCDC & 0x10) != 0);
            int bank = cgb ? attrTileBank : 0;

            byte tileLow = mmu.ReadVramDirect(tileDataRelBase + lineInTile * 2, bank);
            byte tileHigh = mmu.ReadVramDirect(tileDataRelBase + lineInTile * 2 + 1, bank);

            int pixelInTile = bgX & 7;
            int bitIndex = attrXFlip ? pixelInTile : (7 - pixelInTile);
            int colorBit = (((tileHigh >> bitIndex) & 1) << 1) | ((tileLow >> bitIndex) & 1);

            bgColorIndexBuffer[screenX] = colorBit;
            bgAttrPriorityBuffer[screenX] = cgb && attrBgPriority && colorBit != 0;

            if (cgb)
                scanlineBuffer[screenX] = mmu.GetCGBBgColor(attrPalette, colorBit);
            else
                scanlineBuffer[screenX] = ConvertDMGColor((mmu.BGP >> (colorBit * 2)) & 0x03);
        }

        private void RenderWindowPixel(int screenX, int currentWindowLine, bool cgb)
        {
            int windowX = mmu.WX - 7;
            int windowCol = screenX - windowX;

            if (windowCol < 0)
                return;

            ushort tileMapBase = ((mmu.LCDC & (1 << 6)) != 0) ? (ushort)0x9C00 : (ushort)0x9800;

            int tileCol = windowCol >> 3;
            int tileRow = currentWindowLine >> 3;
            int tileIndex = tileRow * 32 + tileCol;
            int tileMapRel = (tileMapBase - 0x8000) + tileIndex;

            byte tileNumber = mmu.ReadVramDirect(tileMapRel, 0);
            byte attrs = cgb ? mmu.ReadVramDirect(tileMapRel, 1) : (byte)0;

            int attrPalette = attrs & 0x07;
            int attrTileBank = (attrs >> 3) & 0x01;
            bool attrXFlip = (attrs & 0x20) != 0;
            bool attrYFlip = (attrs & 0x40) != 0;
            bool attrBgPriority = (attrs & 0x80) != 0;

            int lineInTile = currentWindowLine & 7;
            if (attrYFlip) lineInTile = 7 - lineInTile;

            int tileDataRelBase = GetBgWindowTileDataRelBase(tileNumber, (mmu.LCDC & (1 << 4)) != 0);
            int bank = cgb ? attrTileBank : 0;

            byte tileLow = mmu.ReadVramDirect(tileDataRelBase + lineInTile * 2, bank);
            byte tileHigh = mmu.ReadVramDirect(tileDataRelBase + lineInTile * 2 + 1, bank);

            int pixelInTile = windowCol & 7;
            int bitIndex = attrXFlip ? pixelInTile : (7 - pixelInTile);
            int colorBit = (((tileHigh >> bitIndex) & 1) << 1) | ((tileLow >> bitIndex) & 1);

            bgColorIndexBuffer[screenX] = colorBit;
            bgAttrPriorityBuffer[screenX] = cgb && attrBgPriority && colorBit != 0;

            if (cgb)
                scanlineBuffer[screenX] = mmu.GetCGBBgColor(attrPalette, colorBit);
            else
                scanlineBuffer[screenX] = ConvertDMGColor((mmu.BGP >> (colorBit * 2)) & 0x03);
        }

        private int GetBgWindowTileDataRelBase(byte tileNumber, bool use8000Method)
        {
            if (use8000Method)
            {
                // Unsigned tiles, $8000-$8FFF
                return tileNumber * 16;
            }

            // Signed tiles, base is $9000 and tile index is signed.
            short signedIndex = (sbyte)tileNumber;
            int absoluteAddress = 0x9000 + (signedIndex * 16);
            return absoluteAddress - 0x8000;
        }

        private void RenderSprites()
        {
            if ((mmu.LCDC & (1 << 1)) == 0)
                return;

            int currentScanline = mmu.LY;
            bool cgb = mmu.IsCGBMode;

            bool masterBgPriority = (mmu.LCDC & 0x01) != 0;

            int renderedSprites = 0;
            int[] pixelOwner = new int[ScreenWidth];
            for (int i = 0; i < ScreenWidth; i++)
                pixelOwner[i] = -1;

            int spriteHeight = ((mmu.LCDC & (1 << 2)) != 0) ? 16 : 8;

            for (int i = 0; i < 40; i++)
            {
                if (renderedSprites >= 10)
                    break;

                int spriteBase = 0xFE00 + i * 4;
                int yPos = mmu.Read((ushort)spriteBase) - 16;
                int xPos = mmu.Read((ushort)(spriteBase + 1)) - 8;
                byte tileIndex = mmu.Read((ushort)(spriteBase + 2));
                byte attributes = mmu.Read((ushort)(spriteBase + 3));

                if (currentScanline < yPos || currentScanline >= yPos + spriteHeight)
                    continue;

                int lineInSprite = currentScanline - yPos;
                bool yFlip = (attributes & (1 << 6)) != 0;
                if (yFlip) lineInSprite = spriteHeight - 1 - lineInSprite;

                if (spriteHeight == 16)
                {
                    tileIndex &= 0xFE;
                    if (lineInSprite >= 8)
                    {
                        tileIndex++;
                        lineInSprite -= 8;
                    }
                }

                int tileBank = (cgb && (attributes & (1 << 3)) != 0) ? 1 : 0;

                int tileRelBase = tileIndex * 16 + lineInSprite * 2;
                byte tileLow = mmu.ReadVramDirect(tileRelBase, tileBank);
                byte tileHigh = mmu.ReadVramDirect(tileRelBase + 1, tileBank);

                bool xFlip = (attributes & (1 << 5)) != 0;

                int cgbPalette = attributes & 0x07;
                bool useOBP1Dmg = (attributes & (1 << 4)) != 0;
                byte dmgPalette = useOBP1Dmg ? mmu.OBP1 : mmu.OBP0;

                for (int px = 0; px < 8; px++)
                {
                    int bitIndex = xFlip ? px : (7 - px);
                    int colorBit = (((tileHigh >> bitIndex) & 1) << 1) | ((tileLow >> bitIndex) & 1);

                    if (colorBit == 0)
                        continue;

                    int screenX = xPos + px;
                    if (screenX < 0 || screenX >= ScreenWidth)
                        continue;

                    bool canDraw;
                    if (cgb)
                        canDraw = (pixelOwner[screenX] == -1);
                    else
                        canDraw = (pixelOwner[screenX] == -1 || xPos < pixelOwner[screenX]);

                    if (!canDraw)
                        continue;

                    if (cgb)
                    {
                        if (masterBgPriority)
                        {
                            bool objAttrBgPriority = (attributes & (1 << 7)) != 0;
                            if ((bgAttrPriorityBuffer[screenX] || objAttrBgPriority) &&
                                bgColorIndexBuffer[screenX] != 0)
                            {
                                continue;
                            }
                        }
                    }
                    else
                    {
                        bool bgOverObj = (attributes & (1 << 7)) != 0;
                        if (bgOverObj && bgColorIndexBuffer[screenX] != 0)
                            continue;
                    }

                    pixelOwner[screenX] = cgb ? i : xPos;

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

        private Color32 ConvertDMGColor(int paletteColor)
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
                mmu.STAT = (byte)(mmu.STAT & ~0x04);
            }
        }

        private void ClearToPalette0()
        {
            Color32 c = ConvertDMGColor(0);
            for (int i = 0; i < frameBuffer.Length; i++)
                frameBuffer[i] = c;
        }

        /*
        private void ClearToWhite()
        {
            //Color32 c = new Color32(255, 255, 255, 255);
            Color32 c = new Color32(0, 0, 0, 0); // Clear to black instead for both
            for (int i = 0; i < frameBuffer.Length; i++)
                frameBuffer[i] = c;
        }

        private void RenderBlankScanline(int ly)
        {
            //Color32 c = new Color32(255, 255, 255, 255);
            Color32 c = new Color32(0, 0, 0, 0); // Clear to black instead for both

            for (int x = 0; x < ScreenWidth; x++)
            {
                scanlineBuffer[x] = c;
                bgColorIndexBuffer[x] = 0;
                bgAttrPriorityBuffer[x] = false;
            }

            Array.Copy(scanlineBuffer, 0, frameBuffer, ly * ScreenWidth, ScreenWidth);
        }
        */

        private Color32 GetLcdOffBlankColor()
        {
            // GBC: blank to white.
            // DMG: blank to palette color 0 (your usual green-tinted GB look).
            return mmu.IsCGBMode
                ? new Color32(255, 255, 255, 255)
                : ConvertDMGColor(0);
        }

        private void ClearToLcdOffBlank()
        {
            Color32 c = GetLcdOffBlankColor();
            for (int i = 0; i < frameBuffer.Length; i++)
                frameBuffer[i] = c;
        }

        private void RenderBlankScanline(int ly)
        {
            Color32 c = GetLcdOffBlankColor();

            for (int x = 0; x < ScreenWidth; x++)
            {
                scanlineBuffer[x] = c;
                bgColorIndexBuffer[x] = 0;
                bgAttrPriorityBuffer[x] = false;
            }

            Array.Copy(scanlineBuffer, 0, frameBuffer, ly * ScreenWidth, ScreenWidth);
        }

        private static void WriteColor32Array(BinaryWriter writer, Color32[] data)
        {
            if (data == null)
            {
                writer.Write(-1);
                return;
            }

            writer.Write(data.Length);
            foreach (var c in data)
            {
                writer.Write(c.r);
                writer.Write(c.g);
                writer.Write(c.b);
                writer.Write(c.a);
            }
        }

        private static Color32[] ReadColor32Array(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0) return null;

            var data = new Color32[length];
            for (int i = 0; i < length; i++)
            {
                byte r = reader.ReadByte();
                byte g = reader.ReadByte();
                byte b = reader.ReadByte();
                byte a = reader.ReadByte();
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
            writer.Write(windowWyTriggeredThisFrame);
            writer.Write(lcdPreviouslyOff);
            writer.Write(lcdBlankFramePending);
            writer.Write(lcdOffFramePushed);
            WriteColor32Array(writer, frameBuffer);
            WriteColor32Array(writer, scanlineBuffer);
        }

        public void LoadState(BinaryReader reader)
        {
            mode = reader.ReadInt32();
            cycles = reader.ReadInt32();
            vblankTriggered = reader.ReadBoolean();
            windowLineCounter = reader.ReadInt32();
            windowWyTriggeredThisFrame = reader.ReadBoolean();
            lcdPreviouslyOff = reader.ReadBoolean();
            lcdBlankFramePending = reader.ReadBoolean();
            lcdOffFramePushed = reader.ReadBoolean();

            Color32[] fb = ReadColor32Array(reader);
            Color32[] sb = ReadColor32Array(reader);

            if (fb == null || fb.Length != frameBuffer.Length)
                throw new InvalidOperationException("Invalid frameBuffer in savestate.");
            if (sb == null || sb.Length != scanlineBuffer.Length)
                throw new InvalidOperationException("Invalid scanlineBuffer in savestate.");

            Array.Copy(fb, frameBuffer, frameBuffer.Length);
            Array.Copy(sb, scanlineBuffer, scanlineBuffer.Length);

            FrameDirty = true;
        }
    }
}