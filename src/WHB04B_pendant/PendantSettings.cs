using System;
using System.Collections.Generic;
using System.Globalization;
using Whb04b;

namespace Plugins
{
    /// <summary>
    /// What a key does. The action text is one of:
    ///   ""              nothing
    ///   a number        UCCNC button code, sent with Callbutton (e.g. "130" = Cycle stop)
    ///   a keyword       STARTPAUSE, STOPALL, SAFEZ, MODE_CONT, MODE_STEP, MODE_SINGLE, MODE_TOGGLE, MODE_CYCLE, TOGGLE_COORDS, ZERO_AXIS, STOPJOG
    ///   anything else   G-code / M-code sent with Code (e.g. "M20000" or "G0 X0 Y0")
    /// </summary>
    internal sealed class KeyBinding
    {
        public Whb04bKey Key;
        public string Name;
        public string Action;
        public string FnAction;
    }

    /// <summary>Plugin settings, stored in the UCCNC profile under section [WHB04B].</summary>
    internal sealed class PendantSettings
    {
        public const string Section = "WHB04B";

        public bool DebugLog;
        /// <summary>Reverse the jog wheel per axis (X, Y, Z, A, B, C). X, Y and Z are reversed on the StepCraft M700.</summary>
        public bool[] ReverseAxis = { true, true, true, false, false, false };

        public static readonly string[] AxisNames = { "X", "Y", "Z", "A", "B", "C" };

        /// <summary>Run all UCCNC calls on UCCNC's main window thread (what the UCR201 plugin does).</summary>
        public bool RunOnMainThread;

        /// <summary>Press UCCNC's own Continuous/Step, step-size and axis buttons so its screen follows the pendant.</summary>
        public bool MirrorOnScreen = true;
        public ChecksumMode Checksum = ChecksumMode.Resync;

        /// <summary>Speed (percent of max) used for step moves.</summary>
        public double StepSpeedPercent = 100;

        /// <summary>At most this many wheel detents are turned into moves per UCCNC loop (protects against run-away queues).</summary>
        public int MaxDetentsPerLoop = 10;

        /// <summary>Continuous jog stops this many ms after the last wheel movement.</summary>
        public int ContinuousStopMs = 150;               // no longer used (JogOnSpeed removed)

        /// <summary>Continuous mode: distance per wheel click at 100 % (scaled by the knob %).</summary>
        public double ContinuousMmPerClick = 1.0;

        /// <summary>Continuous mode: most distance per second at 100 % (scaled by the knob %). Extra clicks are dropped.</summary>
        public double ContinuousMaxRate = 25.0;

        /// <summary>Scales the knob percentage in continuous mode (100 = use the knob value as printed).</summary>
        public double ContinuousScalePercent = 100;

        /// <summary>Start in step mode (true) or continuous mode (false).</summary>
        public bool StartInStepMode = true;

        public int FeedFieldNumber = 867;
        public int SpindleFieldNumber = 869;
        public int SafeZFieldNumber = 225;

        public List<KeyBinding> Keys = DefaultKeys();

        public static List<KeyBinding> DefaultKeys()
        {
            // Alone / with Fn, following the printed labels and the Mach3 driver.
            // UCCNC button codes come from the stock XHC-HB04 UCCNC plugin.
            return new List<KeyBinding>
            {
                B(Whb04bKey.Reset,              "Reset",             "144",        ""),
                B(Whb04bKey.Stop,               "Stop",              "STOPALL",    ""),
                B(Whb04bKey.StartPause,         "Start/Pause",       "STARTPAUSE", ""),
                B(Whb04bKey.Macro1FeedPlus,     "Macro-1 / Feed+",   "",           "132"),
                B(Whb04bKey.Macro2FeedMinus,    "Macro-2 / Feed-",   "",           "133"),
                B(Whb04bKey.Macro3SpindlePlus,  "Macro-3 / Spindle+","",           "134"),
                B(Whb04bKey.Macro4SpindleMinus, "Macro-4 / Spindle-","",           "135"),
                B(Whb04bKey.Macro5MachineHome,  "Macro-5 / M-Home",  "",           "113"),
                B(Whb04bKey.Macro6SafeZ,        "Macro-6 / Safe-Z",  "",           "SAFEZ"),
                B(Whb04bKey.Macro7WorkHome,     "Macro-7 / W-Home",  "",           "131"),
                B(Whb04bKey.Macro8SpindleOnOff, "Macro-8 / S-On/Off","",           "114"),
                B(Whb04bKey.Macro9ProbeZ,       "Macro-9 / Probe-Z", "",           "196"),
                B(Whb04bKey.Macro10,            "Macro-10",          "",           "TOGGLE_COORDS"),
                B(Whb04bKey.Continuous,         "MPG",               "MODE_CYCLE", ""),
                B(Whb04bKey.Step,               "Step/Continuous",   "MODE_TOGGLE", ""),
            };
        }

        private static KeyBinding B(Whb04bKey key, string name, string action, string fnAction)
        {
            return new KeyBinding { Key = key, Name = name, Action = action, FnAction = fnAction };
        }

        public KeyBinding Find(Whb04bKey key)
        {
            foreach (var k in Keys)
                if (k.Key == key) return k;
            return null;
        }

        public PendantSettings Clone()
        {
            var c = (PendantSettings)MemberwiseClone();
            c.ReverseAxis = (bool[])ReverseAxis.Clone();
            c.Keys = new List<KeyBinding>();
            foreach (var k in Keys)
                c.Keys.Add(new KeyBinding { Key = k.Key, Name = k.Name, Action = k.Action, FnAction = k.FnAction });
            return c;
        }

        public static PendantSettings Load(Plugininterface.Entry uc)
        {
            var s = new PendantSettings();
            if (uc == null) return s;

            try
            {
                s.DebugLog = ReadBool(uc, "DebugLog", s.DebugLog);
                for (int i = 0; i < s.ReverseAxis.Length; i++)
                    s.ReverseAxis[i] = ReadBool(uc, "Reverse" + AxisNames[i], s.ReverseAxis[i]);
                s.RunOnMainThread = ReadBool(uc, "RunOnMainThread", s.RunOnMainThread);
                s.MirrorOnScreen = ReadBool(uc, "MirrorOnScreen", s.MirrorOnScreen);
                s.Checksum = (ChecksumMode)ReadInt(uc, "ChecksumMode", (int)s.Checksum);
                s.StepSpeedPercent = ReadDouble(uc, "StepSpeedPercent", s.StepSpeedPercent);
                s.MaxDetentsPerLoop = ReadInt(uc, "MaxDetentsPerLoop", s.MaxDetentsPerLoop);
                s.ContinuousStopMs = ReadInt(uc, "ContinuousStopMs", s.ContinuousStopMs);
                s.ContinuousScalePercent = ReadDouble(uc, "ContinuousScalePercent", s.ContinuousScalePercent);
                s.ContinuousMmPerClick = ReadDouble(uc, "ContinuousMmPerClick", s.ContinuousMmPerClick);
                s.ContinuousMaxRate = ReadDouble(uc, "ContinuousMaxRate", s.ContinuousMaxRate);
                s.StartInStepMode = ReadBool(uc, "StartInStepMode", s.StartInStepMode);
                s.FeedFieldNumber = ReadInt(uc, "FeedField", s.FeedFieldNumber);
                s.SpindleFieldNumber = ReadInt(uc, "SpindleField", s.SpindleFieldNumber);
                s.SafeZFieldNumber = ReadInt(uc, "SafeZField", s.SafeZFieldNumber);

                foreach (var k in s.Keys)
                {
                    k.Action = Read(uc, "Key_" + k.Key, k.Action);
                    k.FnAction = Read(uc, "FnKey_" + k.Key, k.FnAction);
                }

                // Up to v0.3.4 the defaults had feed and spindle override swapped
                // (UCCNC: 132/133 = feed +/-, 134/135 = spindle +/-). Fix profiles saved
                // with those old defaults, but leave any other user choice alone.
                if (ReadInt(uc, "SettingsVersion", 1) < 2)
                {
                    var f1 = s.Find(Whb04bKey.Macro1FeedPlus);
                    var f2 = s.Find(Whb04bKey.Macro2FeedMinus);
                    var f3 = s.Find(Whb04bKey.Macro3SpindlePlus);
                    var f4 = s.Find(Whb04bKey.Macro4SpindleMinus);
                    if (f1.FnAction == "134" && f2.FnAction == "135" && f3.FnAction == "132" && f4.FnAction == "133")
                    {
                        f1.FnAction = "132";
                        f2.FnAction = "133";
                        f3.FnAction = "134";
                        f4.FnAction = "135";
                        Write(uc, "FnKey_" + f1.Key, f1.FnAction);
                        Write(uc, "FnKey_" + f2.Key, f2.FnAction);
                        Write(uc, "FnKey_" + f3.Key, f3.FnAction);
                        Write(uc, "FnKey_" + f4.Key, f4.FnAction);
                    }
                }

                // v0.3.6: the MPG key now cycles continuous -> multi step -> single step.
                if (ReadInt(uc, "SettingsVersion", 1) < 3)
                {
                    var mpg = s.Find(Whb04bKey.Continuous);
                    if (mpg.Action == "MODE_CONT")
                    {
                        mpg.Action = "MODE_CYCLE";
                        Write(uc, "Key_" + mpg.Key, mpg.Action);
                    }
                }

                // v0.3.7: Y is reversed by default too. Fix profiles saved with the old X/Z-only default.
                if (ReadInt(uc, "SettingsVersion", 1) < 4)
                {
                    if (s.ReverseAxis[0] && !s.ReverseAxis[1] && s.ReverseAxis[2])
                    {
                        s.ReverseAxis[1] = true;
                        Write(uc, "ReverseY", "1");
                    }
                }

                // v0.4.0: Stop key stops G-code AND pendant jogging.
                if (ReadInt(uc, "SettingsVersion", 1) < 5)
                {
                    var stop = s.Find(Whb04bKey.Stop);
                    if (stop.Action == "130")
                    {
                        stop.Action = "STOPALL";
                        Write(uc, "Key_" + stop.Key, stop.Action);
                    }
                    Write(uc, "SettingsVersion", "5");
                }
            }
            catch
            {
                // Keep defaults if the profile cannot be read.
            }

            s.Sanitize();
            return s;
        }

        public void Save(Plugininterface.Entry uc)
        {
            if (uc == null) return;
            Write(uc, "DebugLog", DebugLog ? "1" : "0");
            for (int i = 0; i < ReverseAxis.Length; i++)
                Write(uc, "Reverse" + AxisNames[i], ReverseAxis[i] ? "1" : "0");
            Write(uc, "RunOnMainThread", RunOnMainThread ? "1" : "0");
            Write(uc, "MirrorOnScreen", MirrorOnScreen ? "1" : "0");
            Write(uc, "ChecksumMode", ((int)Checksum).ToString(CultureInfo.InvariantCulture));
            Write(uc, "StepSpeedPercent", StepSpeedPercent.ToString(CultureInfo.InvariantCulture));
            Write(uc, "MaxDetentsPerLoop", MaxDetentsPerLoop.ToString(CultureInfo.InvariantCulture));
            Write(uc, "ContinuousStopMs", ContinuousStopMs.ToString(CultureInfo.InvariantCulture));
            Write(uc, "ContinuousScalePercent", ContinuousScalePercent.ToString(CultureInfo.InvariantCulture));
            Write(uc, "ContinuousMmPerClick", ContinuousMmPerClick.ToString(CultureInfo.InvariantCulture));
            Write(uc, "ContinuousMaxRate", ContinuousMaxRate.ToString(CultureInfo.InvariantCulture));
            Write(uc, "StartInStepMode", StartInStepMode ? "1" : "0");
            Write(uc, "FeedField", FeedFieldNumber.ToString(CultureInfo.InvariantCulture));
            Write(uc, "SpindleField", SpindleFieldNumber.ToString(CultureInfo.InvariantCulture));
            Write(uc, "SafeZField", SafeZFieldNumber.ToString(CultureInfo.InvariantCulture));
            Write(uc, "SettingsVersion", "5");
            foreach (var k in Keys)
            {
                Write(uc, "Key_" + k.Key, k.Action ?? "");
                Write(uc, "FnKey_" + k.Key, k.FnAction ?? "");
            }
        }

        public void Sanitize()
        {
            if (StepSpeedPercent <= 0 || StepSpeedPercent > 100) StepSpeedPercent = 100;
            if (MaxDetentsPerLoop < 1) MaxDetentsPerLoop = 1;
            if (MaxDetentsPerLoop > 100) MaxDetentsPerLoop = 100;
            if (ContinuousStopMs < 50) ContinuousStopMs = 50;
            if (ContinuousStopMs > 2000) ContinuousStopMs = 2000;
            if (ContinuousScalePercent <= 0 || ContinuousScalePercent > 100) ContinuousScalePercent = 100;
            if (ContinuousMmPerClick <= 0 || ContinuousMmPerClick > 10) ContinuousMmPerClick = 1.0;
            if (ContinuousMaxRate <= 0 || ContinuousMaxRate > 200) ContinuousMaxRate = 25.0;
            if (!Enum.IsDefined(typeof(ChecksumMode), Checksum)) Checksum = ChecksumMode.Resync;
        }

        private static string Read(Plugininterface.Entry uc, string key, string def)
        {
            var v = uc.Readkey(Section, key, def);
            return v ?? def;
        }

        private static void Write(Plugininterface.Entry uc, string key, string value)
        {
            uc.Writekey(Section, key, value);
        }

        private static bool ReadBool(Plugininterface.Entry uc, string key, bool def)
        {
            var v = Read(uc, key, def ? "1" : "0").Trim();
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static int ReadInt(Plugininterface.Entry uc, string key, int def)
        {
            int r;
            return int.TryParse(Read(uc, key, def.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : def;
        }

        private static double ReadDouble(Plugininterface.Entry uc, string key, double def)
        {
            double r;
            return double.TryParse(Read(uc, key, def.ToString(CultureInfo.InvariantCulture)), NumberStyles.Float, CultureInfo.InvariantCulture, out r) ? r : def;
        }
    }
}
