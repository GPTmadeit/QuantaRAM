using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using QuantaRAM.Core;
using QuantaRAM.UI;

namespace QuantaRAM
{
    public partial class App : Application
    {
        Mutex mutex;
        EventWaitHandle showEvent;
        AppCore core;
        MainWindow window;
        TrayIcon tray;
        bool exiting;

        public TrayIcon Tray => tray;
        public static bool JustUpdated { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (s, ex) => { LogError(ex.Exception); ex.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, ex) => LogError(ex.ExceptionObject as Exception);

            // elevated update helper: swap the exe, relaunch, quit
            int ih = Array.IndexOf(e.Args, "--install-update");
            if (ih >= 0 && ih + 1 < e.Args.Length)
            {
                try { Updater.RunInstallHelper(e.Args[ih + 1]); } catch (Exception ex) { LogError(ex); }
                Shutdown();
                return;
            }

            bool restart = e.Args.Contains("--restart");
            JustUpdated = e.Args.Contains("--updated");
            bool trayStart = e.Args.Contains("--tray");

            // ---- single instance ----
            try
            {
                mutex = new Mutex(false, @"Local\QuantaRAM.SingleInstance");
                bool owned;
                try { owned = mutex.WaitOne(restart ? 10000 : 0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) { SignalExisting(); Shutdown(); return; }
            }
            catch (UnauthorizedAccessException) { SignalExisting(); Shutdown(); return; }

            var settings = AppSettings.Load();
            System.Threading.Tasks.Task.Run(Updater.Cleanup);

            // ---- render budget: cap every WPF animation, optionally take GPU out entirely ----
            if (settings.CpuRendering) RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = settings.FrameRate });
            AnimationHub.SetFps(settings.FrameRate);

            core = new AppCore(settings);
            window = new MainWindow(core);
            new WindowInteropHelper(window).EnsureHandle(); // hotkey works even when starting in tray
            tray = new TrayIcon(core, window);
            core.Start();

            if (trayStart || settings.StartMinimized) window.StartHidden();
            else window.Show();

            try
            {
                showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\QuantaRAM.Show");
                var t = new Thread(() =>
                {
                    while (!exiting)
                    {
                        if (showEvent.WaitOne(1000) && !exiting)
                            Dispatcher.BeginInvoke(new Action(() => window.ShowFromTray()));
                    }
                }) { IsBackground = true, Name = "QuantaShowListener" };
                t.Start();
            }
            catch { }
        }

        static void SignalExisting()
        {
            try { if (EventWaitHandle.TryOpenExisting(@"Local\QuantaRAM.Show", out var ev)) ev.Set(); } catch { }
        }

        public void ExitApp()
        {
            if (exiting) return;
            exiting = true;
            try { core?.Settings.Save(); } catch { }
            try { tray?.Dispose(); } catch { }
            try { core?.Stop(); } catch { }
            window?.ForceClose();
            try { mutex?.ReleaseMutex(); } catch { }
            Shutdown();
        }

        public void RestartElevated()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath, "--restart") { UseShellExecute = true, Verb = "runas" });
                ExitApp();
            }
            catch (Win32Exception) { /* user declined UAC */ }
        }

        static void LogError(Exception ex)
        {
            if (ex == null) return;
            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                File.AppendAllText(Path.Combine(AppSettings.Folder, "error.log"), $"[{DateTime.Now:u}] {ex}\n\n");
            }
            catch { }
        }
    }
}
