using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace WallpaperBridge;

public sealed class HardwareSampler(PrivateStore store, DataHub hub) : BackgroundService
{
    private readonly Computer computer = new() { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = false, IsMotherboardEnabled = false, IsStorageEnabled = false, IsNetworkEnabled = false, IsControllerEnabled = false };
    private SensorInfo[] inventory = [];
    public SensorInfo[] Inventory => Volatile.Read(ref inventory);
    private ulong oldIdle, oldKernel, oldUser;
    private bool opened;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            try { computer.Open(); opened = true; } catch { /* Windows counters remain available. */ }
            while (!stoppingToken.IsCancellationRequested)
            {
                try { Sample(); } catch { hub.Hardware(hub.Snapshot().Hardware with { Status = "error", Message = "传感器读取失败，保留上次值" }); }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { if (opened) computer.Close(); }
    }
    private void Sample()
    {
        var all = new List<IHardware>();
        void Visit(IHardware h) { try { h.Update(); } catch { } all.Add(h); foreach (var child in h.SubHardware) Visit(child); }
        if (opened) foreach (var hardware in computer.Hardware) Visit(hardware);
        Volatile.Write(ref inventory, all.SelectMany(h => h.Sensors.Select(s => new SensorInfo(s.Identifier.ToString(), h.Identifier.ToString(), h.HardwareType.ToString(), h.Name, s.Name, s.SensorType.ToString(), Finite(s.Value)))).ToArray());
        var cpu = all.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        var gpus = all.Where(h => h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel).ToArray();
        var cfg = store.Config.Sensors;
        var gpu = gpus.FirstOrDefault(h => h.Identifier.ToString() == cfg.GpuId) ?? gpus.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd) ?? gpus.FirstOrDefault();
        ISensor? Find(IHardware? h, SensorType type, params string[] names) => names.Select(name => h?.Sensors.FirstOrDefault(s => s.SensorType == type && Finite(s.Value) is not null && s.Name == name)).FirstOrDefault(s => s is not null);
        ISensor? Selected(IHardware? h, string? id, SensorType type) => h?.Sensors.FirstOrDefault(s => s.SensorType == type && s.Identifier.ToString() == id);
        Metric Metric(ISensor? sensor, string unit) => new(Finite(sensor?.Value), unit, sensor is null ? "unavailable" : "LibreHardwareMonitor", sensor?.Identifier.ToString());
        var systemCpu = SystemCpuLoad();
        var cpuLoad = Find(cpu, SensorType.Load, "CPU Total");
        var cpuTemp = Selected(cpu, cfg.CpuTemperatureId, SensorType.Temperature) ?? Find(cpu, SensorType.Temperature, "CPU Package", "Core Max");
        var gpuTemp = Selected(gpu, cfg.GpuTemperatureId, SensorType.Temperature) ?? Find(gpu, SensorType.Temperature, "GPU Core");
        var gpuLoad = Find(gpu, SensorType.Load, "GPU Core");
        var memoryUsed = Find(gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
        var memoryTotal = Find(gpu, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
        // Pinned LibreHardwareMonitor 0.9.6 reports these SmallData sensors in MiB.
        double? usedBytes = Finite(memoryUsed?.Value) * Math.Pow(2, 20), totalBytes = Finite(memoryTotal?.Value) * Math.Pow(2, 20);
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var hasMemory = GlobalMemoryStatusEx(ref memory);
        var ramTotal = hasMemory ? (double?)memory.TotalPhysical : null;
        var ramUsed = hasMemory ? (double?)(memory.TotalPhysical - memory.AvailablePhysical) : null;
        hub.Hardware(new("ok", DateTimeOffset.UtcNow,
            new(cpu?.Name ?? "CPU", cpuLoad is null ? new(systemCpu, "%", "Windows GetSystemTimes") : Metric(cpuLoad, "%"), Metric(cpuTemp, "°C")),
            new(gpu?.Name ?? "未检测到物理 GPU", Metric(gpuLoad, "%"), Metric(gpuTemp, "°C")),
            new("专用显存", new(totalBytes > 0 && usedBytes is not null ? Math.Clamp(usedBytes.Value / totalBytes.Value * 100, 0, 100) : null, "%", memoryTotal is null ? "unavailable" : "LibreHardwareMonitor", memoryUsed?.Identifier.ToString()), new(null, "°C"), usedBytes, totalBytes),
            new("物理内存", new(ramTotal > 0 ? ramUsed / ramTotal * 100 : null, "%", "Windows GlobalMemoryStatusEx"), new(null, "°C"), ramUsed, ramTotal),
            cpuTemp is null ? "CPU 温度不可用；可用管理员模式验证传感器。" : ""));
    }
    private static double? Finite(float? value) => value is { } f && float.IsFinite(f) ? f : null;
    private double? SystemCpuLoad()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var total = kernel + user - oldKernel - oldUser;
        var busy = total > 0 ? Math.Clamp(100d * (total - (idle - oldIdle)) / total, 0, 100) : (double?)null;
        var first = oldKernel == 0;
        oldIdle = idle; oldKernel = kernel; oldUser = user;
        return first ? null : busy;
    }
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
}
