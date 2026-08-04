using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;

namespace ImagingHarness;

/// <summary>
/// Instrumentation that separates the three candidate holders of memory:
///  1. the .NET managed heap,
///  2. ImageSharp's unmanaged pool (outstanding UnmanagedMemoryHandle count),
///  3. the C runtime allocator / OS (RSS minus the two above).
/// </summary>
public static class Diagnostics
{
    private static readonly PropertyInfo? OutstandingHandlesProperty = FindOutstandingHandlesProperty();

    private static PropertyInfo? FindOutstandingHandlesProperty()
    {
        Type? type = typeof(Image).Assembly
            .GetTypes()
            .FirstOrDefault(t => t.Name == "UnmanagedMemoryHandle");

        return type?.GetProperty(
            "TotalOutstandingHandles",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
    }

    [DllImport("libc", EntryPoint = "malloc_trim", SetLastError = true)]
    private static extern int NativeMallocTrim(nuint pad);

    public static int? MallocTrim()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            return NativeMallocTrim(0);
        }
        catch (EntryPointNotFoundException)
        {
            return null; // musl libc has no malloc_trim
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    public static long? ImageSharpOutstandingHandles()
        => OutstandingHandlesProperty?.GetValue(null) switch
        {
            int i => i,
            long l => l,
            _ => null,
        };

    public static Dictionary<string, object?> Snapshot(string label)
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo();

        return new Dictionary<string, object?>
        {
            ["label"] = label,
            ["utc"] = DateTime.UtcNow.ToString("O"),

            // Managed
            ["gcTotalMemoryBytes"] = GC.GetTotalMemory(false),
            ["gcHeapSizeBytes"] = info.HeapSizeBytes,
            ["gcFragmentedBytes"] = info.FragmentedBytes,
            ["gcCommittedBytes"] = info.TotalCommittedBytes,
            ["gcTotalAvailableMemoryBytes"] = info.TotalAvailableMemoryBytes,
            ["gcMemoryLoadBytes"] = info.MemoryLoadBytes,
            ["gcHighMemoryLoadThresholdBytes"] = info.HighMemoryLoadThresholdBytes,
            ["gcGen0Count"] = GC.CollectionCount(0),
            ["gcGen1Count"] = GC.CollectionCount(1),
            ["gcGen2Count"] = GC.CollectionCount(2),
            ["gcServerMode"] = GCSettings.IsServerGC,
            ["gcLatencyMode"] = GCSettings.LatencyMode.ToString(),

            // ImageSharp unmanaged pool
            ["imageSharpOutstandingHandles"] = ImageSharpOutstandingHandles(),
            ["imageSharpAllocatorType"] = Configuration.Default.MemoryAllocator.GetType().Name,

            // Process / OS
            ["totalAllocatedManagedBytes"] = GC.GetTotalAllocatedBytes(false),
            ["workingSetBytes"] = Environment.WorkingSet,
            ["procVmRssBytes"] = ReadProcStatusValueBytes("VmRSS"),
            ["procVmHwmBytes"] = ReadProcStatusValueBytes("VmHWM"),
            ["procVmSizeBytes"] = ReadProcStatusValueBytes("VmSize"),

            // cgroup (what Docker actually kills on)
            ["cgroupCurrentBytes"] = ReadFirstLong(
                "/sys/fs/cgroup/memory.current",
                "/sys/fs/cgroup/memory/memory.usage_in_bytes"),
            ["cgroupPeakBytes"] = ReadFirstLong(
                "/sys/fs/cgroup/memory.peak",
                "/sys/fs/cgroup/memory/memory.max_usage_in_bytes"),
            ["cgroupMaxBytes"] = ReadFirstLong(
                "/sys/fs/cgroup/memory.max",
                "/sys/fs/cgroup/memory/memory.limit_in_bytes"),
        };
    }

    private static long? ReadProcStatusValueBytes(string key)
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith(key + ":", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && long.TryParse(parts[1], out long kb))
                {
                    return kb * 1024;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static long? ReadFirstLong(params string[] paths)
    {
        foreach (string path in paths)
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                string text = File.ReadAllText(path).Trim();
                if (long.TryParse(text, out long value))
                {
                    return value;
                }

                return -1; // e.g. "max"
            }
            catch (IOException)
            {
            }
        }

        return null;
    }
}
