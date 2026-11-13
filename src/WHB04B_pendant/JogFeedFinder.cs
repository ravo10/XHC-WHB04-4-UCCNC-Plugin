using System;
using System.Collections.Generic;
using System.Globalization;

namespace Plugins
{
    /// <summary>
    /// Finds which UCCNC screen field holds "Jog feed %", so the pendant's speed knob can set the
    /// speed of UCCNC's own jog. No stock plugin sets it, so the field is found by experiment, once:
    /// read all fields, press "Increases the jog rate" (159), read again, press "Decreases the jog
    /// rate" (160), read again. The field that went up and then back to its old value is the jog feed.
    /// The result is saved in the profile. The work runs a slice per UCCNC loop so it never blocks.
    /// </summary>
    internal sealed class JogFeedFinder
    {
        private const int FieldsPerSet = 1500;     // field numbers 1..1500, in both field sets
        private const int PerLoop = 500;           // Getfield calls per loop
        private const int WaitMs = 400;            // time for UCCNC to update the field after a press

        private enum State { Idle, ScanBefore, WaitUp, ScanUp, WaitDown, ScanDown, Done }

        private State _state = State.Idle;
        private readonly Dictionary<int, double> _before = new Dictionary<int, double>();
        private readonly Dictionary<int, double> _up = new Dictionary<int, double>();
        private readonly Dictionary<int, double> _down = new Dictionary<int, double>();
        private int _cursor;
        private DateTime _pressedUtc;
        private DateTime _startedUtc;

        /// <summary>True when the field is known, or the search ended without a result.</summary>
        public bool IsDone(PendantSettings s)
        {
            return s.JogFeedField > 0 || _state == State.Done;
        }

        public void Step(Plugininterface.Entry uc, Logger log, PendantSettings s)
        {
            if (IsDone(s)) return;
            var now = DateTime.UtcNow;
            try
            {
                switch (_state)
                {
                    case State.Idle:
                        _before.Clear(); _up.Clear(); _down.Clear();
                        _cursor = 0;
                        _startedUtc = now;
                        _state = State.ScanBefore;
                        log.Write("Looking for UCCNC's Jog feed % field (one time, about 2 s)");
                        break;

                    case State.ScanBefore:
                        if (Scan(uc, _before)) { uc.Callbutton(159); _pressedUtc = now; _state = State.WaitUp; }
                        break;

                    case State.WaitUp:
                        if ((now - _pressedUtc).TotalMilliseconds >= WaitMs) { _cursor = 0; _state = State.ScanUp; }
                        break;

                    case State.ScanUp:
                        if (Scan(uc, _up)) { uc.Callbutton(160); _pressedUtc = now; _state = State.WaitDown; }
                        break;

                    case State.WaitDown:
                        if ((now - _pressedUtc).TotalMilliseconds >= WaitMs) { _cursor = 0; _state = State.ScanDown; }
                        break;

                    case State.ScanDown:
                        if (Scan(uc, _down)) { Pick(uc, log, s); _state = State.Done; }
                        break;
                }
            }
            catch (Exception ex)
            {
                log.Write("Jog feed field search failed: " + ex.Message);
                _state = State.Done;
            }
        }

        private bool Scan(Plugininterface.Entry uc, Dictionary<int, double> into)
        {
            int end = Math.Min(_cursor + PerLoop, FieldsPerSet * 2);
            for (int i = _cursor; i < end; i++)
            {
                bool as3 = i < FieldsPerSet;
                int number = (i % FieldsPerSet) + 1;
                try
                {
                    double v;
                    if (TryNumber(uc.Getfield(as3, number), out v))
                        into[i] = v;
                }
                catch
                {
                    // field does not exist
                }
            }
            _cursor = end;
            return _cursor >= FieldsPerSet * 2;
        }

        private void Pick(Plugininterface.Entry uc, Logger log, PendantSettings s)
        {
            var found = new List<int>();
            foreach (var kv in _before)
            {
                double up, down;
                if (!_up.TryGetValue(kv.Key, out up) || !_down.TryGetValue(kv.Key, out down)) continue;
                bool wentUp = up > kv.Value + 1e-9;
                bool cameBack = Math.Abs(down - kv.Value) < 1e-6;
                if (wentUp && cameBack && kv.Value >= 0 && kv.Value <= 1000) found.Add(kv.Key);
            }

            log.Write(string.Format("Jog feed field search: {0} candidate(s) in {1:0.0} s: {2}",
                found.Count, (DateTime.UtcNow - _startedUtc).TotalSeconds, string.Join(", ", found.ConvertAll(Describe).ToArray())));

            if (found.Count == 0)
            {
                log.Write("Jog feed field not found; the pendant speed knob cannot set UCCNC's jog speed (UCCNC's own Jog feed % is used).");
                return;
            }

            int key = found[0];
            s.JogFeedFieldAS3 = key < FieldsPerSet;
            s.JogFeedField = (key % FieldsPerSet) + 1;
            s.SaveJogFeedField(uc);
            log.Write("Using " + Describe(key) + " as UCCNC's Jog feed % field");
        }

        private string Describe(int key)
        {
            double b;
            _before.TryGetValue(key, out b);
            return string.Format(CultureInfo.InvariantCulture, "{0} field {1} (was {2})", key < FieldsPerSet ? "AS3" : "main", (key % FieldsPerSet) + 1, b);
        }

        private static bool TryNumber(string text, out double value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text)) return false;
            text = text.Trim().TrimEnd('%').Trim().Replace(',', '.');
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
