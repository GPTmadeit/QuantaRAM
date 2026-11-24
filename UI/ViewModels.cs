using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using QuantaRAM.Core;

namespace QuantaRAM.UI
{
    public sealed class LogEntry
    {
        public string Time { get; set; }
        public string Tag { get; set; }
        public string Message { get; set; }
        public Brush TagBrush { get; set; }

        static readonly Brush Info = Geo.Frozen(0xFF, 0x6F, 0x84, 0xA8), Ok = Geo.Frozen(0xFF, 0x00, 0xFF, 0xA3),
            Warn = Geo.Frozen(0xFF, 0xFF, 0xB5, 0x47), Shield = Geo.Frozen(0xFF, 0x00, 0xE5, 0xFF), Err = Geo.Frozen(0xFF, 0xFF, 0x3D, 0x6E);

        public static LogEntry Make(string msg, LogKind k) => new LogEntry
        {
            Time = System.DateTime.Now.ToString("HH:mm:ss"),
            Message = msg,
            Tag = k switch { LogKind.Ok => "OK", LogKind.Warn => "WARN", LogKind.Shield => "SHLD", LogKind.Error => "ERR", _ => "SYS" },
            TagBrush = k switch { LogKind.Ok => Ok, LogKind.Warn => Warn, LogKind.Shield => Shield, LogKind.Error => Err, _ => Info }
        };
    }

    public sealed class ProcessItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        void On([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        static readonly Brush[] Avatars =
        {
            Geo.Frozen(0x40, 0x00, 0xE5, 0xFF), Geo.Frozen(0x40, 0x96, 0x6E, 0xFF), Geo.Frozen(0x40, 0xFF, 0x2B, 0xD6),
            Geo.Frozen(0x40, 0x00, 0xFF, 0xA3), Geo.Frozen(0x40, 0xFF, 0xB5, 0x47)
        };
        static readonly Brush CGame = Geo.Frozen(0xFF, 0xFF, 0x2B, 0xD6), CShield = Geo.Frozen(0xFF, 0x00, 0xE5, 0xFF),
            CAmber = Geo.Frozen(0xFF, 0xFF, 0xB5, 0x47), CMuted = Geo.Frozen(0xFF, 0x6F, 0x84, 0xA8),
            CGreen = Geo.Frozen(0xFF, 0x00, 0xFF, 0xA3), CViolet = Geo.Frozen(0xFF, 0x96, 0x6E, 0xFF);

        public ProcessItem(ProcInfo p)
        {
            Pid = p.Pid;
            Name = p.Name;
            DisplayName = p.DisplayName;
            Initial = string.IsNullOrEmpty(p.DisplayName) ? "?" : p.DisplayName.Substring(0, 1).ToUpperInvariant();
            AvatarBrush = Avatars[(p.Name?.Length ?? 0) % Avatars.Length];
        }

        public int Pid { get; }
        public string Name { get; }
        public string DisplayName { get; }
        public string Initial { get; }
        public Brush AvatarBrush { get; }

        string path; public string Path { get => path; private set { if (path != value) { path = value; On(); } } }
        long ws; public long WorkingSet { get => ws; private set { if (ws != value) { ws = value; On(); On(nameof(WsText)); } } }
        long priv; public long PrivateBytes { get => priv; private set { if (priv != value) { priv = value; On(); On(nameof(PrivText)); } } }
        double bar; public double BarWidth { get => bar; private set { if (System.Math.Abs(bar - value) > 0.5) { bar = value; On(); } } }
        ProcCategory cat = (ProcCategory)(-1);
        public ProcCategory Category { get => cat; private set { if (cat != value) { cat = value; On(); On(nameof(Status)); On(nameof(StatusBrush)); } } }
        bool excl; public bool IsExcluded { get => excl; set { if (excl != value) { excl = value; On(); } } }
        bool game; public bool IsCustomGame { get => game; set { if (game != value) { game = value; On(); } } }

        public string WsText => Cleaner.Fmt(ws);
        public string PrivText => Cleaner.Fmt(priv);

        public string Status => cat switch
        {
            ProcCategory.Game => "GAME",
            ProcCategory.GameRelated => "SHIELDED",
            ProcCategory.AntiCheat => "ANTI-CHEAT",
            ProcCategory.System => "SYSTEM",
            ProcCategory.Excluded => "EXCLUDED",
            ProcCategory.Self => "QUANTA",
            _ => "TRIMMABLE"
        };

        public Brush StatusBrush => cat switch
        {
            ProcCategory.Game => CGame,
            ProcCategory.GameRelated => CShield,
            ProcCategory.AntiCheat => CAmber,
            ProcCategory.Excluded => CAmber,
            ProcCategory.System => CMuted,
            ProcCategory.Self => CViolet,
            _ => CGreen
        };

        public void Update(ProcInfo p, long max, AppSettings s)
        {
            Path = string.IsNullOrEmpty(p.Path) ? "access restricted" : p.Path;
            WorkingSet = p.WorkingSet;
            PrivateBytes = p.PrivateBytes;
            BarWidth = max > 0 ? System.Math.Max(2, 110.0 * p.WorkingSet / max) : 2;
            Category = p.Category;
            IsExcluded = s.Exclusions.Contains(p.Name);
            IsCustomGame = s.CustomGames.Contains(p.Name);
        }
    }
}
