using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;

namespace QuantaRAM.Core
{
    /// <summary>Owns telemetry, game scanning, automation and purge execution. UI-thread object.</summary>
    public sealed class AppCore
    {
        public AppSettings Settings { get; }
        public bool IsAdmin { get; }
        public MemorySample Last { get; private set; }
        public GameSnapshot Snapshot { get; private set; } = new GameSnapshot();
        public bool IsCleaning { get; private set; }
        public DateTime? LastClean { get; private set; }
        public CleanResult LastResult { get; private set; }

        public event Action<MemorySample> Sampled;
        public event Action<GameSnapshot> SnapshotChanged;
        public event Action<string, LogKind> Logged;
        public event Action<string> CleanStarted;
        public event Action<CleanResult> CleanFinished;

        readonly MemoryMonitor monitor = new MemoryMonitor();
        readonly GameDetector detector = new GameDetector();
        readonly DispatcherTimer timer;
        readonly Dispatcher ui;
        bool scanning;
        int tick;
        DateTime lastAuto = DateTime.MinValue;
        DateTime lastInterval = DateTime.Now;

        public AppCore(AppSettings settings)
        {
            Settings = settings;
            ui = Dispatcher.CurrentDispatcher;
            using (var id = WindowsIdentity.GetCurrent())
                IsAdmin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            if (IsAdmin)
            {
                Native.EnablePrivilege("SeProfileSingleProcessPrivilege");
                Native.EnablePrivilege("SeIncreaseQuotaPrivilege");
                Native.EnablePrivilege("SeDebugPrivilege");
            }
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) => OnTick();
        }

        public void Start()
        {
            Last = monitor.Sample();
            timer.Start();
            RequestScan();
        }

        public void Stop()
        {
            timer.Stop();
            monitor.Dispose();
        }

        void OnTick()
        {
            Last = monitor.Sample();
            Sampled?.Invoke(Last);
            if (++tick % 3 == 0) RequestScan();
            CheckAutomation();
        }

        public async void RequestScan()
        {
            if (scanning) return;
            scanning = true;
            try
            {
                var snap = await Task.Run(() => detector.Scan(Settings));
                var prev = Snapshot;
                Snapshot = snap;
                if (prev.Primary?.Pid != snap.Primary?.Pid)
                {
                    if (snap.Primary != null)
                        Log($"Game detected: {snap.Primary.DisplayName} ({snap.Primary.Reason}). Shield engaged on {snap.Shielded.Count} processes", LogKind.Shield);
                    else if (prev.Primary != null)
                        Log($"{prev.Primary.DisplayName} closed. Shield standing by", LogKind.Info);
                }
                SnapshotChanged?.Invoke(snap);
            }
            catch (Exception ex) { Log("Scan error: " + ex.Message, LogKind.Error); }
            finally { scanning = false; }
        }

        void CheckAutomation()
        {
            if (IsCleaning) return;
            var s = Settings;
            if (Snapshot.IsGaming && s.PauseAutoWhileGaming) return;
            var now = DateTime.Now;
            if (s.AutoCleanEnabled && Last.Load >= s.AutoThreshold && (now - lastAuto).TotalMinutes >= 2)
            {
                lastAuto = now;
                _ = CleanAsync(s.Profile, false, $"AUTO ≥{s.AutoThreshold:0}%");
                return;
            }
            if (s.IntervalEnabled && (now - lastInterval).TotalMinutes >= Math.Max(1, s.IntervalMinutes))
            {
                lastInterval = now;
                _ = CleanAsync(s.Profile, false, $"SCHEDULE {s.IntervalMinutes:0}m");
            }
        }

        public void Log(string msg, LogKind kind = LogKind.Info) => Logged?.Invoke(msg, kind);

        public async Task<CleanResult> CleanAsync(int profile, bool includeGamesOnce, string trigger)
        {
            if (IsCleaning) return null;
            IsCleaning = true;
            string pname = CleanOptions.Names[Math.Clamp(profile, 0, 3)];
            CleanStarted?.Invoke(trigger);
            Log($"Purge sequence: {pname} profile · {trigger}", LogKind.Info);

            var opts = CleanOptions.For(profile, Settings);
            Action<string, LogKind> log = (m, k) => ui.InvokeAsync(() => Log(m, k));
            CleanResult r = null;
            try
            {
                r = await Task.Run(() =>
                {
                    var snap = detector.Scan(Settings);
                    return Cleaner.Run(opts, snap, Settings, includeGamesOnce, IsAdmin, monitor, log);
                });
                r.Profile = pname;
                r.Trigger = trigger;
                LastClean = DateTime.Now;
                LastResult = r;
                Settings.LifetimeFreed += r.Freed;
                Settings.LifetimeCleans++;
                Settings.Save();
                Log($"Purge complete: {Cleaner.Fmt(r.Freed)} reclaimed in {r.Duration.TotalSeconds:0.0}s", LogKind.Ok);
            }
            catch (Exception ex) { Log("Purge failed: " + ex.Message, LogKind.Error); }
            finally
            {
                IsCleaning = false;
                lastInterval = DateTime.Now;
            }
            Last = monitor.Sample();
            Sampled?.Invoke(Last);
            CleanFinished?.Invoke(r);
            RequestScan();
            return r;
        }

        public static void ApplyStartup(bool enable, bool admin)
        {
            string exe = Environment.ProcessPath;
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (enable && !admin) k?.SetValue("QuantaRAM", $"\"{exe}\" --tray");
                else k?.DeleteValue("QuantaRAM", false);
            }
            catch { }
            if (!admin) return;
            // Elevated: a logon task runs us with full rights and no UAC prompt.
            string args = enable
                ? $"/Create /TN \"QuantaRAM\" /TR \"\\\"{exe}\\\" --tray\" /SC ONLOGON /RL HIGHEST /F"
                : "/Delete /TN \"QuantaRAM\" /F";
            try
            {
                using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false });
                p?.WaitForExit(5000);
            }
            catch { }
        }
    }
}
