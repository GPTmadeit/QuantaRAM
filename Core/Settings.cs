using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace QuantaRAM.Core
{
    public sealed class AppSettings
    {
        // profile: 0 quick, 1 balanced, 2 deep, 3 custom
        public int Profile { get; set; } = 1;

        // automation
        public bool AutoCleanEnabled { get; set; }
        public double AutoThreshold { get; set; } = 85;
        public bool IntervalEnabled { get; set; }
        public double IntervalMinutes { get; set; } = 30;
        public bool PauseAutoWhileGaming { get; set; }
        public bool HotkeyEnabled { get; set; } = true;
        public bool Notifications { get; set; } = true;

        // game shield
        public bool GameShieldEnabled { get; set; } = true;
        public bool IncludeGameProcesses { get; set; }
        public bool AllowSystemWideWhileGaming { get; set; }
        public bool DetectFullscreen { get; set; } = true;
        public bool ProtectForeground { get; set; } = true;

        // visuals
        public int AnimationLevel { get; set; } = 2; // 2 full, 1 reduced, 0 off
        public int FrameRate { get; set; } = 30;
        public bool CpuRendering { get; set; }
        public bool PauseVisualsWhileGaming { get; set; } = true;

        // system
        public bool StartWithWindows { get; set; }
        public bool StartMinimized { get; set; }
        public bool MinimizeToTray { get; set; } = true;
        public bool TrayHintShown { get; set; }

        // updates
        public bool AutoCheckUpdates { get; set; } = true;
        public bool AutoInstallUpdates { get; set; }

        // custom profile
        public bool CTrim { get; set; } = true;
        public bool CLowStandby { get; set; } = true;
        public bool CStandby { get; set; } = true;
        public bool CModified { get; set; }
        public bool CFileCache { get; set; }
        public bool CCombine { get; set; } = true;

        public ObservableCollection<string> CustomGames { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<string> Exclusions { get; set; } = new ObservableCollection<string>();

        // lifetime stats
        public long LifetimeFreed { get; set; }
        public int LifetimeCleans { get; set; }

        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuantaRAM");
        static string FilePath => Path.Combine(Folder, "settings.json");

        static readonly JsonSerializerOptions Opts = new JsonSerializerOptions { WriteIndented = true };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Opts) ?? new AppSettings();
            }
            catch { }
            return new AppSettings();
        }

        readonly object saveGate = new object();
        public void Save()
        {
            try
            {
                lock (saveGate)
                {
                    Directory.CreateDirectory(Folder);
                    var tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
                    File.Move(tmp, FilePath, true);
                }
            }
            catch { }
        }

        public static string Normalize(string exe)
        {
            if (string.IsNullOrWhiteSpace(exe)) return null;
            exe = exe.Trim().Trim('"').ToLowerInvariant();
            exe = Path.GetFileName(exe);
            if (exe.EndsWith(".exe")) exe = exe.Substring(0, exe.Length - 4);
            return exe.Length == 0 ? null : exe;
        }
    }
}
