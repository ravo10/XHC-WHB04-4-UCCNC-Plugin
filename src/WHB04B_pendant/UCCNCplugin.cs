using System;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace Plugins
{
    /// <summary>
    /// UCCNC plugin entry point. UCCNC looks for the class Plugins.UCCNCplugin and calls
    /// these *_event methods by name (same set as the stock XHC-HB04 / UCR200 plugins).
    /// </summary>
    public class UCCNCplugin
    {
        public const string Version = "0.4.7";

        private Plugininterface.Entry UC;
        private Logger _log;
        private PendantController _controller;
        private volatile bool _shuttingDown;
        private int _inLoop;
        private ConfigForm _configForm;

        // A window handle created on the thread that calls Init_event (UCCNC's main thread),
        // used to run the loop there when "Run on UCCNC's main thread" is ticked.
        private Control _mainInvoker;
        private int _mainLoopQueued;
        private bool _loggedLoopThread;

        public UCCNCplugin()
        {
        }

        // Called right after the plugin is loaded.
        public void Init_event(Plugininterface.Entry UC)
        {
            this.UC = UC;
            _log = new Logger();
            _log.Debug("Plugin v" + Version + " initialised on thread " + Thread.CurrentThread.ManagedThreadId);
            try
            {
                _mainInvoker = new Control();
                var handle = _mainInvoker.Handle; // forces the handle onto this thread
            }
            catch (Exception ex)
            {
                _mainInvoker = null;
                _log.Write("Could not create main-thread invoker: " + ex.Message);
            }
        }

        // Called to read the plugin name/author/version for the plugin list.
        public Plugininterface.Entry.Pluginproperties Getproperties_event(Plugininterface.Entry.Pluginproperties Properties)
        {
            Properties.author = "XHC-WHB04-4-UCCNC-Plugin project";
            Properties.pluginname = "XHC WHB04B-4 wireless pendant";
            Properties.pluginversion = Version;
            return Properties;
        }

        // Called when the user presses "Configure" in the plugin list.
        public void Configure_event()
        {
            ShowConfig();
        }

        // Called when UCCNC has started, if "Call startup" is ticked for this plugin.
        // Like the stock HB04 plugin, this also opens the plugin window.
        public void Startup_event()
        {
            EnsureController();
            ShowConfig(false); // open without taking the keyboard focus from UCCNC (arrow-key jogging)
        }

        private bool EnsureController()
        {
            try
            {
                if (_controller == null)
                    _controller = new PendantController(UC, _log);
            }
            catch (Exception ex)
            {
                _log.Write("Startup failed: " + ex);
            }
            return _controller != null;
        }

        // Called when the user presses "Show" in the plugin list.
        public void Showup_event()
        {
            ShowConfig();
        }

        // Called when UCCNC is closing.
        public void Shutdown_event()
        {
            _shuttingDown = true;
            try
            {
                var f = _configForm;
                if (f != null && !f.IsDisposed)
                    f.BeginInvoke(new MethodInvoker(f.Close));
            }
            catch
            {
            }

            try
            {
                if (_controller != null)
                {
                    _controller.Dispose();
                    _controller = null;
                }
                _log.Debug("Plugin shut down");
            }
            catch
            {
            }
        }

        // Called on every UCCNC loop cycle.
        public void Loop_event()
        {
            if (_shuttingDown) return;
            if (_controller == null && !EnsureController()) return;

            if (!_loggedLoopThread)
            {
                _loggedLoopThread = true;
                _log.Debug("Loop_event runs on thread " + Thread.CurrentThread.ManagedThreadId);
            }

            var inv = _mainInvoker;
            if (_controller.Settings.RunOnMainThread && inv != null && inv.IsHandleCreated && inv.InvokeRequired)
            {
                // Queue one loop at a time on the main thread; never block UCCNC's loop thread.
                if (Interlocked.Exchange(ref _mainLoopQueued, 1) == 1) return;
                try
                {
                    inv.BeginInvoke(new MethodInvoker(() =>
                    {
                        try { RunLoop(); }
                        finally { Interlocked.Exchange(ref _mainLoopQueued, 0); }
                    }));
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref _mainLoopQueued, 0);
                    _log.Write("Main-thread loop failed: " + ex.Message);
                }
                return;
            }

            RunLoop();
        }

        private void RunLoop()
        {
            if (_shuttingDown || _controller == null) return;
            if (Interlocked.Exchange(ref _inLoop, 1) == 1) return;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                _controller.Loop();
            }
            catch (Exception ex)
            {
                _log.Write("Loop error: " + ex);
            }
            finally
            {
                Interlocked.Exchange(ref _inLoop, 0);
            }
        }

        // Called when a UCCNC screen button is pressed.
        public void Buttonpress_event(int buttonnumber, bool onscreen)
        {
        }

        private void ShowConfig()
        {
            ShowConfig(true);
        }

        private void ShowConfig(bool activate)
        {
            if (!EnsureController())
            {
                MessageBox.Show("The WHB04B plugin could not start. See XHC-WHB04B-4_pendant.log in the Plugins folder.", "XHC WHB04B-4");
                return;
            }

            _log.Debug("Opening settings window (thread " + Thread.CurrentThread.ManagedThreadId + ")");

            MethodInvoker open = () =>
            {
                try
                {
                    var f = _configForm;
                    if (f == null || f.IsDisposed)
                    {
                        f = new ConfigForm(UC, _controller, _log) { NoActivate = !activate };
                        f.FormClosed += (s, e) => _configForm = null;
                        _configForm = f;
                    }
                    if (f.WindowState == FormWindowState.Minimized)
                        f.WindowState = FormWindowState.Normal;
                    f.Show();
                    if (activate)
                    {
                        f.BringToFront();
                        f.Activate();
                    }
                }
                catch (Exception ex)
                {
                    _log.Write("Config window error: " + ex);
                    MessageBox.Show("Could not open the settings window:\n" + ex.Message, "XHC WHB04B-4");
                }
            };

            // Like the stock HB04 plugin, the window lives on UCCNC's main (UI) thread.
            var inv = _mainInvoker;
            if (inv != null && inv.IsHandleCreated && inv.InvokeRequired)
                inv.BeginInvoke(open);
            else
                open();
        }
    }
}
