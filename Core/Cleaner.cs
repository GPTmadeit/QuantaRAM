using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace QuantaRAM.Core
{
    public enum LogKind { Info, Ok, Warn, Shield, Error }

    public sealed class CleanOptions
    {
        public bool Trim, PurgeLowStandby, PurgeStandby, FlushModified, FlushFileCache, Combine;

        public static readonly string[] Names = { "QUICK", "BALANCED", "DEEP", "CUSTOM" };

        public static CleanOptions For(int profile, AppSettings s) => profile switch
        {
            0 => new CleanOptions { Trim = true, PurgeLowStandby = true },
            1 => new CleanOptions { Trim = true, PurgeLowStandby = true, PurgeStandby = true, Combine = true },
            2 => new CleanOptions { Trim = true, PurgeLowStandby = true, PurgeStandby = true, FlushModified = true, FlushFileCache = true, Combine = true },
            _ => new CleanOptions
            {
                Trim = s.CTrim, PurgeLowStandby = s.CLowStandby, PurgeStandby = s.CStandby,
                FlushModified = s.CModified, FlushFileCache = s.CFileCache, Combine = s.CCombine
            }
        };
    }

    public sealed class CleanResult
    {
        public long Freed, AvailGain, FreeGain, TrimmedBytes;
        public int Trimmed, Shielded, Protected, Failed;
        public TimeSpan Duration;
        public string GameName, Profile, Trigger;
        public bool GameIncluded;
    }

    public static class Cleaner
    {
        public static CleanResult Run(CleanOptions o, GameSnapshot snap, AppSettings s, bool includeOnce, bool admin,
                                      MemoryMonitor monitor, Action<string, LogKind> log)
        {
            var sw = Stopwatch.StartNew();
            var r = new CleanResult();
            var before = monitor.Sample();

            bool gaming = s.GameShieldEnabled && snap.IsGaming;
            bool includeGames = s.IncludeGameProcesses || includeOnce;
            bool systemWideOk = !gaming || includeGames || s.AllowSystemWideWhileGaming;
            r.GameName = gaming ? snap.Primary.DisplayName : null;
            r.GameIncluded = gaming && includeGames;

            if (gaming)
            {
                if (includeGames) log($"Shield overridden by you: {snap.Primary.DisplayName} will be included", LogKind.Warn);
                else log($"Shield holding {snap.Primary.DisplayName} + {Math.Max(0, snap.Shielded.Count - 1)} linked processes", LogKind.Shield);
            }

            // ---- 1. working-set trim (per process so the game can be skipped) ----
            if (o.Trim)
            {
                foreach (var p in snap.Processes)
                {
                    switch (p.Category)
                    {
                        case ProcCategory.System:
                        case ProcCategory.Self:
                            continue;
                        case ProcCategory.AntiCheat:
                        case ProcCategory.Excluded:
                            r.Protected++; continue;
                    }
                    if (gaming && !includeGames && snap.Shielded.Contains(p.Pid)) { r.Shielded++; continue; }
                    if (s.ProtectForeground && p.Pid == snap.ForegroundPid && !(gaming && includeGames && snap.Shielded.Contains(p.Pid)))
                    { r.Protected++; continue; }

                    IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_QUOTA, false, p.Pid);
                    if (h == IntPtr.Zero) { r.Failed++; continue; }
                    try
                    {
                        if (Native.EmptyWorkingSet(h)) { r.Trimmed++; r.TrimmedBytes += p.WorkingSet; }
                        else r.Failed++;
                    }
                    finally { Native.CloseHandle(h); }
                }
                log($"Trimmed {r.Trimmed} working sets ({Fmt(r.TrimmedBytes)} paged out)", LogKind.Ok);
                if (r.Shielded > 0) log($"{r.Shielded} game processes left untouched", LogKind.Shield);
                if (r.Failed > 0 && !admin) log($"{r.Failed} processes need admin rights to trim", LogKind.Warn);
            }

            // ---- 2. system file cache ----
            if (o.FlushFileCache)
            {
                if (!systemWideOk) log("File-cache flush skipped: it could evict the game's streamed assets", LogKind.Shield);
                else if (!admin) log("File-cache flush needs admin (ELEVATE)", LogKind.Warn);
                else if (Native.SetSystemFileCacheSize(UIntPtr.MaxValue, UIntPtr.MaxValue, 0)) log("System file cache flushed", LogKind.Ok);
                else log($"File-cache flush failed (Win32 {Marshal.GetLastWin32Error()})", LogKind.Warn);
            }

            // ---- 3. modified page list ----
            if (o.FlushModified)
            {
                if (!systemWideOk) log("Modified-list flush skipped: disk writes could cause stutter in-game", LogKind.Shield);
                else MemCmd(Native.MemoryFlushModifiedList, "Modified page list flushed to disk", admin, log);
            }

            // ---- 4. standby list ----
            if (o.PurgeStandby && systemWideOk)
                MemCmd(Native.MemoryPurgeStandbyList, "Standby list purged", admin, log);
            else if (o.PurgeStandby || o.PurgeLowStandby)
            {
                if (o.PurgeStandby) log("Full standby purge reduced to low-priority only (game cache kept)", LogKind.Shield);
                MemCmd(Native.MemoryPurgeLowPriorityStandbyList, "Low-priority standby pages purged", admin, log);
            }

            // ---- 5. page combining (dedupes identical pages; doesn't evict anything) ----
            if (o.Combine)
            {
                if (!admin) log("Page combining needs admin (ELEVATE)", LogKind.Warn);
                else
                {
                    var info = new Native.MEMORY_COMBINE_INFORMATION_EX();
                    int st = Native.NtSetSystemInformation(Native.SystemCombinePhysicalMemoryInformation, ref info, Marshal.SizeOf(info));
                    if (st == 0) log($"Combined {info.PagesCombined.ToUInt64():N0} identical pages", LogKind.Ok);
                    else log("Page combining: " + Native.NtStatusText(st), LogKind.Warn);
                }
            }

            Thread.Sleep(350);
            var after = monitor.Sample();
            r.AvailGain = Math.Max(0, (long)after.Available - (long)before.Available);
            r.FreeGain = Math.Max(0, (long)after.Free - (long)before.Free);
            r.Freed = Math.Max(r.AvailGain, r.FreeGain);
            r.Duration = sw.Elapsed;
            return r;
        }

        static void MemCmd(int cmd, string okText, bool admin, Action<string, LogKind> log)
        {
            if (!admin) { log(okText.Split(' ')[0] + " purge needs admin (ELEVATE)", LogKind.Warn); return; }
            int c = cmd;
            int st = Native.NtSetSystemInformation(Native.SystemMemoryListInformation, ref c, sizeof(int));
            if (st == 0) log(okText, LogKind.Ok);
            else log(okText.Split(' ')[0] + " list: " + Native.NtStatusText(st), LogKind.Warn);
        }

        /// <summary>Trims one process. Returns bytes removed from its working set.</summary>
        public static long TrimOne(int pid)
        {
            using var p = Process.GetProcessById(pid);
            long before = p.WorkingSet64;
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_QUOTA, false, pid);
            if (h == IntPtr.Zero) throw new UnauthorizedAccessException("access denied");
            try { if (!Native.EmptyWorkingSet(h)) throw new InvalidOperationException("trim refused"); }
            finally { Native.CloseHandle(h); }
            p.Refresh();
            return Math.Max(0, before - p.WorkingSet64);
        }

        public static string Fmt(long bytes)
        {
            double mb = bytes / 1048576.0;
            return mb >= 1024 ? $"{mb / 1024:0.00} GB" : $"{mb:0} MB";
        }
    }
}
