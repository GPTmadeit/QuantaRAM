using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace QuantaRAM.UI
{
    static class Geo
    {
        public static Point Polar(Point c, double r, double deg)
        {
            double a = deg * Math.PI / 180;
            return new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        }

        public static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            br.Freeze();
            return br;
        }
    }

    /// <summary>Circular arc, angles clockwise from 12 o'clock. Optional glowing head.</summary>
    public class Arc : FrameworkElement
    {
        public static readonly DependencyProperty StartAngleProperty = Reg(nameof(StartAngle), 0.0);
        public static readonly DependencyProperty SweepProperty = Reg(nameof(Sweep), 90.0);
        public static readonly DependencyProperty ThicknessProperty = Reg(nameof(Thickness), 4.0);
        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Arc),
            new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty HeadBrushProperty = DependencyProperty.Register(nameof(HeadBrush), typeof(Brush), typeof(Arc),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        static DependencyProperty Reg(string n, double d) => DependencyProperty.Register(n, typeof(double), typeof(Arc),
            new FrameworkPropertyMetadata(d, FrameworkPropertyMetadataOptions.AffectsRender));

        public double StartAngle { get => (double)GetValue(StartAngleProperty); set => SetValue(StartAngleProperty, value); }
        public double Sweep { get => (double)GetValue(SweepProperty); set => SetValue(SweepProperty, value); }
        public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
        public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
        public Brush HeadBrush { get => (Brush)GetValue(HeadBrushProperty); set => SetValue(HeadBrushProperty, value); }

        Pen pen;

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == StrokeProperty || e.Property == ThicknessProperty) pen = null;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight, t = Thickness;
            double r = Math.Min(w, h) / 2 - t * 1.5;
            double sweep = Math.Min(Sweep, 359.9);
            if (r <= 0 || sweep < 0.05) return;
            var c = new Point(w / 2, h / 2);
            if (pen == null)
            {
                pen = new Pen(Stroke, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                if (pen.CanFreeze) pen.Freeze();
            }
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(Geo.Polar(c, r, StartAngle), false, false);
                ctx.ArcTo(Geo.Polar(c, r, StartAngle + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
            if (HeadBrush != null)
            {
                var e = Geo.Polar(c, r, StartAngle + sweep);
                dc.DrawEllipse(HeadBrush, null, e, t * 1.5, t * 1.5);
                dc.DrawEllipse(Brushes.White, null, e, t * 0.3, t * 0.3);
            }
        }
    }

    /// <summary>Static ring of tick marks. Drawn once; rotate it cheaply with BitmapCache.</summary>
    public class TickRing : FrameworkElement
    {
        static readonly Pen Minor = MakePen(0x30), Major = MakePen(0x90);
        static Pen MakePen(byte a) { var p = new Pen(Geo.Frozen(a, 0, 229, 255), 1.2); p.Freeze(); return p; }
        public int Count { get; set; } = 90;

        protected override void OnRender(DrawingContext dc)
        {
            var c = new Point(ActualWidth / 2, ActualHeight / 2);
            double r = Math.Min(ActualWidth, ActualHeight) / 2 - 1;
            for (int i = 0; i < Count; i++)
            {
                double a = i * 360.0 / Count;
                bool major = i % 5 == 0;
                dc.DrawLine(major ? Major : Minor, Geo.Polar(c, r, a), Geo.Polar(c, r - (major ? 9 : 4), a));
            }
        }
    }

    /// <summary>Drifting particle network. ~70 nodes, frozen pens/brushes, one DrawingVisual.</summary>
    public class ParticleField : FrameworkElement
    {
        struct P { public double X, Y, VX, VY, R; public bool Alt; }
        enum FieldMode { Drift, Implode, Burst }

        const int Levels = 16;
        static readonly SolidColorBrush[] DotA = new SolidColorBrush[Levels], DotB = new SolidColorBrush[Levels];
        static readonly Pen[] LinePens = new Pen[Levels];

        readonly DrawingVisual dv = new DrawingVisual();
        readonly Random rnd = new Random(42);
        P[] ps = Array.Empty<P>();
        FieldMode mode;
        Point focus;
        double modeT;

        public int Density { get; set; } = 55;
        public bool Lines { get; set; } = true;

        static ParticleField()
        {
            for (int i = 0; i < Levels; i++)
            {
                double a = (i + 1) / (double)Levels;
                DotA[i] = Geo.Frozen((byte)(a * 200), 0, 229, 255);
                DotB[i] = Geo.Frozen((byte)(a * 200), 150, 110, 255);
                var pen = new Pen(Geo.Frozen((byte)(a * 70), 0, 229, 255), 1);
                pen.Freeze();
                LinePens[i] = pen;
            }
        }

        public ParticleField()
        {
            IsHitTestVisible = false;
            AddVisualChild(dv);
            Loaded += (s, e) => AnimationHub.Tick += OnTick;
            Unloaded += (s, e) => AnimationHub.Tick -= OnTick;
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => dv;

        public void Implode(Point p) { focus = p; mode = FieldMode.Implode; modeT = 0; }

        public void Burst(Point p)
        {
            focus = p; mode = FieldMode.Burst; modeT = 0;
            for (int i = 0; i < ps.Length; i++)
            {
                ref var q = ref ps[i];
                if (rnd.NextDouble() < 0.55)
                {
                    double a = rnd.NextDouble() * Math.PI * 2, d = rnd.NextDouble() * 40;
                    q.X = p.X + Math.Cos(a) * d; q.Y = p.Y + Math.Sin(a) * d;
                    double sp = 220 + rnd.NextDouble() * 380;
                    q.VX = Math.Cos(a) * sp; q.VY = Math.Sin(a) * sp;
                }
            }
        }

        public void Clear() { using (dv.RenderOpen()) { } }

        P Spawn(double w, double h, bool edge)
        {
            var p = new P
            {
                X = rnd.NextDouble() * w, Y = rnd.NextDouble() * h,
                VX = (rnd.NextDouble() - 0.5) * 24, VY = (rnd.NextDouble() - 0.5) * 24,
                R = 0.8 + rnd.NextDouble() * 1.8, Alt = rnd.NextDouble() < 0.3
            };
            if (edge)
            {
                switch (rnd.Next(4))
                {
                    case 0: p.X = -10; break;
                    case 1: p.X = w + 10; break;
                    case 2: p.Y = -10; break;
                    default: p.Y = h + 10; break;
                }
            }
            return p;
        }

        void OnTick(double dt)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 2 || h < 2 || !IsVisible) return;
            int target = Math.Clamp((int)(Density * w * h / (1280.0 * 720)), 0, 150);
            if (ps.Length != target)
            {
                var n = new P[target];
                for (int i = 0; i < target; i++) n[i] = i < ps.Length ? ps[i] : Spawn(w, h, false);
                ps = n;
            }
            modeT += dt;
            if (mode == FieldMode.Burst && modeT > 2.2) mode = FieldMode.Drift;

            Point mouse = Mouse.GetPosition(this);
            bool mouseIn = mouse.X >= 0 && mouse.Y >= 0 && mouse.X <= w && mouse.Y <= h;

            for (int i = 0; i < ps.Length; i++)
            {
                ref var p = ref ps[i];
                if (mode == FieldMode.Implode)
                {
                    double dx = focus.X - p.X, dy = focus.Y - p.Y, d = Math.Sqrt(dx * dx + dy * dy) + 0.01;
                    double acc = 420 + 9000 / (d + 30);
                    p.VX += dx / d * acc * dt; p.VY += dy / d * acc * dt;
                    // tangential swirl
                    p.VX += -dy / d * 140 * dt; p.VY += dx / d * 140 * dt;
                    p.VX *= 1 - 1.6 * dt; p.VY *= 1 - 1.6 * dt;
                    if (d < 22) p = Spawn(w, h, true);
                }
                else
                {
                    double sp = Math.Sqrt(p.VX * p.VX + p.VY * p.VY);
                    double max = mode == FieldMode.Burst ? 600 : 18;
                    if (sp > max || (mode == FieldMode.Drift && sp > 18)) { double k = 1 - 2.2 * dt; p.VX *= k; p.VY *= k; }
                    else if (sp < 4) { p.VX += (rnd.NextDouble() - 0.5) * 6; p.VY += (rnd.NextDouble() - 0.5) * 6; }
                }
                if (mouseIn)
                {
                    double dx = p.X - mouse.X, dy = p.Y - mouse.Y, d2 = dx * dx + dy * dy;
                    if (d2 < 120 * 120 && d2 > 1)
                    {
                        double d = Math.Sqrt(d2), f = (1 - d / 120) * 160 * dt;
                        p.VX += dx / d * f; p.VY += dy / d * f;
                    }
                }
                p.X += p.VX * dt; p.Y += p.VY * dt;
                if (mode != FieldMode.Implode)
                {
                    if (p.X < -20) p.X = w + 20; else if (p.X > w + 20) p.X = -20;
                    if (p.Y < -20) p.Y = h + 20; else if (p.Y > h + 20) p.Y = -20;
                }
            }

            using (var dc = dv.RenderOpen())
            {
                if (Lines)
                {
                    const double maxD = 125, maxD2 = maxD * maxD;
                    for (int i = 0; i < ps.Length; i++)
                    {
                        for (int j = i + 1; j < ps.Length; j++)
                        {
                            double dx = ps[i].X - ps[j].X, dy = ps[i].Y - ps[j].Y, d2 = dx * dx + dy * dy;
                            if (d2 >= maxD2) continue;
                            int lv = (int)((1 - Math.Sqrt(d2) / maxD) * (Levels - 1));
                            dc.DrawLine(LinePens[lv], new Point(ps[i].X, ps[i].Y), new Point(ps[j].X, ps[j].Y));
                        }
                        if (mouseIn)
                        {
                            double dx = ps[i].X - mouse.X, dy = ps[i].Y - mouse.Y, d2 = dx * dx + dy * dy;
                            if (d2 < 170 * 170)
                            {
                                int lv = (int)((1 - Math.Sqrt(d2) / 170) * (Levels - 1));
                                dc.DrawLine(LinePens[lv], new Point(ps[i].X, ps[i].Y), mouse);
                            }
                        }
                    }
                }
                for (int i = 0; i < ps.Length; i++)
                {
                    var p = ps[i];
                    int lv = Math.Clamp((int)(p.R / 2.6 * (Levels - 1)), 3, Levels - 1);
                    var c = new Point(p.X, p.Y);
                    if (p.R > 2.1) dc.DrawEllipse((p.Alt ? DotB : DotA)[2], null, c, p.R * 3.2, p.R * 3.2);
                    dc.DrawEllipse((p.Alt ? DotB : DotA)[lv], null, c, p.R, p.R);
                }
            }
        }
    }

    /// <summary>
    /// 2-series area chart (load %, standby %), 120 s window. Geometry is rebuilt once per sample;
    /// the smooth scroll between samples is a composition-level translate, so it costs almost nothing.
    /// </summary>
    public class HistoryChart : FrameworkElement
    {
        const int Cap = 122;
        readonly DrawingVisual gridDv = new DrawingVisual(), seriesDv = new DrawingVisual();
        readonly TranslateTransform scroll = new TranslateTransform();
        readonly double[] load = new double[Cap], stby = new double[Cap];
        int count;
        long seq;
        double threshold = -1;

        static readonly Pen GridPen = MkPen(Geo.Frozen(0x12, 255, 255, 255), 1);
        static readonly Pen LoadPen = MkPen(Geo.Frozen(0xFF, 0, 229, 255), 2);
        static readonly Pen StbyPen = MkPen(Geo.Frozen(0xCC, 150, 110, 255), 1.3);
        static readonly Pen ThreshPen;
        static readonly Brush LoadFill, StbyFill, Dot, DotGlow, LabelBrush = Geo.Frozen(0x70, 160, 185, 220);
        static readonly Typeface Face = new Typeface("Bahnschrift");

        static Pen MkPen(Brush b, double t) { var p = new Pen(b, t) { LineJoin = PenLineJoin.Round }; p.Freeze(); return p; }

        static HistoryChart()
        {
            var tp = new Pen(Geo.Frozen(0xAA, 255, 61, 110), 1) { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) };
            tp.Freeze(); ThreshPen = tp;
            var lf = new LinearGradientBrush(Color.FromArgb(0x55, 0, 229, 255), Color.FromArgb(0x00, 0, 229, 255), 90); lf.Freeze(); LoadFill = lf;
            var sf = new LinearGradientBrush(Color.FromArgb(0x30, 150, 110, 255), Color.FromArgb(0x00, 150, 110, 255), 90); sf.Freeze(); StbyFill = sf;
            Dot = Geo.Frozen(0xFF, 255, 255, 255);
            var g = new RadialGradientBrush(Color.FromArgb(0x90, 0, 229, 255), Color.FromArgb(0, 0, 229, 255)); g.Freeze(); DotGlow = g;
        }

        public double Threshold
        {
            get => threshold;
            set { if (Math.Abs(threshold - value) > 0.01) { threshold = value; DrawGrid(); } }
        }

        public HistoryChart()
        {
            ClipToBounds = true;
            seriesDv.Transform = scroll;
            AddVisualChild(gridDv);
            AddVisualChild(seriesDv);
            SizeChanged += (s, e) => { DrawGrid(); DrawSeries(); };
            IsVisibleChanged += (s, e) => { if (IsVisible) { DrawGrid(); DrawSeries(); } };
        }

        protected override int VisualChildrenCount => 2;
        protected override Visual GetVisualChild(int index) => index == 0 ? gridDv : seriesDv;

        double Step => ActualWidth / (Cap - 3);
        double Y(double v) => 6 + (ActualHeight - 10) * (1 - Math.Clamp(v, 0, 100) / 100);

        public void Push(double l, double s)
        {
            if (count == Cap)
            {
                Array.Copy(load, 1, load, 0, Cap - 1);
                Array.Copy(stby, 1, stby, 0, Cap - 1);
                count--;
            }
            load[count] = l; stby[count] = s; count++; seq++;
            if (!IsVisible) return;
            DrawSeries();
            if (AnimatedNumber.Animate && !AnimationHub.Paused)
                scroll.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -Step, TimeSpan.FromSeconds(1)));
            else { scroll.BeginAnimation(TranslateTransform.XProperty, null); scroll.X = 0; }
        }

        void DrawGrid()
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 10 || h < 10) return;
            using var dc = gridDv.RenderOpen();
            for (int k = 1; k < 4; k++)
            {
                dc.DrawLine(GridPen, new Point(0, Y(k * 25)), new Point(w, Y(k * 25)));
                dc.DrawText(new FormattedText($"{k * 25}%", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9, LabelBrush, 1.0),
                    new Point(4, Y(k * 25) - 12));
            }
            if (threshold > 0) dc.DrawLine(ThreshPen, new Point(0, Y(threshold)), new Point(w, Y(threshold)));
        }

        void DrawSeries()
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 10 || h < 10) return;
            double step = Step;
            // newest point sits one step right of the edge, so after the 1 s scroll it lands exactly on the edge
            double X(int i) => w + step - (count - 1 - i) * step;
            using var dc = seriesDv.RenderOpen();
            // vertical grid lines travel with the data (one every 10 samples)
            for (int i = 0; i < count; i++)
                if ((seq - count + i) % 10 == 0) dc.DrawLine(GridPen, new Point(X(i), 6), new Point(X(i), h - 4));
            if (count < 2) return;
            Series(dc, stby, StbyFill, StbyPen, X, h);
            Series(dc, load, LoadFill, LoadPen, X, h);
            var head = new Point(X(count - 1), Y(load[count - 1]));
            dc.DrawEllipse(DotGlow, null, head, 11, 11);
            dc.DrawEllipse(Dot, null, head, 2.8, 2.8);
        }

        void Series(DrawingContext dc, double[] data, Brush fill, Pen pen, Func<int, double> X, double h)
        {
            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(new Point(X(0), Y(data[0])), false, false);
                for (int i = 1; i < count; i++) ctx.LineTo(new Point(X(i), Y(data[i])), true, true);
            }
            line.Freeze();
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(X(0), h), true, true);
                for (int i = 0; i < count; i++) ctx.LineTo(new Point(X(i), Y(data[i])), false, false);
                ctx.LineTo(new Point(X(count - 1), h), false, false);
            }
            area.Freeze();
            dc.DrawGeometry(fill, null, area);
            dc.DrawGeometry(null, pen, line);
        }
    }

    /// <summary>TextBlock that counts smoothly toward its Value.</summary>
    public class AnimatedNumber : TextBlock
    {
        public static bool Animate = true;

        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(AnimatedNumber),
            new PropertyMetadata(0.0, (d, e) => ((AnimatedNumber)d).OnValue((double)e.NewValue)));
        static readonly DependencyProperty ShownProperty = DependencyProperty.Register("Shown", typeof(double), typeof(AnimatedNumber),
            new PropertyMetadata(0.0, (d, e) => ((AnimatedNumber)d).Render()));

        public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
        public string Format { get; set; } = "0";
        public string Suffix { get; set; } = "";
        public double Duration { get; set; } = 700;

        bool snapping;

        public AnimatedNumber() { Render(); }

        void OnValue(double v)
        {
            if (snapping || !Animate) { BeginAnimation(ShownProperty, null); SetValue(ShownProperty, v); Render(); return; }
            var a = new DoubleAnimation(v, TimeSpan.FromMilliseconds(Duration)) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } };
            BeginAnimation(ShownProperty, a, HandoffBehavior.SnapshotAndReplace);
        }

        public void Snap(double v) { snapping = true; Value = v; snapping = false; BeginAnimation(ShownProperty, null); SetValue(ShownProperty, v); Render(); }

        void Render() => Text = ((double)GetValue(ShownProperty)).ToString(Format) + Suffix;
    }

    /// <summary>Telemetry row: label, counting value, animated ratio bar, sub-caption.</summary>
    public class StatTile : Border
    {
        readonly TextBlock lbl, unit, sub;
        readonly AnimatedNumber num;
        readonly Border fill;
        readonly Rectangle tick;
        readonly ScaleTransform sx = new ScaleTransform(0, 1);
        readonly SolidColorBrush border = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255));
        Color accent = Colors.Cyan;

        public StatTile()
        {
            Margin = new Thickness(0, 3, 0, 3);
            Padding = new Thickness(12, 6, 12, 6);
            CornerRadius = new CornerRadius(6);
            Background = Geo.Frozen(0x0A, 255, 255, 255);
            BorderBrush = border;
            BorderThickness = new Thickness(1);
            VerticalAlignment = VerticalAlignment.Stretch;

            var g = new Grid { VerticalAlignment = VerticalAlignment.Center };
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var head = new DockPanel();
            tick = new Rectangle { Width = 6, Height = 6, RadiusX = 1, RadiusY = 1, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(tick, Dock.Left);
            head.Children.Add(tick);
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
            num = new AnimatedNumber { FontSize = 18, Foreground = Brushes.White, FontFamily = new FontFamily("Bahnschrift SemiLight") };
            unit = new TextBlock { FontSize = 11, Margin = new Thickness(4, 0, 0, 2), VerticalAlignment = VerticalAlignment.Bottom, Foreground = Geo.Frozen(0xFF, 0x6F, 0x84, 0xA8) };
            right.Children.Add(num); right.Children.Add(unit);
            DockPanel.SetDock(right, Dock.Right);
            head.Children.Add(right);
            lbl = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = Geo.Frozen(0xFF, 0x9F, 0xB2, 0xD4) };
            head.Children.Add(lbl);
            g.Children.Add(head);

            var track = new Grid { Height = 3, Margin = new Thickness(0, 5, 0, 3) };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = Geo.Frozen(0x18, 255, 255, 255) });
            fill = new Border { CornerRadius = new CornerRadius(1.5), RenderTransform = sx };
            track.Children.Add(fill);
            Grid.SetRow(track, 1);
            g.Children.Add(track);

            sub = new TextBlock { FontSize = 10, Foreground = Geo.Frozen(0xFF, 0x5B, 0x6E, 0x92), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(sub, 2);
            g.Children.Add(sub);
            Child = g;

            MouseEnter += (s, e) => border.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromArgb(0x80, accent.R, accent.G, accent.B), TimeSpan.FromMilliseconds(200)));
            MouseLeave += (s, e) => border.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromArgb(0x14, 255, 255, 255), TimeSpan.FromMilliseconds(400)));
        }

        public string Label { get => lbl.Text; set => lbl.Text = value; }

        public Brush Accent
        {
            get => fill.Background;
            set
            {
                fill.Background = value;
                tick.Fill = value;
                if (value is SolidColorBrush sc) accent = sc.Color;
                else if (value is GradientBrush gb && gb.GradientStops.Count > 0) accent = gb.GradientStops[0].Color;
            }
        }

        public void Update(double value, string format, string unitText, double ratio, string subText)
        {
            num.Format = format;
            unit.Text = unitText;
            num.Value = value;
            sub.Text = subText;
            ratio = Math.Clamp(ratio, 0, 1);
            if (Math.Abs(sx.ScaleX - ratio) < 0.002) return;
            if (AnimatedNumber.Animate)
                sx.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(ratio, TimeSpan.FromMilliseconds(650)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else { sx.BeginAnimation(ScaleTransform.ScaleXProperty, null); sx.ScaleX = ratio; }
        }
    }
}
