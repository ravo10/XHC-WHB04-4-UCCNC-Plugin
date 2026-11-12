using System;

namespace Whb04b
{
    /// <summary>Key codes reported in bytes 2/3 of the input report. See docs/PROTOCOL.md.</summary>
    public enum Whb04bKey : byte
    {
        None = 0x00,
        Reset = 0x01,
        Stop = 0x02,
        StartPause = 0x03,
        Macro1FeedPlus = 0x04,
        Macro2FeedMinus = 0x05,
        Macro3SpindlePlus = 0x06,
        Macro4SpindleMinus = 0x07,
        Macro5MachineHome = 0x08,
        Macro6SafeZ = 0x09,
        Macro7WorkHome = 0x0A,
        Macro8SpindleOnOff = 0x0B,
        Fn = 0x0C,
        Macro9ProbeZ = 0x0D,
        Continuous = 0x0E,
        Step = 0x0F,
        Macro10 = 0x10,
    }

    /// <summary>Axis knob position (byte 5).</summary>
    public enum Whb04bAxis
    {
        Off = 0,
        X = 1,
        Y = 2,
        Z = 3,
        A = 4,
        B = 5,
        C = 6,
    }

    /// <summary>Feed/step knob position (byte 4 &amp; 0x3F).</summary>
    public enum Whb04bFeedKnob
    {
        Unknown = 0,
        Step0001Pct2 = 0x0D,
        Step001Pct5 = 0x0E,
        Step01Pct10 = 0x0F,
        Step1Pct30 = 0x10,
        Pct60 = 0x1A,
        Pct100 = 0x1B,
        Lead = 0x1C,
    }

    /// <summary>LCD mode indicator (flags bits 0-1).</summary>
    public enum Whb04bDisplayMode : byte
    {
        Continuous = 0,
        Step = 1,
        Mpg = 2,
        Percent = 3,
    }

    public static class Whb04bConstants
    {
        public const ushort VendorId = 0x10CE;
        public const ushort ProductIdWireless = 0xEB93;
        public const ushort ProductIdWiredHb04 = 0xEB70;
        public const byte InputReportId = 0x04;
        public const byte FeatureReportId = 0x06;
        public const int InputReportLength = 8;
        public const int DisplayBlockLength = 20;
        public const int FeaturePayloadLength = 7;
    }

    /// <summary>One decoded 8-byte input report.</summary>
    public sealed class Whb04bInputReport
    {
        public byte[] Raw { get; private set; }
        public byte RandomByte { get; private set; }
        public byte Key1 { get; private set; }
        public byte Key2 { get; private set; }

        /// <summary>The main key that is pressed (the second one when two are held).</summary>
        public Whb04bKey Key { get; private set; }

        /// <summary>True when Fn is held together with <see cref="Key"/>.</summary>
        public bool FnHeld { get; private set; }

        public byte FeedKnobRaw { get; private set; }
        public Whb04bFeedKnob FeedKnob { get; private set; }
        public byte AxisKnobRaw { get; private set; }
        public Whb04bAxis Axis { get; private set; }
        public int WheelDelta { get; private set; }
        public byte Checksum { get; private set; }

        public static bool TryParse(byte[] buffer, int offset, out Whb04bInputReport report)
        {
            report = null;
            if (buffer == null || buffer.Length - offset < Whb04bConstants.InputReportLength)
                return false;
            if (buffer[offset] != Whb04bConstants.InputReportId)
                return false;

            var raw = new byte[Whb04bConstants.InputReportLength];
            Array.Copy(buffer, offset, raw, 0, raw.Length);

            var r = new Whb04bInputReport
            {
                Raw = raw,
                RandomByte = raw[1],
                Key1 = raw[2],
                Key2 = raw[3],
                FeedKnobRaw = (byte)(raw[4] & 0x3F),
                AxisKnobRaw = raw[5],
                WheelDelta = unchecked((sbyte)raw[6]),
                Checksum = raw[7],
            };

            if (r.Key2 == 0)
            {
                r.Key = (Whb04bKey)r.Key1;
                r.FnHeld = false;
            }
            else
            {
                r.Key = (Whb04bKey)r.Key2;
                r.FnHeld = r.Key1 == (byte)Whb04bKey.Fn;
            }

            r.FeedKnob = DecodeFeedKnob(r.FeedKnobRaw);
            r.Axis = DecodeAxis(r.AxisKnobRaw);
            report = r;
            return true;
        }

        public static Whb04bAxis DecodeAxis(byte raw)
        {
            if (raw >= 0x11 && raw <= 0x16)
                return (Whb04bAxis)(raw - 0x10);
            return Whb04bAxis.Off;
        }

        public static Whb04bFeedKnob DecodeFeedKnob(byte raw)
        {
            switch (raw)
            {
                case 0x0D:
                case 0x0E:
                case 0x0F:
                case 0x10:
                case 0x1A:
                case 0x1B:
                case 0x1C:
                    return (Whb04bFeedKnob)raw;
                default:
                    return Whb04bFeedKnob.Unknown;
            }
        }

        /// <summary>Checks the checksum in byte 7 against the seed last sent to the pendant.</summary>
        public bool IsChecksumValid(byte seed)
        {
            return ComputeChecksum(RandomByte, Key1, seed) == Checksum;
        }

        public static byte ComputeChecksum(byte randomByte, byte key1, byte seed)
        {
            byte c = (byte)~randomByte;
            return (byte)(((c + key1) ^ (seed | c)) & 0xFF);
        }

        public override string ToString()
        {
            return string.Format(
                "raw={0} key={1}{2} axis={3} feed={4} (0x{5:X2}) wheel={6}",
                BitConverter.ToString(Raw),
                FnHeld ? "Fn+" : "",
                Key,
                Axis,
                FeedKnob,
                FeedKnobRaw,
                WheelDelta);
        }
    }

    /// <summary>Contents of the pendant LCD, encoded as the 20-byte display block.</summary>
    public sealed class Whb04bDisplayData
    {
        public Whb04bDisplayMode Mode { get; set; }
        public bool MachineCoordinates { get; set; }
        public bool ResetActive { get; set; }
        public double Axis1 { get; set; }
        public double Axis2 { get; set; }
        public double Axis3 { get; set; }
        public int FeedRate { get; set; }
        public int SpindleSpeed { get; set; }

        public byte[] Encode(byte seed)
        {
            var b = new byte[Whb04bConstants.DisplayBlockLength];
            b[0] = 0xFE;
            b[1] = 0xFD;
            b[2] = seed;

            byte flags = (byte)((byte)Mode & 0x03);
            if (MachineCoordinates) flags |= 0x40;
            if (ResetActive) flags |= 0x80;
            b[3] = flags;

            WriteCoordinate(b, 4, Axis1);
            WriteCoordinate(b, 8, Axis2);
            WriteCoordinate(b, 12, Axis3);
            WriteUInt16(b, 16, Clamp16(FeedRate));
            WriteUInt16(b, 18, Clamp16(SpindleSpeed));
            return b;
        }

        private static void WriteCoordinate(byte[] b, int offset, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                value = 0;

            bool negative = value < 0;
            // Round to 4 decimals first so 1.99999 shows as 2.0000 and not 1.9999.
            long scaled = (long)Math.Round(Math.Abs(value) * 10000.0, MidpointRounding.AwayFromZero);
            long integer = scaled / 10000;
            int fraction = (int)(scaled % 10000);
            if (integer > 0xFFFF) { integer = 0xFFFF; fraction = 9999; }
            if (negative && scaled != 0) fraction |= 0x8000;

            WriteUInt16(b, offset, (ushort)integer);
            WriteUInt16(b, offset + 2, (ushort)fraction);
        }

        private static ushort Clamp16(int v)
        {
            if (v < 0) return 0;
            if (v > 0xFFFF) return 0xFFFF;
            return (ushort)v;
        }

        private static void WriteUInt16(byte[] b, int offset, ushort v)
        {
            b[offset] = (byte)(v & 0xFF);
            b[offset + 1] = (byte)(v >> 8);
        }

        /// <summary>Splits a display block into 8-byte feature reports (ID 0x06 + 7 payload bytes).</summary>
        public static byte[][] ToFeatureReports(byte[] block)
        {
            int count = (block.Length + Whb04bConstants.FeaturePayloadLength - 1) / Whb04bConstants.FeaturePayloadLength;
            var reports = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                var r = new byte[1 + Whb04bConstants.FeaturePayloadLength];
                r[0] = Whb04bConstants.FeatureReportId;
                int src = i * Whb04bConstants.FeaturePayloadLength;
                int n = Math.Min(Whb04bConstants.FeaturePayloadLength, block.Length - src);
                Array.Copy(block, src, r, 1, n);
                reports[i] = r;
            }
            return reports;
        }

        /// <summary>The single feature report the Mach3 driver sends to resynchronise the seed.</summary>
        public static byte[] SeedPacket(byte seed)
        {
            return new byte[] { Whb04bConstants.FeatureReportId, 0xFE, 0xFD, seed, 0x20, 0x20, 0x20, 0x20 };
        }
    }
}
