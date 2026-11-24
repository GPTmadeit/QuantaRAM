using System;
using QuantaRAM.Core;
using SD = System.Drawing;
using SD2 = System.Drawing.Drawing2D;
using WF = System.Windows.Forms;

namespace QuantaRAM.UI
{
    /// <summary>Notification-area icon whose ring shows live memory load.</summary>
    public sealed class TrayIcon : IDisposable
    {
        readonly WF.NotifyIcon ni;
        readonly AppCore core;
        readonly MainWindow win;
        int lastPct = -1;
        bool lastGame;
        IntPtr lastHicon = IntPtr.Zero;

        public TrayIcon(AppCore core, MainWindow win)
        {
            this.core = core;
            this.win = win;
            var menu = new WF.ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, Font = new SD.Font("Segoe UI", 9.5f) };
            menu.Items.Add(Item("Open Quanta", () => win.ShowFromTray()));
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(Item("Quick purge", () => _ = core.CleanAsync(0, false, "TRAY")));
            menu.Items.Add(Item("Balanced purge", () => _ = core.CleanAsync(1, false, "TRAY")));
            menu.Items.Add(Item("Deep purge", () => _ = core.CleanAsync(2, false, "TRAY")));
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(Item("Exit", () => ((App)System.Windows.Application.Current).ExitApp()));

            ni = new WF.NotifyIcon { ContextMenuStrip = menu, Text = "Quanta Memory Core", Visible = true };
            ni.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) win.ShowFromTray(); };
            core.Sampled += OnSample;
            core.CleanFinished += OnCleaned;
            Draw(0, false);
        }

        static WF.ToolStripMenuItem Item(string text, Action a)
        {
            var it = new WF.ToolStripMenuItem(text) { ForeColor = SD.Color.FromArgb(230, 241, 255), Padding = new WF.Padding(6, 3, 6, 3) };
            it.Click += (s, e) => a();
            return it;
        }

        void OnSample(MemorySample s)
        {
            int pct = (int)Math.Round(s.Load);
            bool game = core.Snapshot.IsGaming;
            if (pct != lastPct || game != lastGame) Draw(pct, game);
            string tip = $"Quanta · RAM {pct}% · {s.Used / 1073741824.0:0.0}/{s.Total / 1073741824.0:0.0} GB";
            if (game) tip += " · Shield ON";
            ni.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        void OnCleaned(CleanResult r)
        {
            if (r == null || !core.Settings.Notifications || win.IsVisible) return;
            ni.ShowBalloonTip(3500, "Quanta · purge complete",
                $"{Cleaner.Fmt(r.Freed)} reclaimed ({r.Profile}, {r.Trigger})" + (r.GameName != null && !r.GameIncluded ? $"\n{r.GameName} was shielded." : ""),
                WF.ToolTipIcon.None);
        }

        public void Balloon(string title, string text) => ni.ShowBalloonTip(3000, title, text, WF.ToolTipIcon.None);

        void Draw(int pct, bool game)
        {
            lastPct = pct; lastGame = game;
            using var bmp = new SD.Bitmap(32, 32);
            using (var g = SD.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SD2.SmoothingMode.AntiAlias;
                g.Clear(SD.Color.Transparent);
                using (var bg = new SD.SolidBrush(SD.Color.FromArgb(255, 8, 12, 24))) g.FillEllipse(bg, 1, 1, 30, 30);
                using (var track = new SD.Pen(SD.Color.FromArgb(60, 255, 255, 255), 4)) g.DrawArc(track, 4, 4, 24, 24, 135, 270);
                SD.Color c = pct >= 85 ? SD.Color.FromArgb(255, 61, 110) : pct >= 65 ? SD.Color.FromArgb(255, 181, 71) : SD.Color.FromArgb(0, 229, 255);
                using (var arc = new SD.Pen(c, 4) { StartCap = SD2.LineCap.Round, EndCap = SD2.LineCap.Round })
                    g.DrawArc(arc, 4, 4, 24, 24, 135, Math.Max(4, 270f * pct / 100f));
                var hex = new SD.PointF[6];
                for (int i = 0; i < 6; i++)
                {
                    double a = Math.PI / 3 * i - Math.PI / 2;
                    hex[i] = new SD.PointF(16 + 6.5f * (float)Math.Cos(a), 16 + 6.5f * (float)Math.Sin(a));
                }
                using (var hb = new SD.SolidBrush(game ? SD.Color.FromArgb(255, 43, 214) : SD.Color.FromArgb(0, 229, 255))) g.FillPolygon(hb, hex);
            }
            IntPtr h = bmp.GetHicon();
            var old = ni.Icon;
            ni.Icon = SD.Icon.FromHandle(h);
            old?.Dispose();
            if (lastHicon != IntPtr.Zero) Native.DestroyIcon(lastHicon);
            lastHicon = h;
        }

        public void Dispose()
        {
            core.Sampled -= OnSample;
            core.CleanFinished -= OnCleaned;
            ni.Visible = false;
            ni.Dispose();
            if (lastHicon != IntPtr.Zero) Native.DestroyIcon(lastHicon);
        }

        sealed class DarkRenderer : WF.ToolStripProfessionalRenderer
        {
            public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }
            protected override void OnRenderItemText(WF.ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Selected ? SD.Color.FromArgb(0, 229, 255) : SD.Color.FromArgb(230, 241, 255);
                base.OnRenderItemText(e);
            }
        }

        sealed class DarkColors : WF.ProfessionalColorTable
        {
            static readonly SD.Color Bg = SD.Color.FromArgb(11, 16, 30), Sel = SD.Color.FromArgb(18, 34, 56), Line = SD.Color.FromArgb(30, 48, 78);
            public override SD.Color ToolStripDropDownBackground => Bg;
            public override SD.Color MenuBorder => Line;
            public override SD.Color MenuItemBorder => Sel;
            public override SD.Color MenuItemSelected => Sel;
            public override SD.Color SeparatorDark => Line;
            public override SD.Color SeparatorLight => Bg;
            public override SD.Color ImageMarginGradientBegin => Bg;
            public override SD.Color ImageMarginGradientMiddle => Bg;
            public override SD.Color ImageMarginGradientEnd => Bg;
        }
    }
}
