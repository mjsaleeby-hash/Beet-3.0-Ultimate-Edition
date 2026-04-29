using System.IO;
using System.Runtime.InteropServices;

namespace BeetsBackup.Services;

/// <summary>
/// Manages Volume Shadow Copy (VSS) snapshots via COM P/Invoke.
/// Creates on-demand snapshots per volume and caches them for the session.
/// Dispose to clean up all created snapshots.
/// </summary>
public sealed class VssSnapshotService : IDisposable
{
    // Snapshot cache: volume root (e.g. "C:\") → shadow device path
    private readonly Dictionary<string, string> _snapshotRoots = new();
    // Track snapshot set IDs for cleanup
    private readonly List<(IVssBackupComponents components, Guid snapshotSetId, Guid snapshotId)> _snapshots = new();
    // Serializes snapshot creation across parallel copy workers. Without this two workers hitting
    // locked files on the same volume could race in CreateSnapshot, double-create snapshots, and
    // leak COM handles. Granularity is per-volume in spirit (ConcurrentDictionary<volume, Lazy<>>
    // would express that better) but a single lock is fine here — VSS create is rare, slow when
    // it does happen, and we don't want to create two on the same volume in parallel anyway.
    private readonly object _snapshotLock = new();
    private bool _disposed;

    /// <summary>
    /// Gets the shadow copy device path for the volume containing the given file.
    /// Creates a new VSS snapshot if one doesn't already exist for that volume.
    /// Thread-safe: parallel copy workers can call this concurrently.
    /// </summary>
    /// <param name="filePath">Full path to the locked file (e.g. C:\Users\foo\bar.db)</param>
    /// <returns>Shadow device root (e.g. \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3)</returns>
    /// <exception cref="InvalidOperationException">VSS is unavailable (not elevated, service stopped, etc.)</exception>
    public string GetOrCreateSnapshotRoot(string filePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var volumeRoot = Path.GetPathRoot(filePath)
            ?? throw new ArgumentException($"Cannot determine volume root for: {filePath}");

        lock (_snapshotLock)
        {
            // Return cached snapshot if we already have one for this volume.
            if (_snapshotRoots.TryGetValue(volumeRoot, out var cached))
                return cached;

            FileLogger.Info($"Creating VSS snapshot for volume {volumeRoot}");

            try
            {
                var snapshotDevicePath = CreateSnapshot(volumeRoot);
                _snapshotRoots[volumeRoot] = snapshotDevicePath;
                FileLogger.Info($"VSS snapshot created: {volumeRoot} → {snapshotDevicePath}");
                return snapshotDevicePath;
            }
            catch (COMException ex)
            {
                FileLogger.Error($"VSS failed for {volumeRoot}: 0x{ex.HResult:X8} — {ex.Message}");
                throw new InvalidOperationException(
                    $"VSS snapshot failed for {volumeRoot}. The application may need to run as Administrator.", ex);
            }
            catch (Exception ex)
            {
                FileLogger.Error($"VSS failed for {volumeRoot}: {ex.Message}");
                throw new InvalidOperationException($"VSS snapshot failed for {volumeRoot}: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Translates a normal file path to its shadow copy equivalent.
    /// E.g. C:\Users\foo\bar.db → \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3\Users\foo\bar.db
    /// </summary>
    public string GetShadowPath(string filePath, string snapshotRoot)
    {
        // Strip the volume root (e.g. "C:\") and append to shadow device root
        var root = Path.GetPathRoot(filePath) ?? "";
        return snapshotRoot + filePath.Substring(root.TrimEnd('\\').Length);
    }

    private string CreateSnapshot(string volumeRoot)
    {
        // Step 1: Create the backup components COM object
        int hr = NativeMethods.CreateVssBackupComponents(out var backupComponents);
        if (hr != 0)
            Marshal.ThrowExceptionForHR(hr);

        try
        {
            // Step 2: Initialize for backup
            hr = backupComponents.InitializeForBackup(null);
            Marshal.ThrowExceptionForHR(hr);

            // Step 3: Set backup state — we're doing a file backup, not a full system backup
            hr = backupComponents.SetBackupState(false, true, VSS_BACKUP_TYPE.VSS_BT_COPY, false);
            Marshal.ThrowExceptionForHR(hr);

            // Step 4: Gather writer metadata (required by VSS state machine)
            hr = backupComponents.GatherWriterMetadata(out var asyncGather);
            Marshal.ThrowExceptionForHR(hr);
            WaitForAsync(asyncGather);

            // Step 5: Start a snapshot set
            hr = backupComponents.StartSnapshotSet(out var snapshotSetId);
            Marshal.ThrowExceptionForHR(hr);

            // Step 6: Add the volume to the snapshot set
            // Ensure volume root ends with backslash (VSS requires it)
            var volume = volumeRoot.EndsWith('\\') ? volumeRoot : volumeRoot + "\\";
            hr = backupComponents.AddToSnapshotSet(volume, Guid.Empty, out var snapshotId);
            Marshal.ThrowExceptionForHR(hr);

            // Step 7: Prepare for backup
            hr = backupComponents.PrepareForBackup(out var asyncPrepare);
            Marshal.ThrowExceptionForHR(hr);
            WaitForAsync(asyncPrepare);

            // Step 8: Execute the snapshot
            hr = backupComponents.DoSnapshotSet(out var asyncSnapshot);
            Marshal.ThrowExceptionForHR(hr);
            WaitForAsync(asyncSnapshot);

            // Step 9: Get the snapshot properties to find the device path
            hr = backupComponents.GetSnapshotProperties(snapshotId, out var props);
            Marshal.ThrowExceptionForHR(hr);

            string devicePath;
            try
            {
                devicePath = Marshal.PtrToStringUni(props.m_pwszSnapshotDeviceObject) ?? "";
            }
            finally
            {
                NativeMethods.VssFreeSnapshotProperties(ref props);
            }

            if (string.IsNullOrEmpty(devicePath))
                throw new InvalidOperationException("VSS returned empty snapshot device path");

            // Track for cleanup
            _snapshots.Add((backupComponents, snapshotSetId, snapshotId));

            // Remove trailing backslash if present so path concatenation works cleanly
            return devicePath.TrimEnd('\\');
        }
        catch
        {
            // If snapshot creation failed, release the COM object
            try { Marshal.ReleaseComObject(backupComponents); } catch { }
            throw;
        }
    }

    private static void WaitForAsync(IVssAsync vssAsync)
    {
        try
        {
            // Wait up to 60 seconds for the async operation
            int hr = vssAsync.Wait(60000);
            Marshal.ThrowExceptionForHR(hr);

            hr = vssAsync.QueryStatus(out int statusHr, out _);
            Marshal.ThrowExceptionForHR(hr);
            Marshal.ThrowExceptionForHR(statusHr);
        }
        finally
        {
            Marshal.ReleaseComObject(vssAsync);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var (components, snapshotSetId, _) in _snapshots)
        {
            try
            {
                // Delete the snapshot set — this removes the shadow copies from disk
                components.DeleteSnapshots(snapshotSetId, VSS_OBJECT_TYPE.VSS_OBJECT_SNAPSHOT_SET,
                    true, out _, out _);
            }
            catch (Exception ex)
            {
                FileLogger.Warn($"Failed to delete VSS snapshot set {snapshotSetId}: {ex.Message}");
            }
            finally
            {
                try { Marshal.ReleaseComObject(components); } catch { }
            }
        }

        _snapshots.Clear();
        _snapshotRoots.Clear();
        FileLogger.Info("VSS snapshots disposed");
    }

    // ─── P/Invoke declarations ──────────────────────────────────────────

    private static class NativeMethods
    {
        [DllImport("vssapi.dll", EntryPoint = "CreateVssBackupComponentsInternal")]
        public static extern int CreateVssBackupComponents(
            [MarshalAs(UnmanagedType.Interface)] out IVssBackupComponents backupComponents);

        [DllImport("vssapi.dll")]
        public static extern void VssFreeSnapshotProperties(ref VSS_SNAPSHOT_PROP pProp);
    }

    // ─── VSS COM interfaces ─────────────────────────────────────────────

    private enum VSS_BACKUP_TYPE
    {
        VSS_BT_UNDEFINED = 0,
        VSS_BT_FULL = 1,
        VSS_BT_INCREMENTAL = 2,
        VSS_BT_DIFFERENTIAL = 3,
        VSS_BT_LOG = 4,
        VSS_BT_COPY = 5,
        VSS_BT_OTHER = 6,
    }

    private enum VSS_OBJECT_TYPE
    {
        VSS_OBJECT_UNKNOWN = 0,
        VSS_OBJECT_NONE = 1,
        VSS_OBJECT_SNAPSHOT_SET = 2,
        VSS_OBJECT_SNAPSHOT = 3,
        VSS_OBJECT_PROVIDER = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VSS_SNAPSHOT_PROP
    {
        public Guid m_SnapshotId;
        public Guid m_SnapshotSetId;
        public int m_lSnapshotsCount;
        public IntPtr m_pwszSnapshotDeviceObject;
        public IntPtr m_pwszOriginalVolumeName;
        public IntPtr m_pwszOriginatingMachine;
        public IntPtr m_pwszServiceMachine;
        public IntPtr m_pwszExposedName;
        public IntPtr m_pwszExposedPath;
        public Guid m_ProviderId;
        public int m_lSnapshotAttributes;
        public long m_tsCreationTimestamp;
        public int m_eStatus;
    }

    [ComImport]
    [Guid("507C37B4-CF5B-4e95-B0AF-14EB9767467E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVssAsync
    {
        void Cancel();
        [PreserveSig]
        int Wait([In] uint dwMilliseconds);
        [PreserveSig]
        int QueryStatus([Out] out int pHrResult, [Out] out int pReserved);
    }

    /// <summary>
    /// IVssBackupComponents COM interface — only the methods we need are declared.
    /// Methods are declared in vtable order; unused slots use placeholder signatures.
    /// </summary>
    [ComImport]
    [Guid("665c1d5f-c218-414d-a05d-7fef5f9d5c86")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVssBackupComponents
    {
        // Vtable slot 3
        [PreserveSig]
        int GetWriterComponentsCount(out uint pcComponents);

        // Vtable slot 4
        [PreserveSig]
        int GetWriterComponents(uint iWriter, [MarshalAs(UnmanagedType.Interface)] out object ppWriter);

        // Vtable slot 5
        [PreserveSig]
        int InitializeForBackup([MarshalAs(UnmanagedType.BStr)] string? bstrXML);

        // Vtable slot 6
        [PreserveSig]
        int SetBackupState(
            [MarshalAs(UnmanagedType.Bool)] bool bSelectComponents,
            [MarshalAs(UnmanagedType.Bool)] bool bBackupBootableSystemState,
            VSS_BACKUP_TYPE backupType,
            [MarshalAs(UnmanagedType.Bool)] bool bPartialFileSupport);

        // Vtable slot 7
        [PreserveSig]
        int InitializeForRestore([MarshalAs(UnmanagedType.BStr)] string bstrXML);

        // Vtable slot 8
        [PreserveSig]
        int SetRestoreState(int restoreType);

        // Vtable slot 9
        [PreserveSig]
        int GatherWriterMetadata([MarshalAs(UnmanagedType.Interface)] out IVssAsync pAsync);

        // Vtable slot 10
        [PreserveSig]
        int GetWriterMetadataCount(out uint pcWriters);

        // Vtable slot 11
        [PreserveSig]
        int GetWriterMetadata(uint iWriter, out Guid pidInstance, [MarshalAs(UnmanagedType.Interface)] out object ppMetadata);

        // Vtable slot 12
        [PreserveSig]
        int FreeWriterMetadata();

        // Vtable slot 13
        [PreserveSig]
        int AddComponent(Guid instanceId, Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName);

        // Vtable slot 14
        [PreserveSig]
        int PrepareForBackup([MarshalAs(UnmanagedType.Interface)] out IVssAsync pAsync);

        // Vtable slot 15
        [PreserveSig]
        int AbortBackup();

        // Vtable slot 16
        [PreserveSig]
        int GatherWriterStatus([MarshalAs(UnmanagedType.Interface)] out IVssAsync pAsync);

        // Vtable slot 17
        [PreserveSig]
        int GetWriterStatusCount(out uint pcWriters);

        // Vtable slot 18
        [PreserveSig]
        int FreeWriterStatus();

        // Vtable slot 19
        [PreserveSig]
        int GetWriterStatus(uint iWriter, out Guid pidInstance, out Guid pidWriter, [MarshalAs(UnmanagedType.BStr)] out string pbstrWriter, out int pnStatus, out int phrFailure);

        // Vtable slot 20
        [PreserveSig]
        int SetBackupSucceeded(Guid instanceId, Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, [MarshalAs(UnmanagedType.Bool)] bool bSucceeded);

        // Vtable slot 21
        [PreserveSig]
        int SetBackupOptions(Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, [MarshalAs(UnmanagedType.LPWStr)] string wszBackupOptions);

        // Vtable slot 22
        [PreserveSig]
        int SetSelectedForRestore(Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, [MarshalAs(UnmanagedType.Bool)] bool bSelectedForRestore);

        // Vtable slot 23
        [PreserveSig]
        int SetRestoreOptions(Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, [MarshalAs(UnmanagedType.LPWStr)] string wszRestoreOptions);

        // Vtable slot 24
        [PreserveSig]
        int SetAdditionalRestores(Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, [MarshalAs(UnmanagedType.Bool)] bool bAdditionalRestores);

        // Vtable slot 25
        [PreserveSig]
        int SetPreviousBackupStamp(Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, [MarshalAs(UnmanagedType.LPWStr)] string wszPreviousBackupStamp);

        // Vtable slot 26
        [PreserveSig]
        int SaveAsXML([MarshalAs(UnmanagedType.BStr)] out string pbstrXML);

        // Vtable slot 27
        [PreserveSig]
        int BackupComplete([MarshalAs(UnmanagedType.Interface)] out IVssAsync pAsync);

        // Vtable slot 28
        [PreserveSig]
        int AddAlternativeLocationMapping(Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, [MarshalAs(UnmanagedType.LPWStr)] string wszPath, [MarshalAs(UnmanagedType.LPWStr)] string wszFilespec, [MarshalAs(UnmanagedType.Bool)] bool bRecursive, [MarshalAs(UnmanagedType.LPWStr)] string wszDestination);

        // Vtable slot 29
        [PreserveSig]
        int AddRestoreSubcomponent(Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, [MarshalAs(UnmanagedType.LPWStr)] string wszSubComponentLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszSubComponentName, [MarshalAs(UnmanagedType.Bool)] bool bRepair);

        // Vtable slot 30
        [PreserveSig]
        int SetFileRestoreStatus(Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, int status);

        // Vtable slot 31
        [PreserveSig]
        int AddNewTarget(Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, [MarshalAs(UnmanagedType.LPWStr)] string wszPath, [MarshalAs(UnmanagedType.LPWStr)] string wszFileName, [MarshalAs(UnmanagedType.Bool)] bool bRecursive, [MarshalAs(UnmanagedType.LPWStr)] string wszAlternatePath);

        // Vtable slot 32
        [PreserveSig]
        int SetRangesFilePath(Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, uint iPartialFile, [MarshalAs(UnmanagedType.LPWStr)] string wszRangesFile);

        // Vtable slot 33
        [PreserveSig]
        int PreRestore([MarshalAs(UnmanagedType.Interface)] out IVssAsync pAsync);

        // Vtable slot 34
        [PreserveSig]
        int PostRestore([MarshalAs(UnmanagedType.Interface)] out IVssAsync pAsync);

        // Vtable slot 35
        [PreserveSig]
        int SetContext(int lContext);

        // Vtable slot 36
        [PreserveSig]
        int StartSnapshotSet(out Guid pSnapshotSetId);

        // Vtable slot 37
        [PreserveSig]
        int AddToSnapshotSet(
            [MarshalAs(UnmanagedType.LPWStr)] string pwszVolumeName,
            Guid ProviderId,
            out Guid pidSnapshot);

        // Vtable slot 38
        [PreserveSig]
        int DoSnapshotSet([MarshalAs(UnmanagedType.Interface)] out IVssAsync pAsync);

        // Vtable slot 39
        [PreserveSig]
        int DeleteSnapshots(
            Guid SourceObjectId,
            VSS_OBJECT_TYPE eSourceObjectType,
            [MarshalAs(UnmanagedType.Bool)] bool bForceDelete,
            out int plDeletedSnapshots,
            out Guid pNondeletedSnapshotID);

        // Vtable slot 40
        [PreserveSig]
        int ImportSnapshots([MarshalAs(UnmanagedType.Interface)] out IVssAsync pAsync);

        // Vtable slot 41
        [PreserveSig]
        int BreakSnapshotSet(Guid SnapshotSetId);

        // Vtable slot 42
        [PreserveSig]
        int GetSnapshotProperties(
            Guid SnapshotId,
            out VSS_SNAPSHOT_PROP pProp);

        // Vtable slot 43
        [PreserveSig]
        int Query(Guid QueriedObjectId, int eQueriedObjectType, int eReturnedObjectsType, [MarshalAs(UnmanagedType.Interface)] out object ppEnum);

        // Vtable slot 44
        [PreserveSig]
        int IsVolumeSupported(Guid ProviderId, [MarshalAs(UnmanagedType.LPWStr)] string pwszVolumeName, [MarshalAs(UnmanagedType.Bool)] out bool pbSupportedByThisProvider);

        // Vtable slot 45
        [PreserveSig]
        int DisableWriterClasses([MarshalAs(UnmanagedType.LPArray)] Guid[] rgWriterClassId, uint cClassId);

        // Vtable slot 46
        [PreserveSig]
        int EnableWriterClasses([MarshalAs(UnmanagedType.LPArray)] Guid[] rgWriterClassId, uint cClassId);

        // Vtable slot 47
        [PreserveSig]
        int DisableWriterInstances([MarshalAs(UnmanagedType.LPArray)] Guid[] rgWriterInstanceId, uint cInstanceId);

        // Vtable slot 48
        [PreserveSig]
        int ExposeSnapshot(Guid SnapshotId, [MarshalAs(UnmanagedType.LPWStr)] string wszPathFromRoot, int lAttributes, [MarshalAs(UnmanagedType.LPWStr)] string wszExpose, [MarshalAs(UnmanagedType.LPWStr)] out string pwszExposed);

        // Vtable slot 49
        [PreserveSig]
        int RevertToSnapshot(Guid SnapshotId, [MarshalAs(UnmanagedType.Bool)] bool bForceDismount);

        // Vtable slot 50
        [PreserveSig]
        int QueryRevertStatus([MarshalAs(UnmanagedType.LPWStr)] string pwszVolume, [MarshalAs(UnmanagedType.Interface)] out IVssAsync ppAsync);
    }
}
