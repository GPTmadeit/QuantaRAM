using System;
using System.Runtime.InteropServices;

namespace QuantaRAM.Core
{
    public struct MemorySample
    {
        public ulong Total, Available, Used, Standby, Modified, Free, CommitUsed, CommitLimit, Cache;
        public double Load, Cpu;
        public bool HasLists;
    }

    /// <summary>Cheap 1 Hz memory telemetry: GlobalMemoryStatusEx + GetPerformanceInfo + PDH (no admin needed).</summary>
    public sealed class MemoryMonitor : IDisposable
    {
        readonly object gate = new object();
        IntPtr query;
        IntPtr cStbCore, cStbNorm, cStbRes, cMod, cFree, cCpu;
        bool pdh;

        public MemoryMonitor()
        {
            if (Native.PdhOpenQuery(null, IntPtr.Zero, out query) == 0)
            {
                pdh = true;
                cStbCore = Add(@"\Memory\Standby Cache Core Bytes");
                cStbNorm = Add(@"\Memory\Standby Cache Normal Priority Bytes");
                cStbRes = Add(@"\Memory\Standby Cache Reserve Bytes");
                cMod = Add(@"\Memory\Modified Page List Bytes");
                cFree = Add(@"\Memory\Free & Zero Page List Bytes");
                cCpu = Add(@"\Processor Information(_Total)\% Processor Utility");
                if (cCpu == IntPtr.Zero) cCpu = Add(@"\Processor(_Total)\% Processor Time");
                Native.PdhCollectQueryData(query);
            }
        }

        IntPtr Add(string path) =>
            Native.PdhAddEnglishCounter(query, path, IntPtr.Zero, out var c) == 0 ? c : IntPtr.Zero;

        static double Get(IntPtr c)
        {
            if (c == IntPtr.Zero) return -1;
            if (Native.PdhGetFormattedCounterValue(c, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, out _, out var v) != 0) return -1;
            return v.doubleValue;
        }

        public MemorySample Sample()
        {
            lock (gate)
            {
                var s = new MemorySample();
                var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
                if (Native.GlobalMemoryStatusEx(ref m))
                {
                    s.Total = m.ullTotalPhys;
                    s.Available = m.ullAvailPhys;
                    s.Used = s.Total - s.Available;
                    s.Load = s.Total > 0 ? s.Used * 100.0 / s.Total : 0;
                }
                if (Native.GetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>()))
                {
                    ulong page = pi.PageSize.ToUInt64();
                    s.CommitUsed = pi.CommitTotal.ToUInt64() * page;
                    s.CommitLimit = pi.CommitLimit.ToUInt64() * page;
                    s.Cache = pi.SystemCache.ToUInt64() * page;
                }
                if (pdh && Native.PdhCollectQueryData(query) == 0)
                {
                    double a = Get(cStbCore), b = Get(cStbNorm), c = Get(cStbRes);
                    if (a >= 0 && b >= 0 && c >= 0)
                    {
                        s.Standby = (ulong)(a + b + c);
                        s.HasLists = true;
                    }
                    double mod = Get(cMod); if (mod >= 0) s.Modified = (ulong)mod;
                    double fr = Get(cFree); if (fr >= 0) s.Free = (ulong)fr;
                    double cpu = Get(cCpu); if (cpu >= 0) s.Cpu = Math.Min(100, cpu);
                }
                if (!s.HasLists)
                {
                    s.Standby = s.Cache;
                    s.Free = s.Available > s.Standby ? s.Available - s.Standby : 0;
                }
                return s;
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (pdh) Native.PdhCloseQuery(query);
                pdh = false;
            }
        }
    }
}
