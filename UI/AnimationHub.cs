using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace QuantaRAM.UI
{
    /// <summary>
    /// One shared, frame-capped clock for every custom-drawn element plus a registry of ambient
    /// animations, so everything can be frozen at once (minimised, in tray, game running...).
    /// </summary>
    public static class AnimationHub
    {
        static readonly DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Render);
        static readonly Stopwatch sw = Stopwatch.StartNew();
        static readonly List<AnimationClock> clocks = new List<AnimationClock>();
        static double last;

        public static event Action<double> Tick;
        public static double Time => sw.Elapsed.TotalSeconds;
        public static bool IsRunning => timer.IsEnabled;
        public static bool Paused { get; private set; } = true;
        public static int Fps { get; private set; } = 30;

        static AnimationHub()
        {
            timer.Tick += (s, e) =>
            {
                double now = sw.Elapsed.TotalSeconds;
                double dt = Math.Min(0.1, now - last);
                last = now;
                Tick?.Invoke(dt);
            };
            SetFps(30);
        }

        public static void SetFps(int fps)
        {
            Fps = Math.Clamp(fps, 10, 120);
            timer.Interval = TimeSpan.FromMilliseconds(1000.0 / Fps);
        }

        public static AnimationClock Ambient(IAnimatable target, DependencyProperty dp, AnimationTimeline anim)
        {
            anim.RepeatBehavior = RepeatBehavior.Forever;
            var clock = anim.CreateClock();
            target.ApplyAnimationClock(dp, clock);
            clocks.Add(clock);
            if (Paused) clock.Controller?.Pause();
            return clock;
        }

        public static void SetPaused(bool paused)
        {
            if (Paused == paused) return;
            Paused = paused;
            foreach (var c in clocks)
            {
                if (paused) c.Controller?.Pause();
                else c.Controller?.Resume();
            }
            if (paused) timer.Stop();
            else { last = sw.Elapsed.TotalSeconds; timer.Start(); }
        }
    }
}
