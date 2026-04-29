using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

namespace BeetsBackup.Services;

/// <summary>Physical-layer classification of a drive — used to pick worker concurrency for parallel copy.</summary>
public enum DriveKind
{
    /// <summary>Solid-state storage (no seek penalty). Tolerates many parallel I/O workers.</summary>
    SSD,
    /// <summary>Spinning disk (seek penalty). Best with one worker — parallelism causes thrash.</summary>
    HDD,
    /// <summary>Network share. Moderate parallelism helps mask latency without saturating the link.</summary>
    Network,
    /// <summary>Removable media (USB stick, SD card). Treated like HDD for safety.</summary>
    Removable,
    /// <summary>Detection failed or path not on a recognizable volume.</summary>
    Unknown,
}

/// <summary>
/// Detects drive type (SSD, HDD, network, removable) for a given file-system path and computes
/// the recommended copy-worker concurrency for a source→destination pair.
///
/// Detection uses <c>IOCTL_STORAGE_QUERY_PROPERTY</c> with <c>StorageDeviceSeekPenaltyProperty</c>
/// to distinguish SSD from HDD; this is the same mechanism Windows uses internally for "Optimize
/// Drives" (formerly "Disk Defragmenter") to pick TRIM vs defrag. Results are cached per drive
/// root because the IOCTL involves opening a kernel handle and we may probe the same drive
/// hundreds of times during a single backup.
/// </summary>
public static class DriveTypeService
{
    /// <summary>Cache keyed by drive root (e.g. <c>"C:\\"</c> or <c>"\\\\server\\share\\"</c>).</summary>
    private static readonly ConcurrentDictionary<string, DriveKind> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maximum workers when both endpoints are SSD. Capped at 8 to avoid swamping the
    /// kernel's I/O scheduler on machines with many cores; 8 is the sweet spot per the original
    /// Windows practices audit.</summary>
    private const int MaxSsdWorkers = 8;

    /// <summary>Workers for cross-drive operations involving network shares. SMB benefits from
    /// some pipelining but more than ~4 streams hit diminishing returns and increase server load.</summary>
    private const int NetworkWorkers = 4;

    /// <summary>
    /// Classifies the drive on which <paramref name="path"/> lives. Network paths and removable
    /// volumes are detected via <see cref="DriveInfo"/>; SSD vs HDD is determined by an
    /// <c>IOCTL_STORAGE_QUERY_PROPERTY</c> call. Failures degrade to <see cref="DriveKind.Unknown"/>
    /// rather than throw — callers should treat Unknown as the conservative case.
    /// </summary>
    public static DriveKind GetDriveKind(string path)
    {
        if (string.IsNullOrEmpty(path))
            return DriveKind.Unknown;

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return DriveKind.Unknown;

        return _cache.GetOrAdd(root, ProbeDriveKind);
    }

    /// <summary>
    /// Returns the recommended <see cref="ParallelOptions.MaxDegreeOfParallelism"/> for copying
    /// from <paramref name="sourcePath"/> to <paramref name="destPath"/>. The decision is the
    /// most constraining of the two endpoints — a fast SSD source with an HDD dest is HDD-bound,
    /// not SSD-bound.
    /// </summary>
    public static int GetWorkerCount(string sourcePath, string destPath)
    {
        var src = GetDriveKind(sourcePath);
        var dst = GetDriveKind(destPath);
        return ComputeWorkers(src, dst);
    }

    /// <summary>
    /// Pure decision table for worker concurrency given the two drive kinds. Exposed for tests.
    /// </summary>
    internal static int ComputeWorkers(DriveKind source, DriveKind dest)
    {
        // Any HDD or Removable in the pair forces single-threaded — head-seek thrashing on a
        // spinning disk costs more than parallelism saves, and cheap USB controllers serialize
        // requests anyway.
        if (source == DriveKind.HDD || dest == DriveKind.HDD ||
            source == DriveKind.Removable || dest == DriveKind.Removable)
        {
            return 1;
        }

        // Network on either side: moderate parallelism. Going SSD→Network or Network→SSD still
        // hits SMB's per-stream latency wall, so Network dominates here.
        if (source == DriveKind.Network || dest == DriveKind.Network)
            return NetworkWorkers;

        // Both confirmed SSD: full fan-out.
        if (source == DriveKind.SSD && dest == DriveKind.SSD)
            return Math.Min(MaxSsdWorkers, Environment.ProcessorCount);

        // Anything Unknown: conservative middle ground. Better than 1 (we'd waste SSD parallelism
        // on misclassified drives) but well below SSD-SSD (we don't know if seeks are cheap).
        return 2;
    }

    /// <summary>Bypass the cache and re-probe a drive. Useful for tests; not used in production.</summary>
    internal static void ClearCache() => _cache.Clear();

    /// <summary>
    /// One-shot probe that consults <see cref="DriveInfo"/> for high-level type, then falls back
    /// to the IOCTL for fixed disks to distinguish SSD from HDD.
    /// </summary>
    private static DriveKind ProbeDriveKind(string root)
    {
        try
        {
            // UNC paths: DriveInfo throws for "\\server\share\" — treat any UNC root as Network.
            if (root.StartsWith(@"\\", StringComparison.Ordinal))
                return DriveKind.Network;

            var info = new DriveInfo(root);
            switch (info.DriveType)
            {
                case DriveType.Network: return DriveKind.Network;
                case DriveType.Removable: return DriveKind.Removable;
                case DriveType.CDRom: return DriveKind.Removable;
                case DriveType.Ram: return DriveKind.SSD; // RAM disks behave like SSDs to our scheduler
                case DriveType.Fixed:
                    var kind = ProbeSeekPenalty(root);
                    FileLogger.Info($"DriveTypeService: {root} (Fixed) → {kind}");
                    return kind;
                default:
                    FileLogger.Info($"DriveTypeService: {root} → Unknown (DriveType={info.DriveType})");
                    return DriveKind.Unknown;
            }
        }
        catch (Exception ex)
        {
            // Any unexpected failure (denied access, malformed root, etc.) — conservative default.
            FileLogger.Warn($"DriveTypeService: probe failed for {root}: {ex.GetType().Name}: {ex.Message}");
            return DriveKind.Unknown;
        }
    }

    /// <summary>
    /// Opens the physical volume and asks the storage driver whether seeks incur a penalty.
    /// IncursSeekPenalty=false means SSD; true means HDD. The volume must be opened with no
    /// access rights (just metadata query) so we don't need elevation for this IOCTL.
    /// </summary>
    private static DriveKind ProbeSeekPenalty(string root)
    {
        // Convert "C:\" → "\\.\C:" — the kernel object name for a volume handle. Trailing
        // backslash and longer paths don't open as a volume; only the drive-letter form works.
        var letter = root.Length >= 2 && root[1] == ':' ? root.Substring(0, 2) : null;
        if (letter == null)
        {
            FileLogger.Warn($"DriveTypeService: ProbeSeekPenalty bad root {root} (no drive letter)");
            return DriveKind.Unknown;
        }

        var volumePath = @"\\.\" + letter;
        IntPtr handle = CreateFileW(volumePath, 0, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1))
        {
            int err = Marshal.GetLastWin32Error();
            FileLogger.Warn($"DriveTypeService: CreateFile({volumePath}) failed, Win32Error={err}");
            return DriveKind.Unknown;
        }

        try
        {
            var query = new STORAGE_PROPERTY_QUERY
            {
                PropertyId = STORAGE_PROPERTY_ID.StorageDeviceSeekPenaltyProperty,
                QueryType = STORAGE_QUERY_TYPE.PropertyStandardQuery,
            };
            var result = new DEVICE_SEEK_PENALTY_DESCRIPTOR();
            int querySize = Marshal.SizeOf<STORAGE_PROPERTY_QUERY>();
            int resultSize = Marshal.SizeOf<DEVICE_SEEK_PENALTY_DESCRIPTOR>();

            bool ok = DeviceIoControl(
                handle,
                IOCTL_STORAGE_QUERY_PROPERTY,
                ref query, querySize,
                ref result, resultSize,
                out uint bytesReturned, IntPtr.Zero);

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                FileLogger.Warn($"DriveTypeService: DeviceIoControl({volumePath}) failed, Win32Error={err}");
                return DriveKind.Unknown;
            }
            FileLogger.Info($"DriveTypeService: {volumePath} IOCTL ok, IncursSeekPenalty={result.IncursSeekPenalty} (bytesReturned={bytesReturned})");
            return result.IncursSeekPenalty ? DriveKind.HDD : DriveKind.SSD;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ── PInvoke surface ──────────────────────────────────────────────────────────

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    private enum STORAGE_PROPERTY_ID
    {
        StorageDeviceSeekPenaltyProperty = 7,
    }

    private enum STORAGE_QUERY_TYPE
    {
        PropertyStandardQuery = 0,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public STORAGE_PROPERTY_ID PropertyId;
        public STORAGE_QUERY_TYPE QueryType;
        // Variable-length AdditionalParameters[1] omitted — not needed for SeekPenalty query.
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)]
        public bool IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref STORAGE_PROPERTY_QUERY lpInBuffer,
        int nInBufferSize,
        ref DEVICE_SEEK_PENALTY_DESCRIPTOR lpOutBuffer,
        int nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
