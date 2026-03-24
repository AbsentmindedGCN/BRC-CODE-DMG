using System;
using System.IO;

namespace BRCCodeDmg
{
    public sealed class Mbc3Rtc
    {
        // Live RTC registers
        private byte rtcS;   // 0x08
        private byte rtcM;   // 0x09
        private byte rtcH;   // 0x0A
        private byte rtcDL;  // 0x0B
        private byte rtcDH;  // 0x0C (bit0=day high, bit6=halt, bit7=carry)

        // Latched snapshot
        private byte latchedS;
        private byte latchedM;
        private byte latchedH;
        private byte latchedDL;
        private byte latchedDH;

        private bool isLatched;
        private byte selectedRegister = 0xFF;
        private byte lastLatchWrite;
        private long lastHostUnixTime;

        public Mbc3Rtc()
        {
            SeedFromHostClock();
        }

        public void SeedFromHostClock()
        {
            DateTimeOffset nowLocal = DateTimeOffset.Now;
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

            rtcS = (byte)nowLocal.Second;
            rtcM = (byte)nowLocal.Minute;
            rtcH = (byte)nowLocal.Hour;

            int days = (int)((nowUtc.ToUnixTimeSeconds() / 86400L) & 0x1FF);
            rtcDL = (byte)(days & 0xFF);
            rtcDH = (byte)((days >> 8) & 0x01);

            latchedS = rtcS;
            latchedM = rtcM;
            latchedH = rtcH;
            latchedDL = rtcDL;
            latchedDH = rtcDH;

            isLatched = false;
            lastLatchWrite = 0;
            lastHostUnixTime = GetHostUnixTime();
        }

        public void SelectRegister(byte value)
        {
            selectedRegister = value;
        }

        public void Latch(byte value)
        {
            // Latch on 00 -> 01 transition
            if (lastLatchWrite == 0x00 && value == 0x01)
            {
                SyncToHostClock();

                latchedS = rtcS;
                latchedM = rtcM;
                latchedH = rtcH;
                latchedDL = rtcDL;
                latchedDH = rtcDH;
                isLatched = true;
            }

            lastLatchWrite = value;
        }

        public byte ReadSelected()
        {
            SyncToHostClock();

            switch (selectedRegister)
            {
                case 0x08: return isLatched ? latchedS : rtcS;
                case 0x09: return isLatched ? latchedM : rtcM;
                case 0x0A: return isLatched ? latchedH : rtcH;
                case 0x0B: return isLatched ? latchedDL : rtcDL;
                case 0x0C: return isLatched ? latchedDH : rtcDH;
                default: return 0xFF;
            }
        }

        public void WriteSelected(byte value)
        {
            SyncToHostClock();

            switch (selectedRegister)
            {
                case 0x08:
                    rtcS = (byte)(value % 60);
                    break;
                case 0x09:
                    rtcM = (byte)(value % 60);
                    break;
                case 0x0A:
                    rtcH = (byte)(value % 24);
                    break;
                case 0x0B:
                    rtcDL = value;
                    break;
                case 0x0C:
                    {
                        bool wasHalted = IsHalted();
                        rtcDH = (byte)(value & 0xC1); // only bits 0, 6, 7 are valid
                        bool nowHalted = IsHalted();

                        if (wasHalted && !nowHalted)
                            lastHostUnixTime = GetHostUnixTime();

                        break;
                    }
            }
        }

        public void SyncToHostClock()
        {
            long now = GetHostUnixTime();

            if (lastHostUnixTime == 0)
            {
                lastHostUnixTime = now;
                return;
            }

            if (IsHalted())
            {
                lastHostUnixTime = now;
                return;
            }

            long delta = now - lastHostUnixTime;
            if (delta <= 0)
                return;

            AddSeconds(delta);
            lastHostUnixTime = now;
        }

        private void AddSeconds(long deltaSeconds)
        {
            long days = GetDayCounter();

            long totalSeconds =
                rtcS +
                (rtcM * 60L) +
                (rtcH * 3600L) +
                (days * 86400L);

            totalSeconds += deltaSeconds;

            long newDays = totalSeconds / 86400L;
            long daySeconds = totalSeconds % 86400L;

            if (newDays > 0x1FF)
            {
                rtcDH |= 0x80; // carry
                newDays &= 0x1FF;
            }

            rtcH = (byte)(daySeconds / 3600L);
            daySeconds %= 3600L;
            rtcM = (byte)(daySeconds / 60L);
            rtcS = (byte)(daySeconds % 60L);

            SetDayCounter((int)newDays);
        }

        private int GetDayCounter()
        {
            return rtcDL | ((rtcDH & 0x01) << 8);
        }

        private void SetDayCounter(int days)
        {
            days &= 0x1FF;
            rtcDL = (byte)(days & 0xFF);
            rtcDH = (byte)((rtcDH & 0xC0) | ((days >> 8) & 0x01));
        }

        private bool IsHalted()
        {
            return (rtcDH & 0x40) != 0;
        }

        private static long GetHostUnixTime()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        public void Save(BinaryWriter writer)
        {
            SyncToHostClock();

            writer.Write(rtcS);
            writer.Write(rtcM);
            writer.Write(rtcH);
            writer.Write(rtcDL);
            writer.Write(rtcDH);

            writer.Write(latchedS);
            writer.Write(latchedM);
            writer.Write(latchedH);
            writer.Write(latchedDL);
            writer.Write(latchedDH);

            writer.Write(isLatched);
            writer.Write(selectedRegister);
            writer.Write(lastLatchWrite);
            writer.Write(lastHostUnixTime);
        }

        public void Load(BinaryReader reader)
        {
            rtcS = reader.ReadByte();
            rtcM = reader.ReadByte();
            rtcH = reader.ReadByte();
            rtcDL = reader.ReadByte();
            rtcDH = reader.ReadByte();

            latchedS = reader.ReadByte();
            latchedM = reader.ReadByte();
            latchedH = reader.ReadByte();
            latchedDL = reader.ReadByte();
            latchedDH = reader.ReadByte();

            isLatched = reader.ReadBoolean();
            selectedRegister = reader.ReadByte();
            lastLatchWrite = reader.ReadByte();
            lastHostUnixTime = reader.ReadInt64();

            // Catch up immediately after load so RTC still advances across savestate reloads.
            SyncToHostClock();
        }
    }
}