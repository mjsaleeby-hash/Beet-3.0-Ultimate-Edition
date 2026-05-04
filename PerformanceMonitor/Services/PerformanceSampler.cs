using System.Diagnostics;
using BeetsBackup.PerfMon.Models;

namespace BeetsBackup.PerfMon.Services;

public sealed class PerformanceSampler
{
    private readonly Process _process;
    private readonly int _logicalCores;

    private DateTime _lastSampleUtc;
    private TimeSpan _lastProcTime;
    private ulong _lastIoRead;
    private ulong _lastIoWrite;

    private long _lastSysIdle;
    private long _lastSysKernel;
    private long _lastSysUser;

    public PerformanceSampler(Process process)
    {
        _process = process;
        _logicalCores = Environment.ProcessorCount;
        InitializeBaselines();
    }

    private void InitializeBaselines()
    {
        try
        {
            _process.Refresh();
            _lastProcTime = _process.TotalProcessorTime;
        }
        catch
        {
            _lastProcTime = TimeSpan.Zero;
        }

        if (NativeMethods.GetProcessIoCounters(_process.Handle, out var io))
        {
            _lastIoRead = io.ReadTransferCount;
            _lastIoWrite = io.WriteTransferCount;
        }

        NativeMethods.GetSystemTimes(out _lastSysIdle, out _lastSysKernel, out _lastSysUser);
        _lastSampleUtc = DateTime.UtcNow;
    }

    public PerformanceSample? Sample()
    {
        try
        {
            _process.Refresh();
            if (_process.HasExited)
                return null;
        }
        catch
        {
            return null;
        }

        var nowUtc = DateTime.UtcNow;
        var elapsed = nowUtc - _lastSampleUtc;
        if (elapsed.TotalMilliseconds < 1)
            elapsed = TimeSpan.FromMilliseconds(1);

        var procTime = SafeTotalProcessorTime();
        var deltaProcMs = (procTime - _lastProcTime).TotalMilliseconds;
        var procCpu = deltaProcMs / (elapsed.TotalMilliseconds * _logicalCores) * 100.0;
        if (procCpu < 0) procCpu = 0;
        if (procCpu > 100) procCpu = 100;

        ulong ioReadTotal = 0, ioWriteTotal = 0;
        long ioReadPerSec = 0, ioWritePerSec = 0;
        if (NativeMethods.GetProcessIoCounters(_process.Handle, out var io))
        {
            ioReadTotal = io.ReadTransferCount;
            ioWriteTotal = io.WriteTransferCount;
            ioReadPerSec = (long)((ioReadTotal - _lastIoRead) / Math.Max(elapsed.TotalSeconds, 0.001));
            ioWritePerSec = (long)((ioWriteTotal - _lastIoWrite) / Math.Max(elapsed.TotalSeconds, 0.001));
            _lastIoRead = ioReadTotal;
            _lastIoWrite = ioWriteTotal;
        }

        double sysCpu = 0;
        if (NativeMethods.GetSystemTimes(out var sysIdle, out var sysKernel, out var sysUser))
        {
            var idleDelta = sysIdle - _lastSysIdle;
            var kernelDelta = sysKernel - _lastSysKernel;
            var userDelta = sysUser - _lastSysUser;
            var totalSys = kernelDelta + userDelta;
            if (totalSys > 0)
                sysCpu = (totalSys - idleDelta) * 100.0 / totalSys;

            _lastSysIdle = sysIdle;
            _lastSysKernel = sysKernel;
            _lastSysUser = sysUser;
        }

        long availMem = 0;
        var memStatus = new NativeMethods.MEMORYSTATUSEX();
        if (NativeMethods.GlobalMemoryStatusEx(memStatus))
            availMem = (long)memStatus.ullAvailPhys;

        _lastProcTime = procTime;
        _lastSampleUtc = nowUtc;

        return new PerformanceSample
        {
            Timestamp = new DateTimeOffset(nowUtc, TimeSpan.Zero),
            UptimeMs = (long)(nowUtc - _process.StartTime.ToUniversalTime()).TotalMilliseconds,
            ProcessCpuPercent = Math.Round(procCpu, 2),
            WorkingSetBytes = _process.WorkingSet64,
            PrivateBytes = _process.PrivateMemorySize64,
            VirtualBytes = _process.VirtualMemorySize64,
            HandleCount = _process.HandleCount,
            ThreadCount = _process.Threads.Count,
            IoReadBytesTotal = (long)ioReadTotal,
            IoWriteBytesTotal = (long)ioWriteTotal,
            IoReadBytesPerSec = ioReadPerSec,
            IoWriteBytesPerSec = ioWritePerSec,
            SystemCpuPercent = Math.Round(sysCpu, 2),
            SystemAvailableMemoryBytes = availMem,
        };
    }

    private TimeSpan SafeTotalProcessorTime()
    {
        try { return _process.TotalProcessorTime; }
        catch { return _lastProcTime; }
    }
}
