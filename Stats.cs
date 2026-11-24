using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PcDash;

/// <summary>Samples PC performance once per second and keeps a short history.</summary>
public sealed class StatsSampler : IDisposable
{
    const int HistoryLength = 60;

    readonly System.Threading.Timer _timer;
    readonly object _lock = new();
    object? _snapshot;

    // CPU
    ulong _prevIdle, _prevKernel, _prevUser;
    readonly string _cpuName = ReadCpuName();
    PerformanceCounter? _cpuPerf, _cpuFreq;

    // GPU (raw samples per engine instance)
    Dictionary<string, CounterSample> _prevGpu = new();
    readonly bool _gpuAvailable = PerformanceCounterCategory.Exists("GPU Engine");
    readonly bool _gpuMemAvailable = PerformanceCounterCategory.Exists("GPU Adapter Memory");
    readonly string _gpuName = ReadGpuName();

    // Disk I/O
    PerformanceCounter? _diskRead, _diskWrite, _diskIdle;

    // Network
    long _prevRx = -1, _prevTx = -1;
    DateTime _prevNetTime;

    // Processes
    Dictionary<int, TimeSpan> _prevProcTimes = new();
    DateTime _prevProcSample = DateTime.MinValue;
    object[] _topProcs = [];
    int _tick;

    readonly Queue<double> _hCpu = new(), _hGpu = new(), _hRam = new(), _hRx = new(), _hTx = new();

    public StatsSampler()
    {
        Try(() =>
        {
            _cpuPerf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total");
            _cpuFreq = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total");
            _cpuPerf.NextValue();
        });
        Try(() =>
        {
            _diskRead = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
            _diskWrite = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
            _diskIdle = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total");
            _diskRead.NextValue(); _diskWrite.NextValue(); _diskIdle.NextValue();
        });
        _timer = new System.Threading.Timer(_ => Sample(), null, 0, 1000);
    }

    public object? Snapshot { get { lock (_lock) return _snapshot; } }

    void Sample()
    {
        try
        {
            double cpu = SampleCpu();
            double? cpuGhz = null;
            Try(() =>
            {
                if (_cpuPerf != null && _cpuFreq != null)
                    cpuGhz = Math.Round(_cpuFreq.NextValue() * _cpuPerf.NextValue() / 100.0 / 1000.0, 2);
            });

            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            GlobalMemoryStatusEx(ref mem);
            ulong ramUsed = mem.ullTotalPhys - mem.ullAvailPhys;
            double ramPct = 100.0 * ramUsed / mem.ullTotalPhys;

            var (gpuPct, gpuEngine) = SampleGpu();
            long? gpuShared = null, gpuDedicated = null;
            if (_gpuMemAvailable) Try(() => (gpuShared, gpuDedicated) = SampleGpuMemory());

            var (rx, tx) = SampleNetwork();

            double? diskReadBps = null, diskWriteBps = null, diskActive = null;
            Try(() =>
            {
                if (_diskRead == null) return;
                diskReadBps = _diskRead.NextValue();
                diskWriteBps = _diskWrite!.NextValue();
                diskActive = Math.Clamp(100 - _diskIdle!.NextValue(), 0, 100);
            });

            var drives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => (object)new
                {
                    name = d.Name.TrimEnd('\\'),
                    label = d.VolumeLabel,
                    total = d.TotalSize,
                    used = d.TotalSize - d.TotalFreeSpace,
                })
                .ToArray();

            if (_tick++ % 2 == 0) Try(() => _topProcs = SampleProcesses());

            lock (_lock)
            {
                Push(_hCpu, cpu); Push(_hGpu, gpuPct ?? 0); Push(_hRam, ramPct);
                Push(_hRx, rx); Push(_hTx, tx);
                _snapshot = new
                {
                    time = DateTimeOffset.Now,
                    uptimeSec = Environment.TickCount64 / 1000,
                    host = Environment.MachineName,
                    cpu = new { name = _cpuName, cores = Environment.ProcessorCount, pct = Math.Round(cpu, 1), ghz = cpuGhz, history = _hCpu.ToArray() },
                    ram = new { total = mem.ullTotalPhys, used = ramUsed, pct = Math.Round(ramPct, 1), history = _hRam.ToArray() },
                    gpu = new { name = _gpuName, pct = gpuPct is null ? (double?)null : Math.Round(gpuPct.Value, 1), engine = gpuEngine, sharedUsed = gpuShared, dedicatedUsed = gpuDedicated, history = _hGpu.ToArray() },
                    net = new { rxBps = rx, txBps = tx, rxHistory = _hRx.ToArray(), txHistory = _hTx.ToArray() },
                    disk = new { readBps = diskReadBps, writeBps = diskWriteBps, activePct = diskActive, drives },
                    processes = _topProcs,
                };
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    static void Push(Queue<double> q, double v)
    {
        q.Enqueue(Math.Round(v, 1));
        while (q.Count > HistoryLength) q.Dequeue();
    }

    double SampleCpu()
    {
        GetSystemTimes(out var idleFt, out var kernelFt, out var userFt);
        ulong idle = idleFt.ToULong(), kernel = kernelFt.ToULong(), user = userFt.ToULong();
        ulong dIdle = idle - _prevIdle, dKernel = kernel - _prevKernel, dUser = user - _prevUser;
        bool first = _prevKernel == 0;
        _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
        ulong total = dKernel + dUser; // kernel time includes idle time
        if (first || total == 0) return 0;
        return Math.Clamp(100.0 * (total - dIdle) / total, 0, 100);
    }

    /// <summary>Same method as Task Manager: sum utilisation per engine type, report the busiest type.</summary>
    (double? pct, string? engine) SampleGpu()
    {
        if (!_gpuAvailable) return (null, null);
        try
        {
            var data = new PerformanceCounterCategory("GPU Engine").ReadCategory()["Utilization Percentage"];
            var current = new Dictionary<string, CounterSample>();
            var perType = new Dictionary<string, double>();
            foreach (InstanceData d in data.Values)
            {
                current[d.InstanceName] = d.Sample;
                if (!_prevGpu.TryGetValue(d.InstanceName, out var prev)) continue;
                double v = CounterSample.Calculate(prev, d.Sample);
                int i = d.InstanceName.IndexOf("engtype_", StringComparison.Ordinal);
                string type = i >= 0 ? d.InstanceName[(i + 8)..] : "other";
                perType[type] = perType.GetValueOrDefault(type) + v;
            }
            _prevGpu = current;
            if (perType.Count == 0) return (0, null);
            var top = perType.MaxBy(kv => kv.Value);
            return (Math.Clamp(top.Value, 0, 100), top.Key);
        }
        catch { return (null, null); }
    }

    static (long shared, long dedicated) SampleGpuMemory()
    {
        var cat = new PerformanceCounterCategory("GPU Adapter Memory").ReadCategory();
        long shared = 0, dedicated = 0;
        foreach (InstanceData d in cat["Shared Usage"].Values) shared += d.RawValue;
        foreach (InstanceData d in cat["Dedicated Usage"].Values) dedicated += d.RawValue;
        return (shared, dedicated);
    }

    (double rx, double tx) SampleNetwork()
    {
        long rx = 0, tx = 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try
            {
                var s = ni.GetIPStatistics();
                rx += s.BytesReceived; tx += s.BytesSent;
            }
            catch { }
        }
        var now = DateTime.UtcNow;
        double rxBps = 0, txBps = 0;
        if (_prevRx >= 0)
        {
            double secs = Math.Max(0.1, (now - _prevNetTime).TotalSeconds);
            rxBps = Math.Max(0, (rx - _prevRx) / secs);
            txBps = Math.Max(0, (tx - _prevTx) / secs);
        }
        _prevRx = rx; _prevTx = tx; _prevNetTime = now;
        return (rxBps, txBps);
    }

    /// <summary>Top processes by CPU, grouped by name (so all chrome.exe count as one).</summary>
    object[] SampleProcesses()
    {
        var now = DateTime.UtcNow;
        double elapsedMs = (now - _prevProcSample).TotalMilliseconds;
        bool hasPrev = _prevProcSample != DateTime.MinValue;
        var times = new Dictionary<int, TimeSpan>();
        var groups = new Dictionary<string, (double cpu, long mem, int count)>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == 0) continue; // System Idle Process
                double cpu = 0; long mem = 0;
                try
                {
                    var t = p.TotalProcessorTime;
                    times[p.Id] = t;
                    if (hasPrev && _prevProcTimes.TryGetValue(p.Id, out var pt))
                        cpu = (t - pt).TotalMilliseconds / elapsedMs / Environment.ProcessorCount * 100.0;
                }
                catch { /* access denied for some system processes */ }
                try { mem = p.WorkingSet64; } catch { }
                var g = groups.GetValueOrDefault(p.ProcessName);
                groups[p.ProcessName] = (g.cpu + cpu, g.mem + mem, g.count + 1);
            }
        }
        _prevProcTimes = times;
        _prevProcSample = now;

        return groups
            .OrderByDescending(kv => kv.Value.cpu).ThenByDescending(kv => kv.Value.mem)
            .Take(6)
            .Select(kv => (object)new { name = kv.Key, cpu = Math.Round(Math.Max(0, kv.Value.cpu), 1), mem = kv.Value.mem, count = kv.Value.count })
            .ToArray();
    }

    static string ReadCpuName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
        }
        catch { return "CPU"; }
    }

    static string ReadGpuName()
    {
        try
        {
            // Display adapter class key; first entry with a DriverDesc is the GPU name.
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls != null)
                foreach (var sub in cls.GetSubKeyNames())
                {
                    if (!int.TryParse(sub, out _)) continue;
                    using var k = cls.OpenSubKey(sub);
                    if (k?.GetValue("DriverDesc") is string s && !s.Contains("Basic", StringComparison.OrdinalIgnoreCase)) return s;
                }
        }
        catch { }
        return "GPU";
    }

    static void Try(Action a) { try { a(); } catch (Exception ex) { Debug.WriteLine(ex); } }

    public void Dispose()
    {
        _timer.Dispose();
        _cpuPerf?.Dispose(); _cpuFreq?.Dispose();
        _diskRead?.Dispose(); _diskWrite?.Dispose(); _diskIdle?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FILETIME { public uint Low, High; public ulong ToULong() => ((ulong)High << 32) | Low; }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
}
