using System;
using System.Threading;
using Whb04b;

namespace Whb04bTest
{
    /// <summary>
    /// Hardware test without UCCNC: prints every report and event, and shows a
    /// simulated position on the pendant LCD that follows the jog wheel.
    /// Usage: Whb04bTest.exe [--raw]
    /// </summary>
    internal static class Program
    {
        private static readonly object Sync = new object();
        private static readonly double[] Pos = new double[6];
        private static Whb04bAxis _axis = Whb04bAxis.Off;
        private static Whb04bFeedKnob _feed = Whb04bFeedKnob.Unknown;
        private static bool _step = true;
        private static bool _machine;

        private static int Main(string[] args)
        {
            bool raw = Array.Exists(args, a => a == "--raw");

            Console.WriteLine("XHC WHB04B test tool. Ctrl+C to quit.");
            Console.WriteLine();
            Console.WriteLine("XHC devices (VID 10CE) found:");
            int n = 0;
            foreach (var pid in new ushort[] { Whb04bConstants.ProductIdWireless, Whb04bConstants.ProductIdWiredHb04 })
            {
                foreach (var p in Whb04bDevice.FindDevicePaths(pid))
                {
                    Console.WriteLine("  PID {0:X4}: {1}", pid, p);
                    n++;
                }
            }
            if (n == 0)
            {
                Console.WriteLine("  none. Is the USB dongle plugged in? Check Device Manager -> HID -> Hardware IDs.");
                return 1;
            }
            Console.WriteLine();

            using (var dev = new Whb04bDevice())
            {
                dev.Log = m => Console.WriteLine("[log] " + m);
                dev.LogRawReports = raw;
                dev.ChecksumMode = ChecksumMode.Resync;

                dev.AxisChanged += a => { lock (Sync) _axis = a; Console.WriteLine("Axis knob  -> " + a); };
                dev.FeedKnobChanged += f => { lock (Sync) _feed = f; Console.WriteLine("Feed knob  -> " + f); };
                dev.KeyDown += (k, fn) =>
                {
                    Console.WriteLine("Key down   -> " + (fn ? "Fn + " : "") + k);
                    lock (Sync)
                    {
                        if (k == Whb04bKey.Continuous) _step = false;
                        if (k == Whb04bKey.Step) _step = !_step;
                        if (k == Whb04bKey.Macro10 && fn) _machine = !_machine;
                        if (k == Whb04bKey.Reset) Array.Clear(Pos, 0, Pos.Length);
                    }
                };
                dev.KeyUp += k => Console.WriteLine("Key up     -> " + k);
                dev.WheelMoved += (a, d) =>
                {
                    Console.WriteLine("Wheel      -> " + d + " on " + a);
                    lock (Sync)
                    {
                        if (a == Whb04bAxis.Off) return;
                        double step = StepOf(_feed);
                        if (!_step || step == 0) step = 0.1;
                        Pos[(int)a - 1] += d * step;
                    }
                };
                dev.Disconnected += () => Console.WriteLine("!!! Disconnected");

                bool ok = dev.TryOpen(Whb04bConstants.ProductIdWireless)
                          || dev.TryOpen(Whb04bConstants.ProductIdWiredHb04);
                if (!ok)
                {
                    Console.WriteLine("Could not open the dongle.");
                    return 2;
                }
                if (dev.SharedWithOtherProgram)
                    Console.WriteLine("WARNING: another program (UCCNC plugin?) is already using the pendant. Close it first.");
                Console.WriteLine("Opened. Turn the pendant on, then press keys, turn the knobs and the wheel.");
                Console.WriteLine("Close this window before starting UCCNC, or both will write to the pendant LCD.");
                Console.WriteLine("The LCD should show a position that follows the wheel. RESET zeroes it, Fn+Macro-10 toggles X1 (machine) display.");
                Console.WriteLine();

                var quit = new ManualResetEvent(false);
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; quit.Set(); };

                int tick = 0;
                while (!quit.WaitOne(100, false))
                {
                    var d = new Whb04bDisplayData();
                    lock (Sync)
                    {
                        bool abc = _axis >= Whb04bAxis.A;
                        d.Axis1 = Pos[abc ? 3 : 0];
                        d.Axis2 = Pos[abc ? 4 : 1];
                        d.Axis3 = Pos[abc ? 5 : 2];
                        d.Mode = _step ? Whb04bDisplayMode.Step : Whb04bDisplayMode.Continuous;
                        d.MachineCoordinates = _machine;
                    }
                    d.FeedRate = 1000 + tick % 100;
                    d.SpindleSpeed = 12000;
                    dev.UpdateDisplay(d);
                    tick++;
                    if (!dev.IsOpen) break;
                }

                Console.WriteLine("Checksum errors seen: " + dev.ChecksumErrors);
            }
            return 0;
        }

        private static double StepOf(Whb04bFeedKnob f)
        {
            switch (f)
            {
                case Whb04bFeedKnob.Step0001Pct2: return 0.001;
                case Whb04bFeedKnob.Step001Pct5: return 0.01;
                case Whb04bFeedKnob.Step01Pct10: return 0.1;
                case Whb04bFeedKnob.Step1Pct30: return 1.0;
                default: return 0;
            }
        }
    }
}
