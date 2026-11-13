using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Whb04b
{
    public enum ChecksumMode
    {
        /// <summary>Do not check the checksum at all.</summary>
        Ignore,
        /// <summary>Accept every report; send a seed packet when the checksum is wrong (default).</summary>
        Resync,
        /// <summary>Drop reports with a wrong checksum and send a seed packet (what the Mach3 driver does).</summary>
        Strict,
    }

    /// <summary>
    /// Talks to the WHB04B USB dongle. Events are raised on a background reader
    /// thread, so handlers must be thread-safe.
    /// </summary>
    public sealed class Whb04bDevice : IDisposable
    {
        private readonly object _writeLock = new object();
        private SafeFileHandle _readHandle;
        private SafeFileHandle _writeHandle;
        private Thread _readerThread;
        private volatile bool _stopping;
        private int _inputLength = Whb04bConstants.InputReportLength;
        private int _featureLength = 1 + Whb04bConstants.FeaturePayloadLength;
        private byte[] _lastBlock;
        private DateTime _lastBlockSent = DateTime.MinValue;
        private DateTime _lastSeedSent = DateTime.MinValue;
        private volatile bool _forceDisplay;
        private Thread _writerThread;
        private readonly AutoResetEvent _writeSignal = new AutoResetEvent(false);
        private byte[] _pendingBlock;           // newest display block waiting for the writer thread

        private ushort _lastKeyWord;
        private Whb04bKey _pressedKey = Whb04bKey.None;
        private Whb04bAxis _lastAxis = (Whb04bAxis)(-1);
        private Whb04bFeedKnob _lastFeed = (Whb04bFeedKnob)(-1);

        public Whb04bDevice()
        {
            ChecksumMode = ChecksumMode.Resync;
            DisplayRefreshInterval = TimeSpan.FromMilliseconds(500);
            Seed = (byte)DateTime.UtcNow.Day;
        }

        /// <summary>Optional log sink (called from any thread).</summary>
        public Action<string> Log { get; set; }

        /// <summary>When true, every raw input report is logged.</summary>
        public bool LogRawReports { get; set; }

        public ChecksumMode ChecksumMode { get; set; }

        /// <summary>The display block is resent at least this often even when it has not changed.</summary>
        public TimeSpan DisplayRefreshInterval { get; set; }

        public byte Seed { get; private set; }
        public bool IsOpen { get { return _readHandle != null && !_readHandle.IsInvalid && !_readHandle.IsClosed; } }
        public string ReadPath { get; private set; }
        public string WritePath { get; private set; }
        public int ChecksumErrors { get; private set; }

        /// <summary>True when another program also had the LCD interface open for writing at connect time.</summary>
        public bool SharedWithOtherProgram { get; private set; }

        /// <summary>Every input report (after checksum handling).</summary>
        public event Action<Whb04bInputReport> ReportReceived;

        /// <summary>A key went down. The bool is true when Fn is held.</summary>
        public event Action<Whb04bKey, bool> KeyDown;

        /// <summary>The key that was down has been released.</summary>
        public event Action<Whb04bKey> KeyUp;

        /// <summary>Jog wheel moved; delta is in detents (100 per turn), sign is direction.</summary>
        public event Action<Whb04bAxis, int> WheelMoved;

        public event Action<Whb04bAxis> AxisChanged;
        public event Action<Whb04bFeedKnob> FeedKnobChanged;

        /// <summary>The dongle was unplugged or a read failed.</summary>
        public event Action Disconnected;

        /// <summary>Lists the HID interface paths that belong to the dongle.</summary>
        public static List<string> FindDevicePaths(ushort productId = Whb04bConstants.ProductIdWireless)
        {
            string id = string.Format("vid_{0:x4}&pid_{1:x4}", Whb04bConstants.VendorId, productId);
            var found = new List<string>();
            foreach (var path in NativeMethods.EnumerateHidPaths())
            {
                if (path.ToLowerInvariant().Contains(id))
                    found.Add(path);
            }
            return found;
        }

        public bool TryOpen(ushort productId = Whb04bConstants.ProductIdWireless)
        {
            Close();
            _stopping = false;

            var paths = FindDevicePaths(productId);
            if (paths.Count == 0)
                return false;

            string readPath = null, writePath = null;
            foreach (var p in paths)
            {
                var lower = p.ToLowerInvariant();
                if (lower.Contains("&col01")) readPath = p;
                else if (lower.Contains("&col02")) writePath = p;
            }

            // Fallback for a dongle that exposes a single collection.
            if (readPath == null) readPath = paths[0];
            if (writePath == null) writePath = readPath;

            WriteLog("Found " + paths.Count + " interface(s). Read: " + readPath + "  Write: " + writePath);

            _readHandle = NativeMethods.CreateFile(readPath, NativeMethods.GENERIC_READ,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero,
                NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (_readHandle.IsInvalid)
            {
                WriteLog("Could not open read interface, error " + Marshal.GetLastWin32Error());
                Close();
                return false;
            }

            // Ask for exclusive write access first, so two programs never write to the LCD at once
            // (that makes the display jump between their values and show garbage).
            SharedWithOtherProgram = false;
            _writeHandle = NativeMethods.CreateFile(writePath, NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ, IntPtr.Zero,
                NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (_writeHandle.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == NativeMethods.ERROR_SHARING_VIOLATION)
                {
                    SharedWithOtherProgram = true;
                    WriteLog("WARNING: another program is writing to the pendant (Whb04bTest.exe, Mach3, another UCCNC plugin?). Close it, otherwise the LCD will flicker.");
                }
                _writeHandle = NativeMethods.CreateFile(writePath, NativeMethods.GENERIC_WRITE,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            }
            if (_writeHandle.IsInvalid)
            {
                // Some setups only allow access 0 on the write collection; feature reports still work.
                _writeHandle = NativeMethods.CreateFile(writePath, 0,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            }
            if (_writeHandle.IsInvalid)
                WriteLog("Could not open write interface, error " + Marshal.GetLastWin32Error() + " (LCD will not update)");

            NativeMethods.HIDP_CAPS caps;
            if (NativeMethods.TryGetCaps(_readHandle, out caps))
            {
                _inputLength = Math.Max((int)caps.InputReportByteLength, Whb04bConstants.InputReportLength);
                WriteLog(string.Format("Read caps: usagePage=0x{0:X4} usage=0x{1:X4} in={2} out={3} feature={4}",
                    caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength));
            }
            if (!_writeHandle.IsInvalid && NativeMethods.TryGetCaps(_writeHandle, out caps))
            {
                if (caps.FeatureReportByteLength > 0)
                    _featureLength = Math.Max((int)caps.FeatureReportByteLength, 1 + Whb04bConstants.FeaturePayloadLength);
                WriteLog(string.Format("Write caps: usagePage=0x{0:X4} usage=0x{1:X4} in={2} out={3} feature={4}",
                    caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength));
            }

            ReadPath = readPath;
            WritePath = writePath;
            _lastBlock = null;
            _lastKeyWord = 0;
            _pressedKey = Whb04bKey.None;
            _lastAxis = (Whb04bAxis)(-1);
            _lastFeed = (Whb04bFeedKnob)(-1);

            SendSeed();

            _readerThread = new Thread(ReaderLoop) { IsBackground = true, Name = "WHB04B reader" };
            _readerThread.Start();
            _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "WHB04B LCD writer" };
            _writerThread.Start();
            return true;
        }

        public void Close()
        {
            _stopping = true;
            var t = _readerThread;
            if (t != null && t != Thread.CurrentThread)
                t.Join(1000);
            _readerThread = null;

            _writeSignal.Set();
            var w = _writerThread;
            if (w != null && w != Thread.CurrentThread)
                w.Join(1000);
            _writerThread = null;
            Interlocked.Exchange(ref _pendingBlock, null);

            if (_readHandle != null) { _readHandle.Dispose(); _readHandle = null; }
            lock (_writeLock)
            {
                if (_writeHandle != null) { _writeHandle.Dispose(); _writeHandle = null; }
            }
        }

        public void Dispose()
        {
            Close();
        }

        /// <summary>Sends the seed packet (also done automatically on open and on checksum errors).</summary>
        public bool SendSeed()
        {
            _lastSeedSent = DateTime.UtcNow;
            return SendFeature(Whb04bDisplayData.SeedPacket(Seed));
        }

        /// <summary>Updates the LCD. Only sends when the content changed or the refresh interval elapsed.</summary>
        public bool UpdateDisplay(Whb04bDisplayData data, bool force = false)
        {
            var block = data.Encode(Seed);
            var now = DateTime.UtcNow;
            if (_forceDisplay)
            {
                _forceDisplay = false;
                force = true;
            }
            if (!force && _lastBlock != null && SameBytes(block, _lastBlock) && now - _lastBlockSent < DisplayRefreshInterval)
                return true;

            // The USB writes happen on the writer thread, so the caller (UCCNC's plugin loop)
            // never waits for the dongle. Only the newest block is kept.
            _lastBlock = block;
            _lastBlockSent = now;
            Interlocked.Exchange(ref _pendingBlock, block);
            _writeSignal.Set();
            return IsOpen;
        }

        private void WriterLoop()
        {
            while (!_stopping)
            {
                _writeSignal.WaitOne(250, false);
                var block = Interlocked.Exchange(ref _pendingBlock, null);
                if (block == null || _stopping) continue;

                // Hold the lock for the whole block so nothing can be sent between its 3 reports.
                lock (_writeLock)
                {
                    foreach (var report in Whb04bDisplayData.ToFeatureReports(block))
                    {
                        if (!SendFeature(report))
                        {
                            _lastBlock = null; // resend next time
                            break;
                        }
                    }
                }
            }
        }

        private bool SendFeature(byte[] report)
        {
            lock (_writeLock)
            {
                if (_writeHandle == null || _writeHandle.IsInvalid || _writeHandle.IsClosed)
                    return false;

                byte[] buf = report;
                if (_featureLength > report.Length)
                {
                    buf = new byte[_featureLength];
                    Array.Copy(report, buf, report.Length);
                }

                if (!NativeMethods.HidD_SetFeature(_writeHandle, buf, buf.Length))
                {
                    WriteLog("HidD_SetFeature failed, error " + Marshal.GetLastWin32Error());
                    return false;
                }
                return true;
            }
        }

        private void ReaderLoop()
        {
            int len = _inputLength;
            IntPtr buffer = Marshal.AllocHGlobal(len);
            IntPtr overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
            var evt = new ManualResetEvent(false);
            var managed = new byte[len];
            bool lost = false;

            try
            {
                while (!_stopping)
                {
                    evt.Reset();
                    var ov = new NativeOverlapped { EventHandle = evt.SafeWaitHandle.DangerousGetHandle() };
                    Marshal.StructureToPtr(ov, overlapped, false);

                    if (!NativeMethods.ReadFile(_readHandle, buffer, len, IntPtr.Zero, overlapped))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != NativeMethods.ERROR_IO_PENDING)
                        {
                            WriteLog("ReadFile failed, error " + err);
                            lost = true;
                            break;
                        }
                    }

                    while (!evt.WaitOne(100, false))
                    {
                        if (_stopping)
                        {
                            NativeMethods.CancelIoEx(_readHandle, overlapped);
                            int ignored;
                            NativeMethods.GetOverlappedResult(_readHandle, overlapped, out ignored, true);
                            return;
                        }
                    }

                    int read;
                    if (!NativeMethods.GetOverlappedResult(_readHandle, overlapped, out read, false))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != NativeMethods.ERROR_OPERATION_ABORTED || !_stopping)
                        {
                            WriteLog("Read failed, error " + err);
                            lost = true;
                        }
                        break;
                    }

                    Marshal.Copy(buffer, managed, 0, read);
                    for (int offset = 0; offset + Whb04bConstants.InputReportLength <= read; offset += Whb04bConstants.InputReportLength)
                        HandleReport(managed, offset);
                }
            }
            catch (Exception ex)
            {
                WriteLog("Reader thread error: " + ex);
                lost = true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                Marshal.FreeHGlobal(overlapped);
                evt.Close();
            }

            if (lost && !_stopping)
            {
                var h = Disconnected;
                if (h != null) h();
            }
        }

        private void HandleReport(byte[] data, int offset)
        {
            Whb04bInputReport r;
            if (!Whb04bInputReport.TryParse(data, offset, out r))
            {
                if (LogRawReports)
                    WriteLog("Ignored report: " + BitConverter.ToString(data, offset, Whb04bConstants.InputReportLength));
                return;
            }

            if (LogRawReports)
                WriteLog(r.ToString());

            if (ChecksumMode != ChecksumMode.Ignore && !r.IsChecksumValid(Seed))
            {
                ChecksumErrors++;
                // The full display block carries the seed too, so resend that instead of the
                // short seed-only packet (which can blank the LCD until the next full block).
                _forceDisplay = true;
                if (LogRawReports)
                    WriteLog("Checksum mismatch (seed 0x" + Seed.ToString("X2") + "): " + BitConverter.ToString(r.Raw));
                if (ChecksumMode == ChecksumMode.Strict)
                    return;
            }

            Raise(ReportReceived, r);

            // Knobs
            if (r.Axis != _lastAxis)
            {
                bool first = (int)_lastAxis == -1;
                _lastAxis = r.Axis;
                var h = AxisChanged;
                if (h != null) h(r.Axis);
                // The Mach3 driver ignores wheel movement in the report that changes the axis.
                if (!first) goto keys;
            }
            if (r.FeedKnob != _lastFeed)
            {
                _lastFeed = r.FeedKnob;
                var h = FeedKnobChanged;
                if (h != null) h(r.FeedKnob);
            }

            if (r.WheelDelta != 0)
            {
                var h = WheelMoved;
                if (h != null) h(r.Axis, r.WheelDelta);
            }

        keys:
            ushort keyWord = (ushort)(r.Key1 | (r.Key2 << 8));
            if (keyWord == _lastKeyWord)
                return;
            _lastKeyWord = keyWord;

            if (_pressedKey != Whb04bKey.None && _pressedKey != r.Key)
            {
                var up = KeyUp;
                var released = _pressedKey;
                _pressedKey = Whb04bKey.None;
                if (up != null) up(released);
            }

            if (r.Key != Whb04bKey.None && r.Key != _pressedKey)
            {
                _pressedKey = r.Key;
                var down = KeyDown;
                if (down != null) down(r.Key, r.FnHeld);
            }
        }

        private void Raise(Action<Whb04bInputReport> h, Whb04bInputReport r)
        {
            if (h != null) h(r);
        }

        private void WriteLog(string message)
        {
            var l = Log;
            if (l != null)
            {
                try { l(message); } catch { }
            }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
