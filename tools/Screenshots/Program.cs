using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Plugininterface;
using Plugins;

namespace Whb04bScreenshots
{
    /// <summary>Stands in for UCCNC with fixed demo values.</summary>
    internal sealed class DemoEntry : Entry
    {
        private readonly List<Functionproperties> _buttons = new List<Functionproperties>();

        public DemoEntry(string buttonsFile)
        {
            foreach (var line in File.ReadAllLines(buttonsFile))
            {
                var parts = line.Split(new[] { '\t' }, 2);
                int code;
                if (parts.Length == 2 && int.TryParse(parts[0], out code))
                    _buttons.Add(new Functionproperties { functioncode = code, functiondescription = parts[1] });
            }
        }

        public override List<Functionproperties> Getbuttonsdescriptions() { return _buttons; }
        public override string Readkey(string section, string key, string defaultValue) { return defaultValue; }
        public override void Writekey(string section, string key, string value) { }
        public override bool GetLED(int number) { return false; }
        public override void Callbutton(int code) { }
        public override void Code(string code) { }
        public override string Getfield(bool isAS3, int number) { return number == 867 ? "100%" : number == 869 ? "100%" : "0"; }
        public override double Getfielddouble(bool isAS3, int number) { return 320; }
        public override double GetXpos() { return 25.0; }
        public override double GetYpos() { return 70.4; }
        public override double GetZpos() { return 11.9963; }
        public override double GetApos() { return 0; }
        public override double GetBpos() { return 0; }
        public override double GetCpos() { return 0; }
        public override double GetXmachpos() { return 325.0; }
        public override double GetYmachpos() { return 484.4; }
        public override double GetZmachpos() { return -8.0037; }
        public override double GetAmachpos() { return 0; }
        public override double GetBmachpos() { return 0; }
        public override double GetCmachpos() { return 0; }
        public override int AddLinearMoveRel(int axis, double distance, int count, double speedPercent, bool direction) { return 0; }
        public override int JogOnSpeed(int axis, bool direction, double speedPercent) { return 0; }
        public override int MPGJogOff(int axis) { return 0; }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: Whb04bScreenshots <uccnc_buttons.txt> <output folder>");
                return 1;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Directory.CreateDirectory(args[1]);

            var uc = new DemoEntry(args[0]);
            var log = new Logger();
            var controller = new PendantController(uc, log);
            controller.LoadCatalogSafe();

            Set(controller, "StatusText", "Connected");
            Set(controller, "LiveStatus",
                "Mode: MULTI STEP   Axis knob: X   Speed knob: Step01Pct10   Last wheel: +3 at 14:32:10\n" +
                "Last jog: AddLinearMoveRel(axis 0, 0.003125 x 96 motor steps, 100%, +) = 0 at 14:32:10");

            // The build machine's screen is small and Windows shrinks top-level windows to fit it,
            // so the settings window is rendered as a child of a host form at its full size.
            var form = new ConfigForm(uc, controller, log) { TopMost = false };
            form.TopLevel = false;
            form.FormBorderStyle = FormBorderStyle.None;
            form.Size = new Size(1260, 760); // ConfigForm's design size (a top-level form gets clamped to the screen)
            var host = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(400, 300), ShowInTaskbar = false };
            host.Controls.Add(form);
            form.Show();

            int result = 2;
            var timer = new Timer { Interval = 1500 };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                try
                {
                    // ConfigForm refreshes its status from the controller; keep the demo text.
                    Set(controller, "StatusText", "Connected");
                    form.Refresh();
                    Save(form, Path.Combine(args[1], "screenshot-settings.png"));
                    result = 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                }
                host.Close();
            };
            host.Shown += (s, e) => timer.Start();
            Application.Run(host);
            return result;
        }

        private static void Save(Control c, string path)
        {
            using (var bmp = new Bitmap(c.Width, c.Height))
            {
                c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height));
                bmp.Save(path, ImageFormat.Png);
            }
            Console.WriteLine("Saved " + path);
        }

        private static void Set(object target, string property, object value)
        {
            var p = target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            p.SetValue(target, value, null);
        }
    }
}
