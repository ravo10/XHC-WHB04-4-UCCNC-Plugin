using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Plugins
{
    /// <summary>
    /// UCCNC's list of screen functions (code + description). Used to fill the key dropdowns
    /// and to find the jog-mode / jog-step buttons by name instead of guessing their codes.
    /// </summary>
    internal sealed class ButtonCatalog
    {
        public readonly List<KeyValuePair<int, string>> Items = new List<KeyValuePair<int, string>>();

        /// <summary>UCCNC button for "Continuous" jog, or -1.</summary>
        public int JogContinuous = -1;

        /// <summary>UCCNC button for "Step" jog, or -1.</summary>
        public int JogStep = -1;

        /// <summary>UCCNC buttons for jog step 0.001 / 0.01 / 0.1 / 1.0, or -1.</summary>
        public readonly int[] StepSize = { -1, -1, -1, -1 };

        /// <summary>UCCNC buttons "MPG cont." and "MPG multi", or -1.</summary>
        public int MpgContinuous = -1;
        public int MpgMulti = -1;
        public int MpgSingle = -1;

        public static ButtonCatalog Load(Plugininterface.Entry uc, Logger log)
        {
            var c = new ButtonCatalog();
            try
            {
                var list = uc.Getbuttonsdescriptions();
                if (list != null)
                    foreach (var f in list)
                        c.Items.Add(new KeyValuePair<int, string>(f.functioncode, (f.functiondescription ?? "").Trim()));
            }
            catch (Exception ex)
            {
                log.Write("Could not read UCCNC button list: " + ex.Message);
            }

            c.Items.Sort((a, b) => a.Key.CompareTo(b.Key));
            c.Resolve();
            c.Dump(log);
            return c;
        }

        public string Describe(int code)
        {
            foreach (var kv in Items)
                if (kv.Key == code) return kv.Value;
            return null;
        }

        private void Resolve()
        {
            foreach (var kv in Items)
            {
                string d = kv.Value.ToLowerInvariant();
                if (d.Contains("mpg"))
                {
                    // The HB04 plugin uses 226 = MPG continuous, 228 = MPG multi step.
                    // Accept a code only if its name agrees.
                    if (MpgContinuous < 0 && (Regex.IsMatch(d, @"\bcont(inuous|inous|\.|\b)") || (kv.Key == 226 && !d.Contains("single") && !d.Contains("multi"))))
                        MpgContinuous = kv.Key;
                    if (MpgMulti < 0 && (d.Contains("multi") || (kv.Key == 228 && !d.Contains("single") && !Regex.IsMatch(d, @"\bcont(inuous|inous|\.|\b)"))))
                        MpgMulti = kv.Key;
                    if (MpgSingle < 0 && (d.Contains("single") || (kv.Key == 227 && !d.Contains("multi"))))
                        MpgSingle = kv.Key;
                    continue; // MPG buttons are a different set from the jog buttons
                }

                bool jog = d.Contains("jog");
                bool cont = d.Contains("continuous") || d.Contains("continous");
                bool step = d.Contains("step");
                bool hasNumber = Regex.IsMatch(d, @"\d");

                if (jog && cont && !step && !hasNumber && JogContinuous < 0) JogContinuous = kv.Key;
                if (jog && step && !cont && !hasNumber && JogStep < 0) JogStep = kv.Key;

                if (step || jog)
                {
                    SetIf(d, @"(^|[^\d.])0?\.001\b", 0, kv.Key);
                    SetIf(d, @"(^|[^\d.])0?\.01\b", 1, kv.Key);
                    SetIf(d, @"(^|[^\d.])0?\.10?\b", 2, kv.Key);
                    SetIf(d, @"(^|[^\d.])1(\.0{1,3})?\b", 3, kv.Key);
                }
            }
        }

        private void SetIf(string description, string pattern, int index, int code)
        {
            if (StepSize[index] < 0 && Regex.IsMatch(description, pattern))
                StepSize[index] = code;
        }

        private void Dump(Logger log)
        {
            if (!log.DebugEnabled) return; // the button list file is only written with "Debug log" on

            log.Write(string.Format("UCCNC buttons: {0} found. Jog continuous={1}, jog step={2}, step sizes={3}/{4}/{5}/{6}, MPG cont={7}, MPG multi={8}, MPG single={9}",
                Items.Count, JogContinuous, JogStep, StepSize[0], StepSize[1], StepSize[2], StepSize[3], MpgContinuous, MpgMulti, MpgSingle));

            try
            {
                var sb = new StringBuilder();
                foreach (var kv in Items)
                    sb.AppendLine(kv.Key + "\t" + kv.Value);
                string dir = Path.GetDirectoryName(log.FilePath) ?? ".";
                File.WriteAllText(Path.Combine(dir, "XHC-WHB04B-4_uccnc_buttons.txt"), sb.ToString());
            }
            catch
            {
            }
        }
    }
}
