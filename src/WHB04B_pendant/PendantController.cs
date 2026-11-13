using System;
using System.Collections.Generic;
using System.Globalization;
using Whb04b;

namespace Plugins
{
    /// <summary>
    /// Connects the pendant to UCCNC. USB events arrive on the reader thread and are
    /// only queued here; every UCCNC call happens in <see cref="Loop"/>, which runs on
    /// UCCNC's plugin loop thread (the same pattern as the stock XHC-HB04 plugin).
    /// </summary>
    internal sealed class PendantController : IDisposable
    {
        // UCCNC LED / button codes, taken from the stock XHC-HB04 UCCNC plugin.
        private const int LedIdle = 18;              // used by HB04 plugin before start/zero/safe-Z
        private const int LedFeedHold = 217;
        private const int BtnCycleStart = 128;
        private const int BtnFeedHold = 523;
        private const int BtnFeedHoldRelease = 524;
        private const int BtnZeroAxisFirst = 100;    // 100..103 = zero X..A
        private const int BtnMpgAxisFirst = 220;     // 220..223 = select MPG/jog axis X..A (HB04 plugin)
        private const int LedMpgAxisFirst = 155;     // 155..158 = axis X..A selected
        private const int LedStepFirst = 148;        // 148..151 = jog step 0.001 / 0.01 / 0.1 / 1.0

        private readonly Plugininterface.Entry _uc;
        private readonly Logger _log;
        private readonly Whb04bDevice _device = new Whb04bDevice();
        private readonly object _sync = new object();
        private readonly Queue<KeyValuePair<Whb04bKey, bool>> _keys = new Queue<KeyValuePair<Whb04bKey, bool>>();

        private volatile PendantSettings _settings;
        private int _pendingWheel;
        private Whb04bAxis _axis = Whb04bAxis.Off;
        private Whb04bFeedKnob _feed = Whb04bFeedKnob.Unknown;
        private bool _axisChangedSinceLoop;
        private bool _deviceLost;

        private bool _stepMode;
        private bool _singleStep;               // step mode: one step per click, rate limited
        private bool _mpgUi;                    // true after the MPG key: mirror UCCNC's MPG buttons instead of Continous/Step
        private DateTime _lastSingleStepUtc = DateTime.MinValue;
        private double _contBudget;             // stepped continuous mode: distance allowed right now (rate limit)

        // Smooth continuous jog (JogOnSpeed) and the stop safety net
        private int _jogAxis = -1;
        private bool _jogDir;
        private DateTime _lastClickUtc;
        private int _stopStage;                 // 0 = idle, 1 = stop sent, 2 = stop re-sent, 3 = Reset pressed
        private int _stopAxis = -1;
        private bool _stopDir;
        private DateTime _stopUtc;
        private double _stopLastPos;
        private DateTime _stopLastMoveUtc;
        private double _jogPct;                 // speed of the running smooth jog (for the stop check timing)
        private double _stopPct;
        private DateTime _lastWheelLoopUtc = DateTime.MinValue;
        private bool _showMachineCoords;
        private DateTime _nextConnectAttempt = DateTime.MinValue;
        private bool _wasConnected;

        // Jog state
        private int _stepAxis = -1;
        private double _stepRemainder;

        public PendantController(Plugininterface.Entry uc, Logger log)
        {
            _uc = uc;
            _log = log;
            _device.Log = m =>
            {
                // USB problems are always logged; routine USB details only with "Debug log" on.
                string lower = m.ToLowerInvariant();
                if (lower.Contains("fail") || lower.Contains("error") || m.StartsWith("WARNING", StringComparison.Ordinal))
                    _log.Write("[usb] " + m);
                else
                    _log.Debug("[usb] " + m);
            };
            _device.KeyDown += OnKeyDown;
            _device.WheelMoved += OnWheel;
            _device.AxisChanged += OnAxisChanged;
            _device.FeedKnobChanged += OnFeedChanged;
            _device.Disconnected += OnDisconnected;
            ApplySettings(PendantSettings.Load(uc));
            _stepMode = _settings.StartInStepMode;
        }

        public PendantSettings Settings { get { return _settings; } }
        public bool IsConnected { get { return _device.IsOpen; } }
        public string StatusText { get; private set; }

        /// <summary>One-line live state for the settings window (mode, knobs, last wheel move, last jog call).</summary>
        public string LiveStatus { get; private set; }

        private string _lastJogCall = "none yet";

        // UCCNC screen mirroring
        private ButtonCatalog _catalog;
        private int _syncedMode = -1;   // 0 = continuous, 1 = step
        private int _syncedAxis = -1;
        private int _syncedStep = -1;
        private bool _mirrorPrimed;     // false until the pendant state is known; then only changes are mirrored

        public ButtonCatalog Catalog { get { return _catalog; } }
        private string _lastWheelInfo = "none yet";
        public Whb04bDevice Device { get { return _device; } }

        public void ApplySettings(PendantSettings s)
        {
            s.Sanitize();
            _settings = s;
            _log.DebugEnabled = s.DebugLog;
            _device.LogRawReports = s.DebugLog;
            _device.ChecksumMode = s.Checksum;
        }

        public void RequestReconnect()
        {
            lock (_sync) { _deviceLost = true; }
            _nextConnectAttempt = DateTime.MinValue;
        }

        // ---------------------------------------------------------------- USB thread

        private void OnKeyDown(Whb04bKey key, bool fn)
        {
            lock (_sync) { _keys.Enqueue(new KeyValuePair<Whb04bKey, bool>(key, fn)); }
        }

        private void OnWheel(Whb04bAxis axis, int delta)
        {
            lock (_sync) { _pendingWheel += delta; }
        }

        private void OnAxisChanged(Whb04bAxis axis)
        {
            lock (_sync)
            {
                _axis = axis;
                _pendingWheel = 0;
                _axisChangedSinceLoop = true;
            }
            if (_settings.DebugLog) _log.Write("Axis knob: " + axis);
        }

        private void OnFeedChanged(Whb04bFeedKnob feed)
        {
            lock (_sync) { _feed = feed; }
            if (_settings.DebugLog) _log.Write("Feed knob: " + feed);
        }

        private void OnDisconnected()
        {
            lock (_sync) { _deviceLost = true; }
            _log.Write("Pendant dongle disconnected");
        }

        // ---------------------------------------------------------------- UCCNC loop thread

        public void Loop()
        {
            bool lost;
            lock (_sync) { lost = _deviceLost; _deviceLost = false; }
            if (lost)
            {
                StopAllJog();
                _device.Close();
            }

            if (!_device.IsOpen)
            {
                StopAllJog();
                if (_wasConnected)
                {
                    _wasConnected = false;
                    StatusText = "Disconnected";
                }
                TryConnect();
                if (!_device.IsOpen) return;
            }

            if (_catalog == null)
                _catalog = LoadCatalogSafe();

            ProcessKeys();
            VerifyJogStopped();
            ProcessWheel();
            if (_settings.MirrorOnScreen)
                MirrorOnUccncScreen();
            UpdateDisplay();

            Whb04bAxis ax;
            Whb04bFeedKnob fk;
            lock (_sync) { ax = _axis; fk = _feed; }
            LiveStatus = "Mode: " + ModeName + "   Axis knob: " + ax + "   Speed knob: " + fk +
                         "   Last wheel: " + _lastWheelInfo + "\nLast jog: " + _lastJogCall;
        }

        private void TryConnect()
        {
            var now = DateTime.UtcNow;
            if (now < _nextConnectAttempt) return;
            _nextConnectAttempt = now.AddSeconds(2);

            bool ok = false;
            try
            {
                ok = _device.TryOpen(Whb04bConstants.ProductIdWireless);
                if (!ok && Whb04bDevice.FindDevicePaths(Whb04bConstants.ProductIdWiredHb04).Count > 0)
                {
                    _log.Debug("Found a device with PID EB70 (wired HB04 ID); trying it with the WHB04B protocol");
                    ok = _device.TryOpen(Whb04bConstants.ProductIdWiredHb04);
                }
            }
            catch (Exception ex)
            {
                _log.Write("Connect error: " + ex);
            }

            if (ok)
            {
                _wasConnected = true;
                StatusText = _device.SharedWithOtherProgram
                    ? "Connected, but ANOTHER PROGRAM is also using the pendant (close Whb04bTest.exe / Mach3)"
                    : "Connected";
                _log.Write("Pendant connected");
                lock (_sync) { _pendingWheel = 0; _keys.Clear(); }
                _syncedMode = _syncedAxis = _syncedStep = -1;
                _mirrorPrimed = false;
            }
            else
            {
                StatusText = "Dongle not found (VID 10CE, PID EB93)";
            }
        }

        private void ProcessKeys()
        {
            while (true)
            {
                KeyValuePair<Whb04bKey, bool> item;
                lock (_sync)
                {
                    if (_keys.Count == 0) return;
                    item = _keys.Dequeue();
                }

                var binding = _settings.Find(item.Key);
                string action = binding == null ? "" : (item.Value ? binding.FnAction : binding.Action);
                if (_settings.DebugLog)
                    _log.Write("Key " + (item.Value ? "Fn+" : "") + item.Key + " -> '" + action + "'");

                try
                {
                    RunAction(action);
                }
                catch (Exception ex)
                {
                    _log.Write("Action '" + action + "' failed: " + ex.Message);
                }
            }
        }

        private void RunAction(string action)
        {
            if (string.IsNullOrEmpty(action)) return;
            action = action.Trim();
            if (action.Length == 0) return;

            int code;
            if (int.TryParse(action, NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
            {
                // Stop always stops pendant jogging too; Reset drops pending pendant moves.
                if (code == 130) { HardStop(); return; }
                if (code == 144) StopAllJog();
                _uc.Callbutton(code);
                return;
            }

            switch (action.ToUpperInvariant())
            {
                case "STARTPAUSE":
                    if (_uc.GetLED(LedIdle)) _uc.Callbutton(BtnCycleStart);
                    else if (_uc.GetLED(LedFeedHold)) _uc.Callbutton(BtnFeedHoldRelease);
                    else _uc.Callbutton(BtnFeedHold);
                    return;

                case "SAFEZ":
                    if (_uc.GetLED(LedIdle))
                    {
                        string z = (_uc.Getfield(true, _settings.SafeZFieldNumber) ?? "").Trim();
                        double dummy;
                        if (double.TryParse(z, NumberStyles.Float, CultureInfo.InvariantCulture, out dummy))
                            _uc.Code("G0 Z" + z);
                        else
                            _log.Write("Safe-Z field " + _settings.SafeZFieldNumber + " is not a number: '" + z + "'");
                    }
                    return;

                // Step/Continuous family: UCCNC's own Continous/Step jog buttons, so keyboard jogging keeps working.
                case "MODE_CONT":
                    SetUiFamily(false);
                    SetMode(false, false);
                    return;

                case "MODE_STEP":
                    SetUiFamily(false);
                    SetMode(true, false);
                    return;

                case "MODE_TOGGLE":
                    SetUiFamily(false);
                    SetMode(!_stepMode, false);
                    return;

                // MPG family: UCCNC's MPG cont./multi/single buttons (UCCNC turns keyboard jogging off there).
                case "MODE_SINGLE":
                    SetUiFamily(true);
                    SetMode(true, true);
                    return;

                case "MODE_CYCLE":
                    // Like the UCR201 pendant: MPG continuous -> multi step -> single step -> continuous.
                    // The first press from the Step/Continuous family starts at MPG continuous.
                    if (!_mpgUi) { SetUiFamily(true); SetMode(false, false); }
                    else if (!_stepMode) SetMode(true, false);
                    else if (!_singleStep) SetMode(true, true);
                    else SetMode(false, false);
                    return;

                case "TOGGLE_COORDS":
                    _showMachineCoords = !_showMachineCoords;
                    return;

                case "ZERO_AXIS":
                    {
                        Whb04bAxis a;
                        lock (_sync) a = _axis;
                        if (a >= Whb04bAxis.X && a <= Whb04bAxis.A && _uc.GetLED(LedIdle))
                            _uc.Callbutton(BtnZeroAxisFirst + (int)a - 1);
                    }
                    return;

                case "STOPJOG":
                    StopAllJog();
                    return;

                case "STOPALL":
                    HardStop();
                    return;

                default:
                    _uc.Code(action);
                    return;
            }
        }

        private void SetMode(bool step, bool single)
        {
            if (_stepMode == step && (!step || _singleStep == single))
            {
                _singleStep = single;
                return;
            }
            StopAllJog();
            _stepMode = step;
            _singleStep = single;
            lock (_sync) _pendingWheel = 0;
            _log.Debug("Jog mode -> " + ModeName);
        }

        private string ModeName
        {
            get { return (_mpgUi ? "MPG " : "") + (!_stepMode ? "CONTINUOUS" : (_singleStep ? "SINGLE STEP" : "MULTI STEP")); }
        }

        /// <summary>
        /// Which UCCNC jog buttons mirror the pendant. In UCCNC, Continous / Step / MPG cont. /
        /// MPG single / MPG multi are one group (the last pressed wins), and keyboard jogging only
        /// works in Continous or Step. So the Step/Continuous key uses those two, the MPG key the MPG ones.
        /// </summary>
        private void SetUiFamily(bool mpg)
        {
            if (_mpgUi == mpg) return;
            _mpgUi = mpg;
            _syncedMode = -1; // re-press the matching UCCNC button
            _log.Debug("Pendant mode family -> " + (mpg ? "MPG" : "Step/Continuous"));
        }

        private void ProcessWheel()
        {
            int delta;
            Whb04bAxis axis;
            Whb04bFeedKnob feed;
            bool axisChanged;
            lock (_sync)
            {
                delta = _pendingWheel;
                _pendingWheel = 0;
                axis = _axis;
                feed = _feed;
                axisChanged = _axisChangedSinceLoop;
                _axisChangedSinceLoop = false;
            }

            var now = DateTime.UtcNow;
            double loopSeconds = _lastWheelLoopUtc == DateTime.MinValue ? 0 : Math.Min(0.5, (now - _lastWheelLoopUtc).TotalSeconds);
            _lastWheelLoopUtc = now;

            if (axisChanged) StopAllJog();

            var s = _settings;
            int axisIndex = (int)axis - 1; // X=0 .. C=5
            // The pendant counts clockwise as negative relative to UCCNC's axes (the stock HB04 plugin
            // flips it too), so the wheel is flipped by default; "Reverse" flips it back per axis.
            delta = -delta;
            if (axisIndex >= 0 && axisIndex < s.ReverseAxis.Length && s.ReverseAxis[axisIndex]) delta = -delta;
            if (delta != 0)
                _lastWheelInfo = (delta > 0 ? "+" : "") + delta + " at " + DateTime.Now.ToString("HH:mm:ss");

            if (axis == Whb04bAxis.Off || feed == Whb04bFeedKnob.Lead || feed == Whb04bFeedKnob.Unknown)
            {
                if (delta != 0)
                    Diag("Wheel " + delta + " ignored: axis knob " + axis + ", speed knob " + feed + " (axis must be X/Y/Z/A and speed knob not Lead)");
                StopAllJog();
                return;
            }

            // Every mode only ever sends short, fixed-length moves (AddLinearMoveRel).
            // UCCNC's open-ended JogOnSpeed could not be stopped from a plugin, so it is not used.
            double step;
            if (!_stepMode && s.SmoothContinuous)
            {
                ProcessSmoothContinuous(delta, axisIndex, KnobPercent(feed) * s.ContinuousScalePercent / 100.0, now);
                return;
            }
            if (_jogAxis >= 0) StopSmoothJog("mode changed");

            if (!_stepMode)
            {
                // Stepped continuous: distance per click and a rate limit, both scaled by the knob %.
                double pct = KnobPercent(feed);
                step = s.ContinuousMmPerClick * pct / 100.0;
                double rate = s.ContinuousMaxRate * pct / 100.0;          // units per second
                _contBudget = Math.Min(_contBudget + rate * loopSeconds, rate * 0.25);
                if (delta == 0 || step <= 0) return;

                int allowed = (int)Math.Floor(_contBudget / step + 1e-9);
                int wanted = Math.Abs(delta);
                if (wanted > allowed)
                {
                    Diag("Continuous: " + (wanted - allowed) + " click(s) dropped (rate limit " + rate.ToString("0.##", CultureInfo.InvariantCulture) + " units/s)");
                    wanted = allowed;
                }
                if (wanted <= 0) return;
                _contBudget -= wanted * step;
                delta = delta > 0 ? wanted : -wanted;
            }
            else
            {
                if (delta == 0) return;
                step = KnobStep(feed);
                if (step <= 0 && (feed == Whb04bFeedKnob.Pct60 || feed == Whb04bFeedKnob.Pct100))
                    step = 1.0; // 60 % and 100 % have no printed step size; step like the 1.0 position
                if (step <= 0)
                {
                    Diag("Wheel " + delta + " ignored: STEP mode needs a speed knob position other than Lead (it is on " + feed + ").");
                    return;
                }

                if (_singleStep)
                {
                    // One step per click at most every 200 ms; extra clicks are dropped (safe fine positioning).
                    if ((now - _lastSingleStepUtc).TotalMilliseconds < 200) return;
                    _lastSingleStepUtc = now;
                    delta = delta > 0 ? 1 : -1;
                }
            }

            if (delta > s.MaxDetentsPerLoop) delta = s.MaxDetentsPerLoop;
            if (delta < -s.MaxDetentsPerLoop) delta = -s.MaxDetentsPerLoop;

            if (_stepAxis != axisIndex)
            {
                _stepAxis = axisIndex;
                _stepRemainder = 0;
            }

            // Like the HB04 plugin: convert the distance into whole motor steps so
            // that no fractions of a step are lost between moves.
            double distPerMotorStep = MotorStepDistance(axisIndex);
            if (distPerMotorStep <= 0)
            {
                int rc0 = _uc.AddLinearMoveRel(axisIndex, step, Math.Abs(delta), s.StepSpeedPercent, delta > 0);
                RecordJog(string.Format(CultureInfo.InvariantCulture, "AddLinearMoveRel(axis {0}, {1} x {2}, {3}%, {4}) = {5} [steps/unit unknown]", axisIndex, step, Math.Abs(delta), s.StepSpeedPercent, delta > 0 ? "+" : "-", rc0));
                return;
            }

            _stepRemainder += delta * step;
            double ratio = _stepRemainder / distPerMotorStep;
            int motorSteps = (int)(ratio > 0 ? ratio + 0.5 : ratio - 0.5);
            if (motorSteps == 0)
            {
                Diag(string.Format(CultureInfo.InvariantCulture, "Wheel {0}: {1} is less than one motor step ({2}), waiting for more clicks", delta, delta * step, distPerMotorStep));
                return;
            }
            _stepRemainder -= motorSteps * distPerMotorStep;
            int rc = _uc.AddLinearMoveRel(axisIndex, distPerMotorStep, Math.Abs(motorSteps), s.StepSpeedPercent, motorSteps > 0);
            RecordJog(string.Format(CultureInfo.InvariantCulture, "AddLinearMoveRel(axis {0}, {1} x {2} motor steps, {3}%, {4}) = {5}", axisIndex, distPerMotorStep, Math.Abs(motorSteps), s.StepSpeedPercent, motorSteps > 0 ? "+" : "-", rc));
        }

        private void RecordJog(string call)
        {
            _lastJogCall = call + " at " + DateTime.Now.ToString("HH:mm:ss");
            if (_settings.DebugLog) _log.Write("Jog: " + call);
        }

        private void Diag(string message)
        {
            _lastJogCall = message;
            if (_settings.DebugLog) _log.Write(message);
        }

        /// <summary>Distance of one motor step: 1 / steps-per-unit from the UCCNC axis setup (field 8 + axis*15).</summary>
        private double MotorStepDistance(int axisIndex)
        {
            try
            {
                double stepsPerUnit = _uc.Getfielddouble(true, 8 + axisIndex * 15);
                if (stepsPerUnit > 0 && !double.IsInfinity(stepsPerUnit))
                    return 1.0 / stepsPerUnit;
            }
            catch
            {
            }
            return 0;
        }

        public ButtonCatalog LoadCatalogSafe()
        {
            // A failure here (even a type-load error) must never stop jogging and keys.
            try
            {
                return ButtonCatalog.Load(_uc, _log);
            }
            catch (Exception ex)
            {
                _log.Write("UCCNC button list unavailable: " + ex.Message);
                return new ButtonCatalog();
            }
        }

        /// <summary>Makes UCCNC's jog panel show the pendant's mode, step size and axis.</summary>
        private void MirrorOnUccncScreen()
        {
            try
            {
                Whb04bAxis axis;
                Whb04bFeedKnob feed;
                lock (_sync) { axis = _axis; feed = _feed; }

                int mode = !_stepMode ? 0 : (_singleStep ? 2 : 1);
                int modeKey = mode + (_mpgUi ? 10 : 0);
                int axIdx = axis >= Whb04bAxis.X && axis <= Whb04bAxis.A ? (int)axis - 1 : -1;

                if (!_mirrorPrimed)
                {
                    // Do not touch UCCNC's jog panel when the plugin starts or reconnects:
                    // remember the pendant's current state and only mirror what the user changes on it.
                    if (feed == Whb04bFeedKnob.Unknown) return; // no report from the pendant yet
                    _syncedMode = modeKey;
                    _syncedAxis = axIdx;
                    _syncedStep = StepIndex(feed);
                    _mirrorPrimed = true;
                    return;
                }
                if (modeKey != _syncedMode && _stopStage == 0)
                {
                    // One button only: these UCCNC buttons are one group and the last one pressed wins.
                    int mpg = mode == 0 ? _catalog.MpgContinuous : (mode == 1 ? _catalog.MpgMulti : _catalog.MpgSingle);
                    if (_mpgUi && _settings.MirrorMpgButtons && mpg >= 0)
                    {
                        PressScreenButton(mpg, mode == 0 ? "MPG cont." : (mode == 1 ? "MPG multi" : "MPG single"));
                    }
                    else
                    {
                        int code = _stepMode ? _catalog.JogStep : _catalog.JogContinuous;
                        if (code >= 0) PressScreenButton(code, "jog mode " + (_stepMode ? "step" : "continuous"));
                    }
                    _syncedMode = modeKey;
                }

                int ax = axis >= Whb04bAxis.X && axis <= Whb04bAxis.A ? (int)axis - 1 : -1;
                if (ax >= 0 && ax != _syncedAxis)
                {
                    if (!_uc.GetLED(LedMpgAxisFirst + ax))
                        PressScreenButton(BtnMpgAxisFirst + ax, "axis " + axis);
                    _syncedAxis = ax;
                }

                int stepIdx = StepIndex(feed);
                if (_stepMode && stepIdx >= 0 && stepIdx != _syncedStep)
                {
                    int code = _catalog.StepSize[stepIdx];
                    if (code >= 0 && !_uc.GetLED(LedStepFirst + stepIdx))
                        PressScreenButton(code, "step " + KnobStep(feed).ToString(CultureInfo.InvariantCulture));
                    _syncedStep = stepIdx;
                }
            }
            catch (Exception ex)
            {
                if (_settings.DebugLog) _log.Write("Screen mirror failed: " + ex.Message);
            }
        }

        private void PressScreenButton(int code, string what)
        {
            _uc.Callbutton(code);
            if (_settings.DebugLog) _log.Write("Screen: pressed UCCNC button " + code + " (" + what + ")");
        }

        private static int StepIndex(Whb04bFeedKnob f)
        {
            switch (f)
            {
                case Whb04bFeedKnob.Step0001Pct2: return 0;
                case Whb04bFeedKnob.Step001Pct5: return 1;
                case Whb04bFeedKnob.Step01Pct10: return 2;
                case Whb04bFeedKnob.Step1Pct30: return 3;
                default: return -1;
            }
        }

        private static double KnobStep(Whb04bFeedKnob f)
        {
            switch (f)
            {
                case Whb04bFeedKnob.Step0001Pct2: return 0.001;
                case Whb04bFeedKnob.Step001Pct5: return 0.01;
                case Whb04bFeedKnob.Step01Pct10: return 0.1;
                case Whb04bFeedKnob.Step1Pct30: return 1.0;
                default: return 0; // 60 %, 100 % and Lead have no step size
            }
        }

        private static double KnobPercent(Whb04bFeedKnob f)
        {
            switch (f)
            {
                case Whb04bFeedKnob.Step0001Pct2: return 2;
                case Whb04bFeedKnob.Step001Pct5: return 5;
                case Whb04bFeedKnob.Step01Pct10: return 10;
                case Whb04bFeedKnob.Step1Pct30: return 30;
                case Whb04bFeedKnob.Pct60: return 60;
                case Whb04bFeedKnob.Pct100: return 100;
                default: return 0;
            }
        }

        /// <summary>
        /// Smooth continuous jog: UCCNC's JogOnSpeed at the knob % while the wheel turns.
        /// A plugin cannot stop JogOnSpeed with speed 0; testing showed that pressing UCCNC's
        /// "jog mode stepping" (162) and "MPG multi" (228) buttons does stop it. Every stop is then
        /// checked against the machine position, and escalated up to Reset (see VerifyJogStopped).
        /// </summary>
        private void ProcessSmoothContinuous(int delta, int axisIndex, double pct, DateTime now)
        {
            if (delta != 0 && pct > 0)
            {
                bool dir = delta > 0;
                if (_stopStage > 0) return;                       // still confirming the last stop
                if (_jogAxis >= 0 && (_jogAxis != axisIndex || _jogDir != dir))
                {
                    StopSmoothJog("direction or axis changed");   // the next clicks start the new direction
                    return;
                }
                int rc = _uc.JogOnSpeed(axisIndex, dir, pct);
                if (_jogAxis < 0)
                    RecordJog(string.Format(CultureInfo.InvariantCulture, "JogOnSpeed(axis {0}, {1}, {2}%) = {3} (smooth start)", axisIndex, dir ? "+" : "-", pct, rc));
                _jogAxis = axisIndex;
                _jogDir = dir;
                _jogPct = pct;
                _lastClickUtc = now;
            }
            else if (_jogAxis >= 0 && (now - _lastClickUtc).TotalMilliseconds > _settings.ContinuousStopMs)
            {
                StopSmoothJog("wheel stopped");
            }
        }

        private void StopSmoothJog(string reason)
        {
            int axis = _jogAxis;
            if (axis < 0) return;
            _jogAxis = -1;
            _stopAxis = axis;
            _stopDir = _jogDir;
            _stopPct = _jogPct;
            _stopUtc = DateTime.UtcNow;
            _stopLastMoveUtc = _stopUtc;
            _stopLastPos = MachinePos(axis);
            _stopStage = 1;
            SendSmoothStop(axis, _stopDir);
            _log.Debug("Smooth jog stop (" + reason + "), axis " + axis);
        }

        private void SendSmoothStop(int axis, bool dir)
        {
            try
            {
                // Not JogOnSpeed(axis, dir, 0) or MPGJogOff: they never stopped the jog in testing, and
                // they are the likely reason UCCNC's keyboard jog stopped working after pendant use
                // (they leave the axis jog at 0 % speed). The two buttons below do stop it, and
                // VerifyJogStopped checks every stop against the machine position.
                int stepBtn = _catalog != null && _catalog.JogStep >= 0 ? _catalog.JogStep : 162;
                int multiBtn = _catalog != null && _catalog.MpgMulti >= 0 ? _catalog.MpgMulti : 228;
                _uc.Callbutton(stepBtn);
                _uc.Callbutton(multiBtn);
                _syncedMode = -1; // the jog-mode buttons are restored once the stop is confirmed
            }
            catch (Exception ex)
            {
                _log.Write("Jog stop failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Safety net after every smooth-jog stop: watch the machine position. Still moving after
        /// 0.3 s (0.5 s at 100 %): send the stop again. Still moving after 0.8 s (1.0 s at 100 %):
        /// press UCCNC Reset (512). The extra time at high speed is the machine's normal braking:
        /// on a StepCraft M700 a stop from 100 % took up to 0.41 s.
        /// </summary>
        private void VerifyJogStopped()
        {
            if (_stopStage == 0) return;
            var now = DateTime.UtcNow;
            double pos = MachinePos(_stopAxis);
            if (Math.Abs(pos - _stopLastPos) > 1e-6) _stopLastMoveUtc = now;
            _stopLastPos = pos;

            double sinceStop = (now - _stopUtc).TotalMilliseconds;
            double still = (now - _stopLastMoveUtc).TotalMilliseconds;

            if (still >= 120)
            {
                _log.Debug("Smooth jog stopped after " + (int)sinceStop + " ms");
                _stopStage = 0;
                _syncedMode = -1; // restore the jog-mode buttons on the UCCNC screen
                return;
            }
            double brake = 2.0 * Math.Max(0, Math.Min(100, _stopPct)); // up to 200 ms extra at 100 %
            if (_stopStage == 1 && sinceStop > 300 + brake)
            {
                _log.Write("WARNING: axis " + _stopAxis + " still moving " + (int)sinceStop + " ms after the jog stop, sending the stop again");
                SendSmoothStop(_stopAxis, _stopDir);
                _stopStage = 2;
            }
            else if (_stopStage == 2 && sinceStop > 800 + brake)
            {
                _log.Write("ERROR: axis " + _stopAxis + " still moving " + (int)sinceStop + " ms after the jog stop, pressing UCCNC Reset");
                _uc.Callbutton(512); // "Switches the Reset button on"
                _stopStage = 3;
            }
            else if (_stopStage == 3 && sinceStop > 3000)
            {
                _stopStage = 0;
                _syncedMode = -1;
            }
        }

        private double MachinePos(int axis)
        {
            try
            {
                switch (axis)
                {
                    case 0: return _uc.GetXmachpos();
                    case 1: return _uc.GetYmachpos();
                    case 2: return _uc.GetZmachpos();
                    case 3: return _uc.GetAmachpos();
                    case 4: return _uc.GetBmachpos();
                    case 5: return _uc.GetCmachpos();
                }
            }
            catch
            {
            }
            return 0;
        }

        /// <summary>Forgets every wheel click that has not been turned into a move yet.</summary>
        public void StopAllJog()
        {
            if (_jogAxis >= 0) StopSmoothJog("stop");
            _stepRemainder = 0;
            _contBudget = 0;
            lock (_sync) _pendingWheel = 0;
        }

        /// <summary>
        /// Stop key: stop G-code (130), drop pending pendant moves, and press UCCNC's
        /// "jog mode stepping" button (162), which in testing was what halted a running jog.
        /// The jog-mode buttons are pressed again on the next loop so the screen stays in sync.
        /// </summary>
        private void HardStop()
        {
            StopAllJog();
            _uc.Callbutton(130);
            int stepBtn = _catalog != null && _catalog.JogStep >= 0 ? _catalog.JogStep : 162;
            _uc.Callbutton(stepBtn);
            _syncedMode = -1;
            _log.Debug("STOP: pressed 130 (stop) and " + stepBtn + " (jog mode stepping), pending pendant moves dropped");
        }

        private void UpdateDisplay()
        {
            Whb04bAxis axis;
            lock (_sync) axis = _axis;

            try
            {
                var d = new Whb04bDisplayData
                {
                    Mode = _stepMode ? Whb04bDisplayMode.Step : Whb04bDisplayMode.Continuous,
                    MachineCoordinates = _showMachineCoords,
                };

                bool abc = axis == Whb04bAxis.A || axis == Whb04bAxis.B || axis == Whb04bAxis.C;
                if (_showMachineCoords)
                {
                    d.Axis1 = abc ? _uc.GetAmachpos() : _uc.GetXmachpos();
                    d.Axis2 = abc ? _uc.GetBmachpos() : _uc.GetYmachpos();
                    d.Axis3 = abc ? _uc.GetCmachpos() : _uc.GetZmachpos();
                }
                else
                {
                    d.Axis1 = abc ? _uc.GetApos() : _uc.GetXpos();
                    d.Axis2 = abc ? _uc.GetBpos() : _uc.GetYpos();
                    d.Axis3 = abc ? _uc.GetCpos() : _uc.GetZpos();
                }

                d.FeedRate = ReadNumberField(_settings.FeedFieldNumber);
                d.SpindleSpeed = ReadNumberField(_settings.SpindleFieldNumber);
                _device.UpdateDisplay(d);
            }
            catch (Exception ex)
            {
                if (_settings.DebugLog) _log.Write("Display update failed: " + ex.Message);
            }
        }

        /// <summary>Reads a UCCNC field and keeps only the leading number (e.g. "100%" → 100).</summary>
        private int ReadNumberField(int field)
        {
            if (field <= 0) return 0;
            string text = _uc.Getfield(true, field);
            if (string.IsNullOrEmpty(text)) return 0;

            int end = 0;
            text = text.Trim();
            while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.' || text[end] == ',' || (end == 0 && text[end] == '-')))
                end++;
            double v;
            if (end > 0 && double.TryParse(text.Substring(0, end).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return (int)Math.Round(v);
            return 0;
        }

        public void Dispose()
        {
            StopAllJog();
            _device.Dispose();
        }
    }
}
