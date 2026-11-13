using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Whb04b;

namespace Plugins
{
    /// <summary>One choice in a key's dropdown.</summary>
    internal sealed class ActionChoice
    {
        public const string KindNone = "none", KindMacro = "macro", KindCode = "code", KindKeyword = "keyword", KindButton = "button";

        public string Kind;
        public string Value; // keyword name or button code
        public string Text;

        public bool NeedsText { get { return Kind == KindMacro || Kind == KindCode; } }
        public override string ToString() { return Text; }
    }

    /// <summary>Dropdown + text box that edits one key action string.</summary>
    internal sealed class ActionEditor
    {
        public readonly ComboBox Combo = new ComboBox();
        public readonly TextBox Text = new TextBox();

        public ActionEditor(List<ActionChoice> choices)
        {
            Combo.DropDownStyle = ComboBoxStyle.DropDownList;
            Combo.Width = 190;
            Combo.MaxDropDownItems = 25;
            Combo.Items.AddRange(choices.ToArray());
            Text.Width = 56;
            Combo.SelectedIndexChanged += (s, e) => UpdateTextBox();
        }

        private ActionChoice Selected { get { return Combo.SelectedItem as ActionChoice; } }

        private void UpdateTextBox()
        {
            var c = Selected;
            Text.Enabled = c != null && c.NeedsText;
            if (!Text.Enabled) Text.Text = "";
        }

        public void SetAction(string action)
        {
            action = (action ?? "").Trim();
            string text = "";
            Func<ActionChoice, bool> match;

            int code;
            if (action.Length == 0)
                match = c => c.Kind == ActionChoice.KindNone;
            else if (int.TryParse(action, NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
            {
                string v = code.ToString(CultureInfo.InvariantCulture);
                match = c => c.Kind == ActionChoice.KindButton && c.Value == v;
                if (Find(match) < 0)
                    Combo.Items.Add(new ActionChoice { Kind = ActionChoice.KindButton, Value = v, Text = v + "  (button)" });
            }
            else if (Find(c => c.Kind == ActionChoice.KindKeyword && c.Value.Equals(action, StringComparison.OrdinalIgnoreCase)) >= 0)
                match = c => c.Kind == ActionChoice.KindKeyword && c.Value.Equals(action, StringComparison.OrdinalIgnoreCase);
            else if (Regex.IsMatch(action, @"^[Mm]\d+$"))
            {
                match = c => c.Kind == ActionChoice.KindMacro;
                text = action.Substring(1);
            }
            else
            {
                match = c => c.Kind == ActionChoice.KindCode;
                text = action;
            }

            int i = Find(match);
            Combo.SelectedIndex = i >= 0 ? i : 0;
            UpdateTextBox();
            Text.Text = text;
        }

        public string GetAction()
        {
            var c = Selected;
            if (c == null) return "";
            string t = (Text.Text ?? "").Trim();
            switch (c.Kind)
            {
                case ActionChoice.KindMacro:
                    if (t.StartsWith("M", StringComparison.OrdinalIgnoreCase)) t = t.Substring(1);
                    return t.Length == 0 ? "" : "M" + t;
                case ActionChoice.KindCode:
                    return t;
                case ActionChoice.KindKeyword:
                case ActionChoice.KindButton:
                    return c.Value;
                default:
                    return "";
            }
        }

        private int Find(Func<ActionChoice, bool> match)
        {
            for (int i = 0; i < Combo.Items.Count; i++)
                if (match((ActionChoice)Combo.Items[i])) return i;
            return -1;
        }
    }

    /// <summary>Settings window, built in code (no designer file).</summary>
    internal sealed class ConfigForm : Form
    {
        private readonly Plugininterface.Entry _uc;
        private readonly PendantController _controller;
        private readonly Logger _log;
        private readonly PendantSettings _edit;
        private readonly List<ActionChoice> _choices;

        private readonly Label _status = new Label();
        private readonly TableLayoutPanel _keyTable = new TableLayoutPanel();
        private readonly List<KeyValuePair<KeyBinding, ActionEditor[]>> _editors = new List<KeyValuePair<KeyBinding, ActionEditor[]>>();
        private readonly CheckBox _debug = new CheckBox();
        private readonly CheckBox[] _reverse = new CheckBox[4];
        private readonly CheckBox _mainThread = new CheckBox();
        private readonly CheckBox _mirror = new CheckBox();
        private readonly CheckBox _mirrorMpg = new CheckBox();
        private readonly CheckBox _smooth = new CheckBox();
        private readonly NumericUpDown _stopMs = new NumericUpDown();
        private readonly CheckBox _startStep = new CheckBox();
        private readonly CheckBox _onTop = new CheckBox();
        private readonly ComboBox _checksum = new ComboBox();
        private readonly NumericUpDown _stepSpeed = new NumericUpDown();
        private readonly NumericUpDown _maxDetents = new NumericUpDown();
        private readonly NumericUpDown _contStop = new NumericUpDown();
        private readonly NumericUpDown _contScale = new NumericUpDown();
        private readonly NumericUpDown _feedField = new NumericUpDown();
        private readonly NumericUpDown _spindleField = new NumericUpDown();
        private readonly NumericUpDown _safeZField = new NumericUpDown();
        private readonly Timer _timer = new Timer();

        public ConfigForm(Plugininterface.Entry uc, PendantController controller, Logger log)
        {
            _uc = uc;
            _controller = controller;
            _log = log;
            _edit = controller.Settings.Clone();
            _choices = BuildChoices(controller.Catalog ?? controller.LoadCatalogSafe());

            Text = "XHC WHB04B-4 pendant  v" + UCCNCplugin.Version;
            Width = 1180;
            Height = 760;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            TopMost = _edit.WindowOnTop;
            ShowInTaskbar = true;

            BuildLayout();
            LoadValues();

            _timer.Interval = 300;
            _timer.Tick += (s, e) => RefreshStatus();
            _timer.Start();
            RefreshStatus();
        }

        private static List<ActionChoice> BuildChoices(ButtonCatalog catalog)
        {
            var list = new List<ActionChoice>
            {
                new ActionChoice { Kind = ActionChoice.KindNone, Text = "(nothing)" },
                new ActionChoice { Kind = ActionChoice.KindMacro, Text = "Run macro  M…  (type the number)" },
                new ActionChoice { Kind = ActionChoice.KindCode, Text = "Run G-code / M-code  (type it)" },
                K("STARTPAUSE", "★ Start / pause (cycle start, feed hold)"),
                K("STOPALL", "★ Stop: G-code stop + stop pendant jog"),
                K("SAFEZ", "★ Go to safe Z height"),
                K("MODE_CONT", "★ Pendant: continuous jog mode"),
                K("MODE_STEP", "★ Pendant: multi step jog mode"),
                K("MODE_SINGLE", "★ Pendant: single step jog mode"),
                K("MODE_CYCLE", "★ Pendant: cycle continuous / multi / single"),
                K("MODE_TOGGLE", "★ Pendant: toggle step / continuous"),
                K("TOGGLE_COORDS", "★ Pendant LCD: work / machine coordinates"),
                K("ZERO_AXIS", "★ Zero the axis selected on the knob"),
                K("STOPJOG", "★ Stop pendant jog"),
            };
            foreach (var kv in catalog.Items)
            {
                string code = kv.Key.ToString(CultureInfo.InvariantCulture);
                list.Add(new ActionChoice { Kind = ActionChoice.KindButton, Value = code, Text = kv.Value + "   [" + code + "]" });
            }
            return list;
        }

        private static ActionChoice K(string keyword, string text)
        {
            return new ActionChoice { Kind = ActionChoice.KindKeyword, Value = keyword, Text = text };
        }

        // Light theme matching the orange UCCNC StepCraft screen.
        private static readonly Color Accent = Color.FromArgb(232, 89, 12);
        private static readonly Color HeaderBack = Color.FromArgb(43, 47, 54);
        private static readonly Color PageBack = Color.FromArgb(244, 245, 247);
        private static readonly Color RowAlt = Color.FromArgb(248, 249, 251);
        private static readonly Color HeadRow = Color.FromArgb(253, 236, 225);
        private static readonly Color Ok = Color.FromArgb(47, 133, 90);
        private static readonly Color Bad = Color.FromArgb(197, 48, 48);

        private readonly Label _statusTitle = new Label();

        /// <summary>
        /// When true the window opens without taking the keyboard focus, so UCCNC keeps the
        /// arrow keys for jogging (used when UCCNC starts the plugin).
        /// </summary>
        public bool NoActivate { get; set; }

        protected override bool ShowWithoutActivation
        {
            get { return NoActivate; }
        }

        /// <summary>The header picture (embedded icon.png). Icon.ToBitmap cannot decode PNG-compressed icon frames on .NET 4.</summary>
        private static Image LoadHeaderImage()
        {
            try
            {
                var stream = typeof(ConfigForm).Assembly.GetManifestResourceStream("WHB04B.header.png");
                return stream == null ? null : Image.FromStream(stream);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The plugin icon (embedded app.ico), or null when the DLL was built without one.</summary>
        internal static Icon LoadAppIcon()
        {
            try
            {
                var stream = typeof(ConfigForm).Assembly.GetManifestResourceStream("WHB04B.app.ico");
                return stream == null ? null : new Icon(stream);
            }
            catch
            {
                return null;
            }
        }

        private static Panel Card(string title, Control content)
        {
            var card = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(10, 6, 10, 10), Margin = new Padding(4) };
            content.Dock = DockStyle.Fill;
            card.Controls.Add(content);
            card.Controls.Add(new Label
            {
                Text = title,
                Dock = DockStyle.Top,
                Height = 26,
                Font = new Font("Segoe UI Semibold", 10.5f),
                ForeColor = Accent,
            });
            return card;
        }

        private static Button FlatButton(string text, bool primary)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                MinimumSize = new Size(96, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Accent : Color.White,
                ForeColor = primary ? Color.White : Color.FromArgb(40, 40, 40),
                Margin = new Padding(6, 4, 0, 4),
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(200, 200, 205);
            return b;
        }

        private void BuildLayout()
        {
            BackColor = PageBack;
            var appIcon = LoadAppIcon();
            if (appIcon != null) Icon = appIcon;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(0), Margin = new Padding(0) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(root);

            // Header bar: icon + name + version
            var header = new Panel { Dock = DockStyle.Fill, BackColor = HeaderBack, Margin = new Padding(0) };
            int textLeft = 16;
            if (appIcon != null)
            {
                var pic = new PictureBox
                {
                    Image = LoadHeaderImage() ?? new Icon(appIcon, 48, 48).ToBitmap(),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Size = new Size(40, 40),
                    Location = new Point(12, 9),
                };
                header.Controls.Add(pic);
                textLeft = 62;
            }
            header.Controls.Add(new Label
            {
                Text = "XHC WHB04B-4 wireless pendant",
                Font = new Font("Segoe UI Semibold", 14f),
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(textLeft, 6),
            });
            header.Controls.Add(new Label
            {
                Text = "UCCNC plugin  ·  v" + UCCNCplugin.Version,
                ForeColor = Color.FromArgb(180, 186, 196),
                AutoSize = true,
                Location = new Point(textLeft + 2, 34),
            });
            var stripe = new Panel { Dock = DockStyle.Bottom, Height = 3, BackColor = Accent };
            header.Controls.Add(stripe);
            root.Controls.Add(header, 0, 0);
            root.SetColumnSpan(header, 2);

            // Status strip
            var statusPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14, 6, 10, 4), Margin = new Padding(8, 8, 8, 0) };
            _statusTitle.Dock = DockStyle.Top;
            _statusTitle.Height = 22;
            _statusTitle.Font = new Font("Segoe UI Semibold", 10f);
            _status.Dock = DockStyle.Fill;
            _status.ForeColor = Color.FromArgb(70, 70, 75);
            statusPanel.Controls.Add(_status);
            statusPanel.Controls.Add(_statusTitle);
            root.Controls.Add(statusPanel, 0, 1);
            root.SetColumnSpan(statusPanel, 2);

            // Key table
            _keyTable.AutoScroll = true;
            _keyTable.BackColor = Color.White;
            _keyTable.ColumnCount = 5;
            _keyTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
            for (int i = 0; i < 4; i++) _keyTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _keyTable.CellPaint += (s, e) =>
            {
                if (e.Row == 0)
                    using (var b = new SolidBrush(HeadRow)) e.Graphics.FillRectangle(b, e.CellBounds);
                else if (e.Row % 2 == 0)
                    using (var b = new SolidBrush(RowAlt)) e.Graphics.FillRectangle(b, e.CellBounds);
            };
            AddHeader("Key", 0);
            AddHeader("Pressed alone", 1);
            AddHeader("M no. / code", 2);
            AddHeader("With Fn held", 3);
            AddHeader("M no. / code", 4);
            var keysCard = Card("Pendant keys", _keyTable);
            keysCard.Margin = new Padding(8, 8, 4, 0);
            root.Controls.Add(keysCard, 0, 2);

            // Options
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, BackColor = Color.White };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));

            _onTop.Text = "Window always on top";
            _onTop.Checked = _edit.WindowOnTop;
            _onTop.CheckedChanged += (s, e) => TopMost = _onTop.Checked;
            _mirror.Text = "Mirror pendant mode on UCCNC screen";
            _debug.Text = "Debug log";
            _mirrorMpg.Text = "Also light the MPG cont./single/multi buttons";
            _smooth.Text = "Smooth continuous jog (stop is verified)";
            var revPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            revPanel.Controls.Add(new Label { Text = "Reverse wheel:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
            for (int i = 0; i < _reverse.Length; i++)
            {
                _reverse[i] = new CheckBox { Text = PendantSettings.AxisNames[i], AutoSize = true };
                revPanel.Controls.Add(_reverse[i]);
            }
            _startStep.Text = "Start in step mode";
            _mainThread.Text = "Run jog/keys on UCCNC's main thread";
            _checksum.DropDownStyle = ComboBoxStyle.DropDownList;
            _checksum.Items.AddRange(new object[] { "Ignore", "Resync (default)", "Strict (like Mach3)" });

            Num(_stepSpeed, 1, 100);
            Num(_maxDetents, 1, 100);
            Num(_contStop, 0.01m, 10m, 2);
            Num(_contScale, 1m, 200m, 0);
            Num(_feedField, 0, 5000);
            Num(_spindleField, 0, 5000);
            Num(_safeZField, 0, 5000);

            int r = 0;
            AddFull(t, _onTop, ref r);
            AddFull(t, _mirror, ref r);
            AddFull(t, _mirrorMpg, ref r);
            AddFull(t, _smooth, ref r);
            AddFull(t, revPanel, ref r);
            AddFull(t, _startStep, ref r);
            AddFull(t, _debug, ref r);
            AddFull(t, _mainThread, ref r);
            AddRow(t, "Step move speed %", _stepSpeed, ref r);
            AddRow(t, "Max wheel clicks per loop", _maxDetents, ref r);
            Num(_stopMs, 100, 1000);
            AddRow(t, "Smooth jog: stop after ms", _stopMs, ref r);
            AddRow(t, "Continuous: mm per click at 100%", _contStop, ref r);
            AddRow(t, "Continuous: max mm/s at 100%", _contScale, ref r);
            AddRow(t, "LCD feed field #", _feedField, ref r);
            AddRow(t, "LCD spindle field #", _spindleField, ref r);
            AddRow(t, "Safe-Z height field #", _safeZField, ref r);
            _checksum.Width = 140;
            AddRow(t, "Checksum", _checksum, ref r);

            // Rows size to their content; an empty last row takes the leftover height
            // (otherwise the last row stretches and its label drifts down).
            t.RowCount = r + 1;
            for (int i = 0; i < r; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var optsCard = Card("Options", t);
            optsCard.Margin = new Padding(4, 8, 8, 0);
            root.Controls.Add(optsCard, 1, 2);

            // Bottom buttons
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 4, 8, 0) };
            var save = FlatButton("Save", true);
            var close = FlatButton("Close", false);
            var reconnect = FlatButton("Reconnect", false);
            var openLog = FlatButton("Open log", false);
            var defaults = FlatButton("Default keys", false);
            save.Click += (s, e) => SaveValues();
            close.Click += (s, e) => Close();
            reconnect.Click += (s, e) => _controller.RequestReconnect();
            openLog.Click += (s, e) => { try { Process.Start("notepad.exe", "\"" + _log.FilePath + "\""); } catch { } };
            defaults.Click += (s, e) =>
            {
                var def = PendantSettings.DefaultKeys();
                foreach (var row in _editors)
                    foreach (var d in def)
                        if (d.Key == row.Key.Key)
                        {
                            row.Value[0].SetAction(d.Action);
                            row.Value[1].SetAction(d.FnAction);
                        }
            };
            buttons.Controls.AddRange(new Control[] { close, save, reconnect, openLog, defaults });
            root.Controls.Add(buttons, 0, 3);
            root.SetColumnSpan(buttons, 2);
        }

        private void AddHeader(string text, int col)
        {
            _keyTable.Controls.Add(new Label { Text = text, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 6) }, col, 0);
        }

        private static void Num(NumericUpDown n, int min, int max)
        {
            Num(n, min, max, 0);
        }

        private static void Num(NumericUpDown n, decimal min, decimal max, int decimals)
        {
            n.Minimum = min;
            n.Maximum = max;
            n.DecimalPlaces = decimals;
            n.Increment = decimals > 0 ? 0.1m : 1m;
            n.Width = 90;
        }

        private static void AddRow(TableLayoutPanel t, string label, Control c, ref int row)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) }, 0, row);
            t.Controls.Add(c, 1, row);
            row++;
        }

        private static void AddFull(TableLayoutPanel t, Control c, ref int row)
        {
            c.AutoSize = true;
            t.Controls.Add(c, 0, row);
            t.SetColumnSpan(c, 2);
            row++;
        }

        private void LoadValues()
        {
            _mirror.Checked = _edit.MirrorOnScreen;
            _debug.Checked = _edit.DebugLog;
            for (int i = 0; i < _reverse.Length; i++)
                _reverse[i].Checked = _edit.ReverseAxis[i];
            _startStep.Checked = _edit.StartInStepMode;
            _mainThread.Checked = _edit.RunOnMainThread;
            _mirrorMpg.Checked = _edit.MirrorMpgButtons;
            _smooth.Checked = _edit.SmoothContinuous;
            _stopMs.Value = Clamp(_stopMs, _edit.ContinuousStopMs);
            _checksum.SelectedIndex = (int)_edit.Checksum;
            _stepSpeed.Value = Clamp(_stepSpeed, _edit.StepSpeedPercent);
            _maxDetents.Value = Clamp(_maxDetents, _edit.MaxDetentsPerLoop);
            _contStop.Value = Clamp(_contStop, _edit.ContinuousMmPerClick);
            _contScale.Value = Clamp(_contScale, _edit.ContinuousMaxRate);
            _feedField.Value = Clamp(_feedField, _edit.FeedFieldNumber);
            _spindleField.Value = Clamp(_spindleField, _edit.SpindleFieldNumber);
            _safeZField.Value = Clamp(_safeZField, _edit.SafeZFieldNumber);

            _keyTable.SuspendLayout();
            int row = 1;
            foreach (var k in _edit.Keys)
            {
                var alone = new ActionEditor(_choices);
                var fn = new ActionEditor(_choices);
                alone.SetAction(k.Action);
                fn.SetAction(k.FnAction);
                _keyTable.Controls.Add(new Label { Text = k.Name, AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 0, row);
                _keyTable.Controls.Add(alone.Combo, 1, row);
                _keyTable.Controls.Add(alone.Text, 2, row);
                _keyTable.Controls.Add(fn.Combo, 3, row);
                _keyTable.Controls.Add(fn.Text, 4, row);
                _editors.Add(new KeyValuePair<KeyBinding, ActionEditor[]>(k, new[] { alone, fn }));
                row++;
            }
            _keyTable.ResumeLayout();
        }

        private static decimal Clamp(NumericUpDown n, double v)
        {
            var d = (decimal)v;
            if (d < n.Minimum) return n.Minimum;
            if (d > n.Maximum) return n.Maximum;
            return d;
        }

        private void SaveValues()
        {
            foreach (var row in _editors)
            {
                row.Key.Action = row.Value[0].GetAction();
                row.Key.FnAction = row.Value[1].GetAction();
            }

            _edit.MirrorOnScreen = _mirror.Checked;
            _edit.DebugLog = _debug.Checked;
            for (int i = 0; i < _reverse.Length; i++)
                _edit.ReverseAxis[i] = _reverse[i].Checked;
            _edit.StartInStepMode = _startStep.Checked;
            _edit.RunOnMainThread = _mainThread.Checked;
            _edit.MirrorMpgButtons = _mirrorMpg.Checked;
            _edit.SmoothContinuous = _smooth.Checked;
            _edit.ContinuousStopMs = (int)_stopMs.Value;
            _edit.WindowOnTop = _onTop.Checked;
            _edit.Checksum = (ChecksumMode)Math.Max(0, _checksum.SelectedIndex);
            _edit.StepSpeedPercent = (double)_stepSpeed.Value;
            _edit.MaxDetentsPerLoop = (int)_maxDetents.Value;
            _edit.ContinuousMmPerClick = (double)_contStop.Value;
            _edit.ContinuousMaxRate = (double)_contScale.Value;
            _edit.FeedFieldNumber = (int)_feedField.Value;
            _edit.SpindleFieldNumber = (int)_spindleField.Value;
            _edit.SafeZFieldNumber = (int)_safeZField.Value;

            try
            {
                var copy = _edit.Clone();
                copy.Save(_uc);
                _controller.ApplySettings(copy);
                _log.Debug("Settings saved");
                MessageBox.Show(this, "Settings saved.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save settings: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RefreshStatus()
        {
            var dev = _controller.Device;
            bool shared = dev.IsOpen && dev.SharedWithOtherProgram;
            _statusTitle.Text = "\u25CF  " + (_controller.StatusText ?? "Waiting for UCCNC loop…");
            bool connected = (_controller.StatusText ?? "").StartsWith("Connected", StringComparison.Ordinal);
            _statusTitle.ForeColor = !connected ? Bad : (shared ? Accent : Ok);
            string s = _controller.LiveStatus ?? "";
            if (dev.IsOpen && dev.ChecksumErrors > 0)
                s += "    Checksum errors: " + dev.ChecksumErrors;
            _status.Text = s;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            base.OnFormClosed(e);
        }
    }
}
