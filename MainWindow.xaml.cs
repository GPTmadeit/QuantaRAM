using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using QuantaRAM.Core;
using QuantaRAM.UI;

namespace QuantaRAM
{
    public partial class MainWindow : Window
    {
        readonly AppCore core;
        readonly AppSettings S;
        readonly ObservableCollection<LogEntry> logs = new ObservableCollection<LogEntry>();
        readonly ObservableCollection<ProcessItem> procs = new ObservableCollection<ProcessItem>();
        readonly System.Collections.Generic.Dictionary<int, ProcessItem> procMap = new System.Collections.Generic.Dictionary<int, ProcessItem>();
        readonly ListCollectionView procView;
        readonly DispatcherTimer saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        readonly LinearGradientBrush arcBrush;
        readonly GradientStop arcA, arcB;
        FrameworkElement[] pages;
        int page;
        bool forceClose, firstSample = true, hotkeyOn;
        string lastBound;
        string procFilter = "ALL";
        DateTime cleanStart;
        HwndSource hwnd;

        static readonly Color Cyan = Color.FromRgb(0, 229, 255), Violet = Color.FromRgb(150, 110, 255),
            Magenta = Color.FromRgb(255, 43, 214), Amber = Color.FromRgb(255, 181, 71), Red = Color.FromRgb(255, 61, 110),
            Green = Color.FromRgb(0, 255, 163);

        public MainWindow(AppCore core)
        {
            this.core = core;
            S = core.Settings;
            InitializeComponent();
            DataContext = S;
            pages = new FrameworkElement[] { PageDash, PageProc, PageAuto, PageSet };

            arcA = new GradientStop(Cyan, 0);
            arcB = new GradientStop(Violet, 1);
            arcBrush = new LinearGradientBrush(new GradientStopCollection { arcA, arcB }, new Point(0, 1), new Point(1, 0));
            ValueArc.Stroke = arcBrush;
            var head = new RadialGradientBrush(Color.FromArgb(0xCC, 0, 229, 255), Color.FromArgb(0, 0, 229, 255));
            head.Freeze();
            ValueArc.HeadBrush = head;

            LogList.ItemsSource = logs;
            procView = new ListCollectionView(procs);
            procView.SortDescriptions.Add(new SortDescription(nameof(ProcessItem.WorkingSet), ListSortDirection.Descending));
            procView.IsLiveSorting = true;
            procView.LiveSortingProperties.Add(nameof(ProcessItem.WorkingSet));
            procView.IsLiveFiltering = true;
            procView.LiveFilteringProperties.Add(nameof(ProcessItem.Category));
            procView.LiveFilteringProperties.Add(nameof(ProcessItem.IsExcluded));
            procView.Filter = ProcFilterPredicate;
            ProcList.ItemsSource = procView;

            core.Sampled += OnSample;
            core.SnapshotChanged += OnSnapshot;
            core.Logged += (m, k) => AddLog(m, k);
            core.CleanStarted += OnCleanStarted;
            core.CleanFinished += OnCleanFinished;

            saveTimer.Tick += (s, e) => { saveTimer.Stop(); S.Save(); };
            foreach (var p in new FrameworkElement[] { PageAuto, PageSet, IncludeOnce })
            {
                p.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(SettingChanged));
                p.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(SettingChanged));
                p.AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((s, e) => SettingChanged(s, null)));
            }

            Loaded += OnLoaded;
            StateChanged += (s, e) => { Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0); UpdateAnimState(); };
            IsVisibleChanged += (s, e) => UpdateAnimState();
            Activated += (s, e) => UpdateAnimState();
            Deactivated += (s, e) => UpdateAnimState();
        }

        // =====================================================================
        // window plumbing
        // =====================================================================
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var h = new WindowInteropHelper(this).Handle;
            int v = 2; Native.DwmSetWindowAttribute(h, 33, ref v, 4);           // rounded corners (Win11)
            v = 1; Native.DwmSetWindowAttribute(h, 20, ref v, 4);               // dark mode frame
            v = 0x00503A1A; Native.DwmSetWindowAttribute(h, 34, ref v, 4);      // subtle blue border
            hwnd = HwndSource.FromHwnd(h);
            hwnd.AddHook(WndProc);
            ApplyHotkey();
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == 0x0312 && w.ToInt32() == 0x5157)
            {
                handled = true;
                _ = core.CleanAsync(S.Profile, false, "HOTKEY");
            }
            return IntPtr.Zero;
        }

        void ApplyHotkey()
        {
            if (hwnd == null) return;
            if (hotkeyOn) { Native.UnregisterHotKey(hwnd.Handle, 0x5157); hotkeyOn = false; }
            string bound = null;
            if (S.HotkeyEnabled)
            {
                // MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_NOREPEAT=0x4000
                var candidates = new (uint mods, uint vk, string name)[]
                {
                    (0x0003, 0x4D, "CTRL+ALT+M"), (0x0007, 0x4D, "CTRL+ALT+SHIFT+M"), (0x0003, 0x7B, "CTRL+ALT+F12"), (0x0006, 0x7B, "CTRL+SHIFT+F12")
                };
                foreach (var c in candidates)
                {
                    if (Native.RegisterHotKey(hwnd.Handle, 0x5157, c.mods | 0x4000, c.vk)) { hotkeyOn = true; bound = c.name; break; }
                }
                if (!hotkeyOn) AddLog("Every candidate hotkey is taken by other apps", LogKind.Warn);
                else if (bound != "CTRL+ALT+M" && bound != lastBound) AddLog($"Ctrl+Alt+M is taken by another app; using {bound}", LogKind.Info);
            }
            lastBound = bound;
            StatusHotkey.Text = S.HotkeyEnabled ? (hotkeyOn ? "HOTKEY · " + bound : "HOTKEY · UNAVAILABLE") : "HOTKEY · OFF";
            if (ProfilePanel != null) ProfilePanel.Tag = hotkeyOn ? bound : "";
            if (HotkeyBox != null) HotkeyBox.Content = "Global hotkey  ·  " + (bound ?? "Ctrl + Alt + M");
        }

        public void StartHidden() { /* handle already created; stay in tray */ }

        public void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true; Topmost = false;
            Focus();
        }

        public void ForceClose() { forceClose = true; Close(); }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!forceClose && S.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                if (!S.TrayHintShown)
                {
                    S.TrayHintShown = true; S.Save();
                    ((App)Application.Current).Tray?.Balloon("Quanta is still running", "Automation and Ctrl+Alt+M stay active. Right-click the tray icon to exit.");
                }
                return;
            }
            if (!forceClose) { e.Cancel = true; ((App)Application.Current).ExitApp(); return; }
            if (hotkeyOn && hwnd != null) Native.UnregisterHotKey(hwnd.Handle, 0x5157);
            base.OnClosing(e);
        }

        void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        void Close_Click(object sender, RoutedEventArgs e) => Close();
        void Elevate_Click(object sender, RoutedEventArgs e) { S.Save(); ((App)Application.Current).RestartElevated(); }

        // =====================================================================
        // startup & ambient motion
        // =====================================================================
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            // privilege chip
            var accent = core.IsAdmin ? Green : Amber;
            AdminChip.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B));
            AdminChip.Background = new SolidColorBrush(Color.FromArgb(0x14, accent.R, accent.G, accent.B));
            AdminDot.Fill = new SolidColorBrush(accent);
            AdminText.Foreground = new SolidColorBrush(accent);
            AdminText.Text = core.IsAdmin ? "ADMIN · FULL ACCESS" : "LIMITED MODE";
            ElevateBtn.Visibility = core.IsAdmin ? Visibility.Collapsed : Visibility.Visible;

            // restore choices
            (new[] { Prof0, Prof1, Prof2, Prof3 })[Math.Clamp(S.Profile, 0, 3)].IsChecked = true;
            (S.AnimationLevel >= 2 ? AnimFull : S.AnimationLevel == 1 ? AnimReduced : AnimOff).IsChecked = true;
            (S.FrameRate <= 24 ? Fps24 : S.FrameRate >= 60 ? Fps60 : Fps30).IsChecked = true;
            (S.CpuRendering ? RenderCpu : RenderGpu).IsChecked = true;
            UpdatePurgeCaption();
            UpdateStatus();

            InitUpdates();
            StartAmbient();
            ApplyVisualLevel();
            PlayIntro();

            AddLog("Quanta core online. Telemetry stream active", LogKind.Info);
            AddLog(core.IsAdmin ? "Privileges: full. All purge stages available" : "Limited mode: trimming only. ELEVATE unlocks cache purges", core.IsAdmin ? LogKind.Ok : LogKind.Warn);
            AddLog(S.GameShieldEnabled ? "Game Shield armed" : "Game Shield is disabled in Settings", S.GameShieldEnabled ? LogKind.Shield : LogKind.Warn);
        }

        void StartAmbient()
        {
            static DoubleAnimation Spin(double secs, bool reverse = false) =>
                new DoubleAnimation(reverse ? 360 : 0, reverse ? 0 : 360, TimeSpan.FromSeconds(secs));
            DoubleAnimation Breathe(double from, double to, double secs) =>
                new DoubleAnimation(from, to, TimeSpan.FromSeconds(secs)) { AutoReverse = true, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };

            AnimationHub.Ambient(RotOuter, RotateTransform.AngleProperty, Spin(90));
            AnimationHub.Ambient(RotSegs, RotateTransform.AngleProperty, Spin(28));
            AnimationHub.Ambient(RotTicks, RotateTransform.AngleProperty, Spin(140, true));
            AnimationHub.Ambient(RotScanner, RotateTransform.AngleProperty, Spin(5));
            AnimationHub.Ambient(LogoRot, RotateTransform.AngleProperty, Spin(14));
            AnimationHub.Ambient(LogoRot2, RotateTransform.AngleProperty, Spin(7, true));
            AnimationHub.Ambient(LogoDot, OpacityProperty, Breathe(0.25, 1, 1.1));
            AnimationHub.Ambient(StatusDot, OpacityProperty, Breathe(0.3, 1, 1.0));
            AnimationHub.Ambient(BlinkCursor, OpacityProperty, new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromSeconds(1.1),
                KeyFrames = { new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)), new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0.5)) }
            });
            AnimationHub.Ambient(GaugeGlow, OpacityProperty, Breathe(0.35, 0.7, 3.2));
            AnimationHub.Ambient(TitleLineS, ScaleTransform.ScaleXProperty, Breathe(0.15, 1, 6));
            AnimationHub.Ambient(ScanT, TranslateTransform.YProperty, new DoubleAnimation(-200, 1400, TimeSpan.FromSeconds(11)));
            AnimationHub.Ambient((TranslateTransform)Nebula1.RenderTransform, TranslateTransform.XProperty, Breathe(0, 160, 17));
            AnimationHub.Ambient((TranslateTransform)Nebula1.RenderTransform, TranslateTransform.YProperty, Breathe(0, 90, 23));
            AnimationHub.Ambient((TranslateTransform)Nebula2.RenderTransform, TranslateTransform.XProperty, Breathe(0, -180, 19));
            AnimationHub.Ambient((TranslateTransform)Nebula2.RenderTransform, TranslateTransform.YProperty, Breathe(0, -80, 29));

            // reactor pulse ring
            var pulseDur = TimeSpan.FromSeconds(3.2);
            AnimationHub.Ambient(PulseS, ScaleTransform.ScaleXProperty, new DoubleAnimation(0.85, 1.32, pulseDur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            AnimationHub.Ambient(PulseS, ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, 1.32, pulseDur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            AnimationHub.Ambient(PulseRing, OpacityProperty, new DoubleAnimation(0.7, 0, pulseDur));

            // shield sonar ring
            var sDur = TimeSpan.FromSeconds(2.2);
            AnimationHub.Ambient(ShieldPulseS, ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, 1.35, sDur));
            AnimationHub.Ambient(ShieldPulseS, ScaleTransform.ScaleYProperty, new DoubleAnimation(0.6, 1.35, sDur));

            // purge button breathing ring
            PurgeBtn.ApplyTemplate();
            if (PurgeBtn.Template.FindName("Ring", PurgeBtn) is FrameworkElement ring)
                AnimationHub.Ambient(ring, OpacityProperty, Breathe(0.15, 0.85, 1.6));
        }

        void PlayIntro()
        {
            var els = new FrameworkElement[] { ProfilePanel, GaugeBox, TelemetryPanel, ShieldPanel, PurgeBtn, ChartPanel, LogPanel };
            if (S.AnimationLevel == 0) return;
            for (int i = 0; i < els.Length; i++)
            {
                var el = els[i];
                var tt = new TranslateTransform(0, 26);
                el.RenderTransform = tt;
                el.Opacity = 0;
                var begin = TimeSpan.FromMilliseconds(90 * i + 120);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(650)) { BeginTime = begin });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(26, 0, TimeSpan.FromMilliseconds(750)) { BeginTime = begin, EasingFunction = ease });
            }
            var gs = new ScaleTransform(0.85, 0.85, 0, 0);
            Gauge.RenderTransformOrigin = new Point(0.5, 0.5);
            Gauge.RenderTransform = gs;
            var back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
            gs.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(900)) { BeginTime = TimeSpan.FromMilliseconds(200), EasingFunction = back });
            gs.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(900)) { BeginTime = TimeSpan.FromMilliseconds(200), EasingFunction = back });
        }

        void ApplyVisualLevel()
        {
            int lvl = S.AnimationLevel;
            AnimatedNumber.Animate = lvl > 0;
            Particles.Density = lvl >= 2 ? 70 : 34;
            Particles.Lines = true;
            Particles.Visibility = lvl > 0 ? Visibility.Visible : Visibility.Collapsed;
            ScanLine.Visibility = lvl >= 2 ? Visibility.Visible : Visibility.Collapsed;
            PulseRing.Visibility = lvl >= 2 ? Visibility.Visible : Visibility.Collapsed;
            Nebula1.Visibility = Nebula2.Visibility = lvl >= 2 ? Visibility.Visible : Visibility.Collapsed;
            UpdateAnimState();
            UpdateStatus();
        }

        /// <summary>Every animation stops when nobody can see it.</summary>
        void UpdateAnimState()
        {
            bool visible = IsVisible && WindowState != WindowState.Minimized;
            bool gamePause = core.Snapshot.IsGaming && S.PauseVisualsWhileGaming && !IsActive;
            bool run = visible && S.AnimationLevel > 0 && !gamePause;
            AnimationHub.SetPaused(!run);
            StatusPaused.Visibility = visible && gamePause ? Visibility.Visible : Visibility.Collapsed;
        }

        // =====================================================================
        // live telemetry
        // =====================================================================
        static double GB(ulong b) => b / 1073741824.0;

        void OnSample(MemorySample s)
        {
            double total = Math.Max(1, s.Total);
            Chart.Threshold = S.AutoCleanEnabled ? S.AutoThreshold : -1;
            Chart.Push(s.Load, s.Standby / total * 100);   // keeps history even while hidden
            if (!IsVisible) return;

            LoadText.Value = s.Load;
            UsedText.Text = $"{GB(s.Used):0.0} / {GB(s.Total):0.0} GB";
            AnimateArc(ValueArc, s.Load / 100 * 270, firstSample ? 1600 : 800);
            AnimateArc(StandbyArc, s.Standby / total * 270, firstSample ? 1800 : 800);
            firstSample = false;

            // state colouring
            string state; Color a, b, chip;
            if (s.Load >= 85) { state = "CRITICAL"; a = Red; b = Magenta; chip = Red; }
            else if (s.Load >= 65) { state = "ELEVATED"; a = Amber; b = Magenta; chip = Amber; }
            else { state = "NOMINAL"; a = Cyan; b = Violet; chip = Green; }
            if (LoadState.Text != state)
            {
                LoadState.Text = state;
                var d = TimeSpan.FromMilliseconds(900);
                arcA.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(a, d));
                arcB.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(b, d));
                LoadState.Foreground = new SolidColorBrush(chip);
                LoadStateChip.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, chip.R, chip.G, chip.B));
                LoadStateChip.Background = new SolidColorBrush(Color.FromArgb(0x16, chip.R, chip.G, chip.B));
            }

            StAvail.Update(GB(s.Available), "0.00", "GB", s.Available / total, $"{100 - s.Load:0}% of {GB(s.Total):0.0} GB physical");
            StStandby.Update(GB(s.Standby), "0.00", "GB", s.Standby / total, s.HasLists ? "Cached files, reusable instantly" : "Estimated from system cache");
            StModified.Update(s.Modified / 1048576.0, "0", "MB", s.Modified / total * 4, "Dirty pages waiting for disk");
            StFree.Update(GB(s.Free), "0.00", "GB", s.Free / total, "Truly empty pages");
            StCommit.Update(GB(s.CommitUsed), "0.0", "GB", s.CommitLimit > 0 ? (double)s.CommitUsed / s.CommitLimit : 0,
                $"of {GB(s.CommitLimit):0.0} GB limit (RAM + pagefile)");
            StCpu.Update(s.Cpu, "0", "%", s.Cpu / 100, "Processor utility");

            MiniMeterText.Text = $"{s.Load:0}%";
            if (AnimatedNumber.Animate)
                MiniMeterS.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(s.Load / 100, TimeSpan.FromMilliseconds(700)) { EasingFunction = new CubicEase() });
            else MiniMeterS.ScaleY = s.Load / 100;

            if (page == 2) { LifeFreed.Text = Cleaner.Fmt(S.LifetimeFreed); LifeCleans.Text = S.LifetimeCleans.ToString("N0"); }
            UpdateStatus();
        }

        static void AnimateArc(Arc arc, double to, double ms)
        {
            if (Math.Abs(arc.Sweep - to) < 0.08) return;
            if (!AnimatedNumber.Animate) { arc.BeginAnimation(Arc.SweepProperty, null); arc.Sweep = to; return; }
            arc.BeginAnimation(Arc.SweepProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
            { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } }, HandoffBehavior.SnapshotAndReplace);
        }

        void UpdateStatus()
        {
            var snap = core.Snapshot;
            StatusShield.Text = !S.GameShieldEnabled ? "SHIELD · OFF" : snap.IsGaming ? $"SHIELD · ACTIVE ({snap.Primary.DisplayName})" : "SHIELD · STANDBY";
            StatusShield.Foreground = snap.IsGaming && S.GameShieldEnabled ? (Brush)FindResource("B.Cyan") : (Brush)FindResource("B.Muted");
            string auto = S.AutoCleanEnabled ? $"≥{S.AutoThreshold:0}%" : "";
            if (S.IntervalEnabled) auto += (auto.Length > 0 ? " + " : "") + $"{S.IntervalMinutes:0}m";
            StatusAuto.Text = "AUTO · " + (auto.Length > 0 ? auto : "OFF") + (S.PauseAutoWhileGaming && snap.IsGaming ? " (PAUSED)" : "");
            if (core.LastClean is DateTime lc)
            {
                var ago = DateTime.Now - lc;
                string t = ago.TotalSeconds < 60 ? "just now" : ago.TotalMinutes < 60 ? $"{ago.TotalMinutes:0}m ago" : $"{ago.TotalHours:0}h ago";
                StatusLast.Text = $"LAST PURGE · {t} · {Cleaner.Fmt(core.LastResult?.Freed ?? 0)}";
            }
            StatusRender.Text = $"RENDER · {(S.CpuRendering ? "CPU-ONLY" : "GPU-LITE")} {S.FrameRate} FPS" + (S.AnimationLevel == 0 ? " · STATIC" : "");
        }

        // =====================================================================
        // game shield
        // =====================================================================
        bool shieldShown;

        void OnSnapshot(GameSnapshot snap)
        {
            bool active = snap.IsGaming && S.GameShieldEnabled;
            if (active)
            {
                ShieldState.Text = "ACTIVE";
                ShieldState.Foreground = (Brush)FindResource("B.Cyan");
                ShieldGame.Text = snap.Primary.DisplayName;
                ShieldDetail.Text = $"Detected via {snap.Primary.Reason}. {snap.Shielded.Count} processes locked: game, child processes, install folder, overlays and anti-cheat. Only low-priority cache is touched.";
            }
            else
            {
                ShieldState.Text = S.GameShieldEnabled ? "STANDBY" : "DISABLED";
                ShieldState.Foreground = (Brush)FindResource(S.GameShieldEnabled ? "B.Muted" : "B.Amber");
                ShieldGame.Text = snap.IsGaming ? snap.Primary.DisplayName + " (unprotected)" : "No game detected";
                ShieldDetail.Text = S.GameShieldEnabled
                    ? "Watching game libraries, known titles and fullscreen apps. When a game runs, its processes, overlays and anti-cheat are never trimmed."
                    : "Game Shield is off. Running games will be trimmed like any other app.";
            }
            if (active != shieldShown)
            {
                shieldShown = active;
                var d = TimeSpan.FromMilliseconds(500);
                ShieldIcon.Stroke = (Brush)FindResource(active ? "B.Cyan" : "B.Dim");
                ShieldPad.Fill = (Brush)FindResource(active ? "B.Magenta" : "B.Dim");
                ShieldIcon.Fill = new SolidColorBrush(Color.FromArgb((byte)(active ? 0x44 : 0x22), 0, 229, 255));
                ShieldPulse.BeginAnimation(OpacityProperty, new DoubleAnimation(active ? 0.8 : 0, d));
                ShieldGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(active ? 0.9 : 0.3, d));
                if (active && AnimatedNumber.Animate)
                {
                    var st = new ScaleTransform(1, 1);
                    ShieldIcon.RenderTransformOrigin = new Point(0.5, 0.5);
                    ShieldIcon.RenderTransform = st;
                    var pop = new DoubleAnimation(1.35, 1, TimeSpan.FromMilliseconds(600)) { EasingFunction = new ElasticEase { Oscillations = 2, Springiness = 4 } };
                    st.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
                }
            }
            if (page == 1) RefreshProcs(snap);
            UpdateAnimState();
            UpdateStatus();
        }

        // =====================================================================
        // purge choreography
        // =====================================================================
        async void Purge_Click(object sender, RoutedEventArgs e)
        {
            bool once = IncludeOnce.IsChecked == true;
            await core.CleanAsync(S.Profile, once, "MANUAL");
            IncludeOnce.IsChecked = false;
        }

        void OnCleanStarted(string trigger)
        {
            cleanStart = DateTime.Now;
            PurgeBtn.IsEnabled = false;
            PurgeBtn.Content = "PURGING…";
            PurgeBtn.Tag = "Reclaiming idle memory";
            CoreLabel.Text = "PURGING";
            ResultPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));
            if (!AnimatedNumber.Animate || !IsVisible) return;

            ScanFast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)));
            RotScanFast.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.7)) { RepeatBehavior = RepeatBehavior.Forever });
            GaugeGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.5, 1, TimeSpan.FromSeconds(0.35)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            try { Particles.Implode(Gauge.TranslatePoint(new Point(210, 210), Particles)); } catch { }
        }

        async void OnCleanFinished(CleanResult r)
        {
            // keep the show going for at least ~1.4 s so it reads as a sequence
            var elapsed = DateTime.Now - cleanStart;
            if (AnimatedNumber.Animate && IsVisible && elapsed.TotalMilliseconds < 1400)
                await Task.Delay(1400 - (int)elapsed.TotalMilliseconds);

            PurgeBtn.IsEnabled = true;
            PurgeBtn.Content = "PURGE MEMORY";
            CoreLabel.Text = "MEMORY LOAD";
            UpdatePurgeCaption();
            ScanFast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)));
            RotScanFast.BeginAnimation(RotateTransform.AngleProperty, null);
            GaugeGlow.BeginAnimation(OpacityProperty, null);
            UpdateStatus();
            if (r == null) return;

            Point center;
            try { center = Gauge.TranslatePoint(new Point(210, 210), Particles); } catch { center = new Point(ActualWidth / 2, ActualHeight / 2); }
            if (AnimatedNumber.Animate && IsVisible)
            {
                Particles.Burst(center);
                Shock(Shock1, 0);
                Shock(Shock2, 140);
            }

            double mb = r.Freed / 1048576.0;
            FreedText.Snap(0);
            if (mb >= 1024) { FreedText.Format = "0.00"; FreedText.Suffix = " GB"; FreedText.Value = mb / 1024; }
            else { FreedText.Format = "0"; FreedText.Suffix = " MB"; FreedText.Value = mb; }
            string detail = $"{r.Trimmed} trimmed · {r.Duration.TotalSeconds:0.0}s · {r.Profile}";
            if (r.Shielded > 0) detail = $"{r.GameName} shielded ({r.Shielded}) · " + detail;
            else if (r.GameIncluded) detail = $"{r.GameName} included · " + detail;
            ResultDetail.Text = detail;
            var tt = (TranslateTransform)ResultPanel.RenderTransform;
            ResultPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)));
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(600)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut } });
        }

        static void Shock(System.Windows.Shapes.Ellipse el, int delayMs)
        {
            var st = (ScaleTransform)el.RenderTransform;
            var begin = TimeSpan.FromMilliseconds(delayMs);
            var d = TimeSpan.FromMilliseconds(1000);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.7, 1.55, d) { BeginTime = begin, EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.7, 1.55, d) { BeginTime = begin, EasingFunction = ease });
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0.95, 0, d) { BeginTime = begin });
        }

        void UpdatePurgeCaption()
        {
            string p = CleanOptions.Names[Math.Clamp(S.Profile, 0, 3)];
            p = p.Substring(0, 1) + p.Substring(1).ToLowerInvariant();
            PurgeBtn.Tag = $"{p} profile · Game Shield {(S.GameShieldEnabled ? "aware" : "off")}";
        }

        void Profile_Click(object sender, RoutedEventArgs e)
        {
            S.Profile = int.Parse((string)((FrameworkElement)sender).Tag);
            UpdatePurgeCaption();
            saveTimer.Stop(); saveTimer.Start();
        }

        // =====================================================================
        // log
        // =====================================================================
        void AddLog(string msg, LogKind kind)
        {
            logs.Add(LogEntry.Make(msg, kind));
            while (logs.Count > 80) logs.RemoveAt(0);
            LogScroll.ScrollToEnd();
        }

        // =====================================================================
        // navigation
        // =====================================================================
        void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (pages == null) return;
            int idx = sender == NavProc ? 1 : sender == NavAuto ? 2 : sender == NavSet ? 3 : 0;
            if (idx == page) return;
            var old = pages[page];
            var neu = pages[idx];
            page = idx;
            old.Visibility = Visibility.Collapsed;
            neu.Visibility = Visibility.Visible;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            if (AnimatedNumber.Animate)
            {
                neu.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)));
                if (neu.RenderTransform is TranslateTransform tt)
                    tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(22, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
                NavIndT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(idx * 60 + 10, TimeSpan.FromMilliseconds(380)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } });
            }
            else NavIndT.Y = idx * 60 + 10;

            if (idx == 1) RefreshProcs(core.Snapshot);
            if (idx == 2) { LifeFreed.Text = Cleaner.Fmt(S.LifetimeFreed); LifeCleans.Text = S.LifetimeCleans.ToString("N0"); }
            S.Save();
        }

        // =====================================================================
        // process matrix
        // =====================================================================
        void RefreshProcs(GameSnapshot snap)
        {
            if (snap?.Processes == null) return;
            var seen = new System.Collections.Generic.HashSet<int>();
            long max = 1, total = 0;
            foreach (var p in snap.Processes) { if (p.WorkingSet > max) max = p.WorkingSet; total += p.WorkingSet; }
            foreach (var p in snap.Processes)
            {
                if (p.Pid == 0) continue;
                seen.Add(p.Pid);
                if (!procMap.TryGetValue(p.Pid, out var it) || it.Name != p.Name)
                {
                    if (it != null) procs.Remove(it);
                    it = new ProcessItem(p);
                    procMap[p.Pid] = it;
                    it.Update(p, max, S);
                    procs.Add(it);
                }
                else it.Update(p, max, S);
            }
            foreach (var pid in procMap.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                procs.Remove(procMap[pid]);
                procMap.Remove(pid);
            }
            PcTotal.Text = seen.Count.ToString();
            PcShield.Text = snap.Shielded.Count.ToString();
            PcWs.Text = Cleaner.Fmt(total);
        }

        bool ProcFilterPredicate(object o)
        {
            var p = (ProcessItem)o;
            string q = ProcSearch?.Text?.Trim();
            if (!string.IsNullOrEmpty(q) && p.DisplayName.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                !p.Pid.ToString().StartsWith(q)) return false;
            return procFilter switch
            {
                "SHIELD" => p.Category is ProcCategory.Game or ProcCategory.GameRelated or ProcCategory.AntiCheat,
                "TRIM" => p.Category == ProcCategory.Normal,
                "EXCL" => p.Category == ProcCategory.Excluded || p.IsExcluded,
                _ => true
            };
        }

        void ProcSearch_TextChanged(object sender, TextChangedEventArgs e) => procView?.Refresh();

        void ProcFilter_Click(object sender, RoutedEventArgs e)
        {
            procFilter = (string)((FrameworkElement)sender).Tag;
            procView.Refresh();
        }

        void RowExclude_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is not ProcessItem it) return;
            Toggle(S.Exclusions, it.Name, it.IsExcluded);
            AddLog(it.IsExcluded ? $"{it.DisplayName} added to exclusions" : $"{it.DisplayName} removed from exclusions", LogKind.Info);
            S.Save(); core.RequestScan();
        }

        void RowGame_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is not ProcessItem it) return;
            Toggle(S.CustomGames, it.Name, it.IsCustomGame);
            AddLog(it.IsCustomGame ? $"{it.DisplayName} marked as a game" : $"{it.DisplayName} removed from game list", LogKind.Shield);
            S.Save(); core.RequestScan();
        }

        async void RowTrim_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is not ProcessItem it) return;
            if (it.Category is ProcCategory.Game or ProcCategory.GameRelated && !(S.IncludeGameProcesses))
            {
                var res = MessageBox.Show(this, $"{it.DisplayName} is shielded as part of a running game.\n\nTrim it anyway?", "Quanta · Game Shield",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
            }
            try
            {
                long freed = await Task.Run(() => Cleaner.TrimOne(it.Pid));
                AddLog($"{it.DisplayName}: {Cleaner.Fmt(freed)} paged out", LogKind.Ok);
            }
            catch (Exception ex) { AddLog($"{it.DisplayName}: {ex.Message}" + (core.IsAdmin ? "" : " (try ELEVATE)"), LogKind.Warn); }
            core.RequestScan();
        }

        static void Toggle(ObservableCollection<string> list, string name, bool on)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (on && !list.Contains(name)) list.Add(name);
            if (!on) list.Remove(name);
        }

        // =====================================================================
        // settings
        // =====================================================================
        void SettingChanged(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            saveTimer.Stop(); saveTimer.Start();
            ApplyHotkey();
            UpdatePurgeCaption();
            UpdateAnimState();
            UpdateStatus();
            if (sender == PageSet && e?.OriginalSource is CheckBox) core.RequestScan();
        }

        void Seg_Click(object sender, RoutedEventArgs e)
        {
            var rb = (RadioButton)sender;
            string tag = (string)rb.Tag;
            switch (rb.GroupName)
            {
                case "Anim":
                    S.AnimationLevel = int.Parse(tag);
                    ApplyVisualLevel();
                    if (S.AnimationLevel == 0) Particles.Clear();
                    break;
                case "Fps":
                    S.FrameRate = int.Parse(tag);
                    AnimationHub.SetFps(S.FrameRate);
                    AddLog($"Frame cap set to {S.FrameRate} FPS (fully applies after restart)", LogKind.Info);
                    break;
                case "Render":
                    bool cpu = tag == "cpu";
                    if (cpu != S.CpuRendering)
                    {
                        S.CpuRendering = cpu;
                        RenderOptions.ProcessRenderMode = cpu ? RenderMode.SoftwareOnly : RenderMode.Default;
                        AddLog(cpu ? "Renderer: CPU only. GPU is not used at all" : "Renderer: GPU-lite (hardware compositing)", LogKind.Info);
                    }
                    break;
            }
            S.Save();
            UpdateStatus();
        }

        void Startup_Click(object sender, RoutedEventArgs e)
        {
            AppCore.ApplyStartup(S.StartWithWindows, core.IsAdmin);
            AddLog(S.StartWithWindows ? $"Launch at sign-in enabled{(core.IsAdmin ? " (elevated task)" : "")}" : "Launch at sign-in disabled", LogKind.Info);
            S.Save();
        }

        void AddGame_Click(object sender, RoutedEventArgs e) => AddTo(S.CustomGames, GameAddBox, "game list");
        void AddExcl_Click(object sender, RoutedEventArgs e) => AddTo(S.Exclusions, ExclAddBox, "exclusions");
        void GameAddBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) AddTo(S.CustomGames, GameAddBox, "game list"); }
        void ExclAddBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) AddTo(S.Exclusions, ExclAddBox, "exclusions"); }

        void AddTo(ObservableCollection<string> list, TextBox box, string what)
        {
            var n = AppSettings.Normalize(box.Text);
            box.Text = "";
            if (n == null || list.Contains(n)) return;
            list.Add(n);
            AddLog($"{n}.exe added to {what}", LogKind.Info);
            S.Save(); core.RequestScan();
        }

        void Chip_Click(object sender, RoutedEventArgs e)
        {
            var b = (Button)sender;
            if (b.DataContext is not string n) return;
            ((string)b.Tag == "games" ? S.CustomGames : S.Exclusions).Remove(n);
            S.Save(); core.RequestScan();
        }

        // =====================================================================
        // updates (GitHub Releases)
        // =====================================================================
        UpdateInfo pendingUpdate;
        bool updateBusy;
        readonly DispatcherTimer updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };

        void InitUpdates()
        {
            VersionText.Text = Updater.CurrentText;
            UpdVersion.Text = "Installed " + Updater.CurrentText;
            if (App.JustUpdated) AddLog($"Updated to {Updater.CurrentText}", LogKind.Ok);
            updateTimer.Tick += async (s, e) => { if (S.AutoCheckUpdates) await CheckForUpdates(false); };
            updateTimer.Start();
            var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            first.Tick += async (s, e) => { first.Stop(); if (S.AutoCheckUpdates) await CheckForUpdates(false); };
            first.Start();
        }

        async Task CheckForUpdates(bool manual)
        {
            if (updateBusy) return;
            updateBusy = true;
            CheckUpdBtn.IsEnabled = false;
            UpdStatus.Text = "Checking GitHub…";
            try
            {
                var info = await Updater.CheckAsync();
                if (info == null)
                {
                    pendingUpdate = null;
                    UpdStatus.Text = $"Up to date · checked {DateTime.Now:HH:mm}";
                    UpdateChip.Visibility = Visibility.Collapsed;
                    InstallUpdBtn.Visibility = Visibility.Collapsed;
                    if (manual) AddLog($"Quanta is up to date ({Updater.CurrentText})", LogKind.Ok);
                }
                else
                {
                    bool isNew = pendingUpdate?.Tag != info.Tag;
                    pendingUpdate = info;
                    string v = "v" + info.Version.ToString(3);
                    UpdStatus.Text = info.AssetUrl != null ? $"{v} is available" : $"{v} is available (download from GitHub)";
                    UpdateChip.Content = $"UPDATE {v}";
                    InstallUpdBtn.Content = info.AssetUrl != null && Updater.CanSelfUpdate ? $"INSTALL {v}" : "OPEN RELEASE";
                    InstallUpdBtn.Visibility = Visibility.Visible;
                    if (UpdateChip.Visibility != Visibility.Visible)
                    {
                        UpdateChip.Visibility = Visibility.Visible;
                        UpdateChip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)));
                    }
                    if (isNew)
                    {
                        AddLog($"Update available: {v}", LogKind.Info);
                        if (!IsVisible && S.Notifications)
                            ((App)Application.Current).Tray?.Balloon("Quanta update available", $"{v} is ready to install.");
                    }
                    if (!manual && S.AutoInstallUpdates && !core.Snapshot.IsGaming && !core.IsCleaning && info.AssetUrl != null && Updater.CanSelfUpdate)
                    {
                        updateBusy = false;
                        await InstallUpdate();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                UpdStatus.Text = "Couldn't reach GitHub · " + ex.Message;
                if (manual) AddLog("Update check failed: " + ex.Message, LogKind.Warn);
            }
            finally
            {
                updateBusy = false;
                CheckUpdBtn.IsEnabled = true;
            }
        }

        async Task InstallUpdate()
        {
            var info = pendingUpdate;
            if (info == null || updateBusy) return;
            if (info.AssetUrl == null || !Updater.CanSelfUpdate)
            {
                Updater.OpenReleasePage(info.PageUrl);
                if (!Updater.CanSelfUpdate) AddLog("Dev build: self-update only works for the published single-file exe", LogKind.Warn);
                return;
            }
            updateBusy = true;
            CheckUpdBtn.IsEnabled = InstallUpdBtn.IsEnabled = false;
            UpdProgressBox.Visibility = Visibility.Visible;
            UpdProgressS.ScaleX = 0;
            string v = "v" + info.Version.ToString(3);
            AddLog($"Downloading {v}…", LogKind.Info);
            try
            {
                var progress = new Progress<double>(p =>
                {
                    UpdProgressS.ScaleX = p;
                    UpdStatus.Text = $"Downloading {v} · {p * 100:0}%";
                });
                string file = await Updater.DownloadAsync(info, progress);
                UpdStatus.Text = $"Verified {v} · restarting…";
                AddLog($"{v} downloaded and verified. Restarting", LogKind.Ok);
                S.Save();
                await Task.Delay(600);
                if (Updater.Apply(file)) ((App)Application.Current).ExitApp();
            }
            catch (Exception ex)
            {
                UpdStatus.Text = "Update failed · " + ex.Message;
                AddLog("Update failed: " + ex.Message, LogKind.Error);
                UpdProgressBox.Visibility = Visibility.Collapsed;
            }
            finally
            {
                updateBusy = false;
                CheckUpdBtn.IsEnabled = InstallUpdBtn.IsEnabled = true;
            }
        }

        async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckForUpdates(true);
        async void InstallUpdate_Click(object sender, RoutedEventArgs e) => await InstallUpdate();
        void UpdateChip_Click(object sender, RoutedEventArgs e) => NavSet.IsChecked = true;
    }
}