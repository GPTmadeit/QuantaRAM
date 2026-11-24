using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace QuantaRAM.Core
{
    public enum ProcCategory { Normal, System, AntiCheat, Excluded, Game, GameRelated, Self }

    public sealed class ProcInfo
    {
        public int Pid, ParentPid;
        public string Name;         // lower-case exe name without .exe
        public string DisplayName;  // original exe name
        public string Path;
        public long WorkingSet, PrivateBytes;
        public ProcCategory Category;
        public string Reason;
    }

    public sealed class GameSnapshot
    {
        public List<ProcInfo> Processes = new List<ProcInfo>();
        public List<ProcInfo> Games = new List<ProcInfo>();
        public HashSet<int> Shielded = new HashSet<int>();
        public ProcInfo Primary;
        public int ForegroundPid;
        public bool IsGaming => Primary != null;
    }

    /// <summary>
    /// Finds running games and everything that belongs to them (child processes, files in the same
    /// install folder, overlays, anti-cheat) so the cleaner can leave them untouched.
    /// </summary>
    public sealed class GameDetector
    {
        public static readonly HashSet<string> Critical = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "system", "idle", "registry", "memory compression", "memcompression", "secure system", "smss", "csrss",
            "wininit", "winlogon", "lsass", "lsaiso", "services", "dwm", "audiodg", "fontdrvhost", "vmmem",
            "vmmemwsl", "msmpeng", "sgrmbroker", "searchindexer"
        };

        public static readonly HashSet<string> AntiCheat = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "easyanticheat", "easyanticheat_eos", "easyanticheat_launcher", "eac_launcher", "beservice", "beservice_x64",
            "battleye", "vgc", "vgtray", "vgk", "faceit", "faceitservice", "faceitclient", "eaanticheat.gameservice",
            "eaanticheat.installer", "gameguard", "gamemon", "gamemon64", "xigncode", "x3", "npggnt", "npggsvc",
            "atvi-randgrid_sr", "punkbuster", "pnkbstra", "pnkbstrb", "mhyprot", "ace-tray", "sguard64", "acebase"
        };

        static readonly HashSet<string> KnownGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cs2", "csgo", "valorant-win64-shipping", "fortniteclient-win64-shipping", "r5apex", "r5apex_dx12",
            "gta5", "gta5_enhanced", "playgtav", "rdr2", "eldenring", "nightreign", "cyberpunk2077", "witcher3",
            "bg3", "bg3_dx11", "starfield", "fallout4", "fallout76", "skyrimse", "skyrimvr", "overwatch", "dota2",
            "league of legends", "rocketleague", "destiny2", "cod", "cod22-cod", "modernwarfare", "blackops6",
            "helldivers2", "palworld-win64-shipping", "minecraft.windows", "robloxplayerbeta", "eurotrucks2",
            "amtrucks", "flightsimulator", "flightsimulator2024", "forzahorizon5", "forzahorizon4",
            "forzamotorsport", "re4", "re2", "re3", "re8", "monsterhunterwilds", "monsterhunterworld",
            "monsterhunterrise", "darksoulsiii", "sekiro", "hogwartslegacy", "tslgame", "rainbowsix",
            "rainbowsix_vulkan", "escapefromtarkov", "marvel-win64-shipping", "warframe.x64", "wow", "wowclassic",
            "diablo iv", "factorio", "terraria", "stardew valley", "hollow_knight", "cities2", "cities",
            "arma3_x64", "dayz_x64", "rustclient", "ffxiv_dx11", "genshinimpact", "yuanshen", "zenlesszonezero",
            "starrail", "pathofexile_x64", "pathofexile_x64steam", "pathofexile", "lostark", "newworld",
            "bf2042", "bf2042_trial", "bf6", "bfv", "bf1", "deadbydaylight-win64-shipping", "thefinals",
            "discovery", "deltaforceclient-win64-shipping", "ready or not", "readyornot-win64-shipping",
            "hunt", "huntgame", "spacemarine2", "baldur's gate 3", "doometernalx64vk", "doomthedarkages",
            "alanwake2", "stalker2-win64-shipping", "satisfactory", "factorygame-win64-shipping",
            "manorlords-win64-shipping", "nms", "acmirage", "acvalhalla", "acodyssey", "acshadows",
            "farcry6", "watchdogslegion", "ghostoftsushima", "horizonzerodawn", "horizonforbiddenwest",
            "spider-man", "spiderman2", "tlou-i", "u4", "cod_hq", "osu!", "beatsaber", "vrchat", "ets2",
            "warthunder", "aces", "worldoftanks", "wotlauncher", "sotgame", "seaofthieves", "halo infinite",
            "haloinfinite", "mcc-win64-shipping", "smite", "paladins", "brawlhalla", "dying light 2",
            "dyinglightgame_x64_rwdi", "msfs2024", "kingdomcome", "kcd2", "witcher3_dx12", "metro exodus",
            "residentevil4", "sf6", "streetfighter6", "tekken8", "polaris-win64-shipping", "eldenring_nightreign"
        };

        static readonly string[] LibraryMarkers =
        {
            @"\steamapps\common\", @"\epic games\", @"\gog games\", @"\gog galaxy\games\", @"\origin games\",
            @"\ea games\", @"\ubisoft game launcher\games\", @"\xboxgames\", @"\riot games\", @"\games\",
            @"\battlestate games\", @"\rockstar games\", @"\amazon games\library\", @"\itch\apps\"
        };

        // Launchers / helpers that live in game folders but are not games themselves.
        static readonly HashSet<string> NotPrimary = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "unitycrashhandler64", "unitycrashhandler32", "crashreportclient", "crashpad_handler", "steamwebhelper",
            "steam", "epicwebhelper", "epicgameslauncher", "galaxyclient", "galaxyclient helper", "eadesktop",
            "eabackgroundservice", "ubisoftconnect", "upc", "uplaywebcore", "riotclientservices", "riotclientux",
            "riotclientuxrender", "riotclientcrashhandler", "leagueclient", "leagueclientux", "leagueclientuxrender",
            "battle.net", "cefsharp.browsersubprocess", "qtwebengineprocess", "launcher", "rockstarservice",
            "socialclubhelper", "playgtav_launcher", "rockstarerrorhandler", "gamelaunchhelper", "xboxpcapp"
        };

        static readonly HashSet<string> Overlays = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "gameoverlayui", "gameoverlayui64", "eosoverlayrenderer-win64-shipping", "eosoverlayrenderer-win32-shipping",
            "nvidia overlay", "nvidia share", "gamebar", "gamebarftserver", "rtss", "msiafterburner", "discordhookhelper64"
        };

        // Apps that often run fullscreen but aren't games.
        static readonly HashSet<string> FullscreenIgnore = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "arc", "vlc", "mpc-hc64",
            "mpc-be64", "mpv", "potplayermini64", "potplayer", "wmplayer", "video.ui", "microsoft.media.player", "powerpnt",
            "applicationframehost", "searchhost", "startmenuexperiencehost", "shellexperiencehost", "lockapp",
            "textinputhost", "quantaram", "code", "devenv", "obs64", "discord", "spotify", "teams", "ms-teams", "zoom",
            "mstsc", "vmware", "vmconnect", "virtualboxvm", "windowsterminal", "acrobat", "acrord32", "photos",
            "snippingtool", "screenclippinghost", "steam", "steamwebhelper", "plex", "kodi", "netflix", "paraview",
            "freecad", "blender", "msrdc", "parsecd", "moonlight", "sunshine"
        };

        readonly object gate = new object();
        readonly Dictionary<int, (string exe, string path)> pathCache = new Dictionary<int, (string, string)>();
        readonly int selfPid = Environment.ProcessId;

        public GameSnapshot Scan(AppSettings s)
        {
            lock (gate) return ScanCore(s);
        }

        GameSnapshot ScanCore(AppSettings s)
        {
            var snap = new GameSnapshot();
            var customGames = new HashSet<string>(s.CustomGames, StringComparer.OrdinalIgnoreCase);
            var exclusions = new HashSet<string>(s.Exclusions, StringComparer.OrdinalIgnoreCase);

            // 1) pid / parent / exe via toolhelp
            var list = new List<ProcInfo>(256);
            IntPtr h = Native.CreateToolhelp32Snapshot(0x2, 0);
            if (h != IntPtr.Zero && h != new IntPtr(-1))
            {
                try
                {
                    var e = new Native.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Native.PROCESSENTRY32W>() };
                    if (Native.Process32First(h, ref e))
                    {
                        do
                        {
                            list.Add(new ProcInfo
                            {
                                Pid = (int)e.th32ProcessID,
                                ParentPid = (int)e.th32ParentProcessID,
                                DisplayName = e.szExeFile,
                                Name = AppSettings.Normalize(e.szExeFile) ?? ""
                            });
                        } while (Native.Process32Next(h, ref e));
                    }
                }
                finally { Native.CloseHandle(h); }
            }

            // 2) memory figures (one system call under the hood)
            var mem = new Dictionary<int, (long ws, long priv)>(list.Count);
            foreach (var p in Process.GetProcesses())
            {
                try { mem[p.Id] = (p.WorkingSet64, p.PrivateMemorySize64); } catch { }
                finally { p.Dispose(); }
            }

            // 3) image paths (cached per pid)
            var alive = new HashSet<int>();
            var sb = new StringBuilder(1024);
            foreach (var p in list)
            {
                alive.Add(p.Pid);
                if (mem.TryGetValue(p.Pid, out var m)) { p.WorkingSet = m.ws; p.PrivateBytes = m.priv; }
                if (p.Pid <= 4) { p.Path = ""; continue; }
                if (pathCache.TryGetValue(p.Pid, out var c) && c.exe == p.DisplayName) { p.Path = c.path; continue; }
                string path = "";
                IntPtr ph = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
                if (ph != IntPtr.Zero)
                {
                    try
                    {
                        sb.Clear(); uint size = (uint)sb.Capacity;
                        if (Native.QueryFullProcessImageName(ph, 0, sb, ref size)) path = sb.ToString();
                    }
                    finally { Native.CloseHandle(ph); }
                }
                pathCache[p.Pid] = (p.DisplayName, path);
                p.Path = path;
            }
            foreach (var dead in pathCache.Keys.Where(k => !alive.Contains(k)).ToList()) pathCache.Remove(dead);

            // 4) foreground / fullscreen
            int fgPid = 0; bool fgFullscreen = false;
            try
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg != IntPtr.Zero)
                {
                    Native.GetWindowThreadProcessId(fg, out uint pid);
                    fgPid = (int)pid;
                    var cls = new StringBuilder(256);
                    Native.GetClassName(fg, cls, 256);
                    string cn = cls.ToString();
                    // Real fullscreen / borderless games have no caption bar. A maximized app with an
                    // auto-hidden taskbar also covers the monitor, but it keeps WS_CAPTION.
                    long style = Native.GetWindowLongPtr(fg, -16).ToInt64();
                    bool captioned = (style & 0x00C00000L) == 0x00C00000L;
                    if (!captioned && cn != "Progman" && cn != "WorkerW" && cn != "Shell_TrayWnd" && Native.GetWindowRect(fg, out var r))
                    {
                        var mi = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
                        if (Native.GetMonitorInfo(Native.MonitorFromWindow(fg, 2), ref mi))
                        {
                            fgFullscreen = r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                                           r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
                        }
                    }
                }
            }
            catch { }
            snap.ForegroundPid = fgPid;

            // 5) classify
            var gameRoots = new List<string>();
            foreach (var p in list)
            {
                string lowPath = (p.Path ?? "").ToLowerInvariant();
                if (p.Pid == selfPid) p.Category = ProcCategory.Self;
                else if (p.Pid <= 4 || Critical.Contains(p.Name)) p.Category = ProcCategory.System;
                else if (AntiCheat.Contains(p.Name)) p.Category = ProcCategory.AntiCheat;
                else if (exclusions.Contains(p.Name)) p.Category = ProcCategory.Excluded;
                if (p.Category != ProcCategory.Normal && p.Category != ProcCategory.Excluded) continue;

                string reason = null;
                if (customGames.Contains(p.Name)) reason = "your game list";
                else if (KnownGames.Contains(p.Name)) reason = "known title";
                else if (!NotPrimary.Contains(p.Name) && !Overlays.Contains(p.Name) && p.WorkingSet > 120L * 1024 * 1024)
                {
                    foreach (var mk in LibraryMarkers)
                        if (lowPath.Contains(mk)) { reason = "game library"; break; }
                }
                if (reason == null && s.DetectFullscreen && fgFullscreen && p.Pid == fgPid && p.WorkingSet > 150L * 1024 * 1024 &&
                    !FullscreenIgnore.Contains(p.Name) && !NotPrimary.Contains(p.Name))
                    reason = "fullscreen app";

                if (reason != null)
                {
                    p.Category = ProcCategory.Game;
                    p.Reason = reason;
                    snap.Games.Add(p);
                    string root = GameRoot(p.Path);
                    if (root != null) gameRoots.Add(root);
                }
            }

            if (snap.Games.Count > 0)
            {
                snap.Primary = snap.Games.OrderByDescending(g => g.Pid == fgPid).ThenByDescending(g => g.WorkingSet).First();

                // descendants of game processes
                var children = list.GroupBy(p => p.ParentPid).ToDictionary(g => g.Key, g => g.ToList());
                var queue = new Queue<int>(snap.Games.Select(g => g.Pid));
                var related = new HashSet<int>(snap.Games.Select(g => g.Pid));
                while (queue.Count > 0)
                {
                    int pid = queue.Dequeue();
                    if (!children.TryGetValue(pid, out var kids)) continue;
                    foreach (var k in kids)
                        if (k.Pid != pid && related.Add(k.Pid)) queue.Enqueue(k.Pid);
                }

                foreach (var p in list)
                {
                    if (p.Category == ProcCategory.System || p.Category == ProcCategory.Self) continue;
                    string lp = (p.Path ?? "").ToLowerInvariant();
                    bool inRoot = lp.Length > 0 && gameRoots.Any(r => lp.StartsWith(r));
                    if (related.Contains(p.Pid) || inRoot || Overlays.Contains(p.Name) || p.Category == ProcCategory.AntiCheat)
                    {
                        snap.Shielded.Add(p.Pid);
                        if (p.Category == ProcCategory.Normal || p.Category == ProcCategory.Excluded)
                            p.Category = ProcCategory.GameRelated;
                    }
                }
                foreach (var g in snap.Games) snap.Shielded.Add(g.Pid);
            }

            snap.Processes = list;
            return snap;
        }

        /// <summary>Install folder for a game exe, e.g. ...\steamapps\common\MyGame\</summary>
        static string GameRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string lp = path.ToLowerInvariant();
            foreach (var mk in LibraryMarkers)
            {
                int i = lp.IndexOf(mk, StringComparison.Ordinal);
                if (i < 0) continue;
                int start = i + mk.Length;
                int end = lp.IndexOf('\\', start);
                if (end > start) return lp.Substring(0, end + 1);
            }
            string dir = Path.GetDirectoryName(lp);
            if (dir == null) return null;
            // climb out of typical binary sub-folders (Binaries\Win64, bin\x64, ...)
            for (int i = 0; i < 3; i++)
            {
                string leaf = Path.GetFileName(dir);
                if (leaf is "bin" or "binaries" or "win64" or "win32" or "x64" or "x86" or "retail" or "game" or "client")
                {
                    var up = Path.GetDirectoryName(dir);
                    if (up == null || up.Length <= 3) break;
                    dir = up;
                }
                else break;
            }
            // never treat a drive root or Program Files itself as a game root
            if (dir.Length <= 3 || dir.EndsWith(@"\program files") || dir.EndsWith(@"\program files (x86)")) return null;
            return dir + "\\";
        }
    }
}
