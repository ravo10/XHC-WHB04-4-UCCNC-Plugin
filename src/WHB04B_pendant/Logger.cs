using System;
using System.IO;

namespace Plugins
{
    /// <summary>Thread-safe log file next to the plugin DLL (XHC-WHB04B-4_pendant.log).</summary>
    internal sealed class Logger
    {
        private const long MaxBytes = 2 * 1024 * 1024;
        private readonly object _lock = new object();
        private readonly string _path;

        public Logger()
        {
            try
            {
                string dir = Path.GetDirectoryName(typeof(Logger).Assembly.Location);
                _path = Path.Combine(dir ?? ".", "XHC-WHB04B-4_pendant.log");
            }
            catch
            {
                _path = Path.Combine(Path.GetTempPath(), "XHC-WHB04B-4_pendant.log");
            }
        }

        public string FilePath { get { return _path; } }

        /// <summary>Set from the "Debug log" checkbox. When false only errors and connect/disconnect are written.</summary>
        public volatile bool DebugEnabled;

        /// <summary>Writes the message only when debug logging is on.</summary>
        public void Debug(string message)
        {
            if (DebugEnabled) Write(message);
        }

        public void Write(string message)
        {
            lock (_lock)
            {
                try
                {
                    var fi = new FileInfo(_path);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        string old = _path + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(_path, old);
                    }
                    File.AppendAllText(_path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
                catch
                {
                    // Logging must never break the plugin.
                }
            }
        }
    }
}
