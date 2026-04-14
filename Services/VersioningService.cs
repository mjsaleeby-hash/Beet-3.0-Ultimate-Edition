using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using BeetsBackup.Models;

namespace BeetsBackup.Services;

/// <summary>
/// Per-transfer options controlling file versioning behavior.
/// </summary>
public sealed class VersioningOptions
{
    /// <summary>When <c>true</c>, the existing destination copy is archived to <c>.versions/</c> before being overwritten.</summary>
    public bool Enabled { get; set; }

    /// <summary>Maximum number of archived versions to retain per file; older versions are pruned. Minimum 1.</summary>
    public int MaxVersions { get; set; } = 5;

    /// <summary>The root of the destination hierarchy — used to locate the <c>.versions/</c> folder and compute relative paths.</summary>
    public string DestinationRoot { get; set; } = string.Empty;

    /// <summary>Folder name used for the versioned archive (hidden sibling at the destination root).</summary>
    public const string VersionsFolderName = ".versions";
}

/// <summary>
/// Handles archiving of existing destination files into a <c>.versions/</c> subfolder before they are overwritten,
/// and prunes older versions beyond the configured retention count.
/// </summary>
/// <remarks>
/// Version archive path format: <c>{DestinationRoot}\.versions\{relativeDir}\{nameNoExt}__{yyyy-MM-dd_HH-mm-ss}{ext}</c>.
/// All operations are best-effort — failures never bubble up to abort a transfer; they're logged instead.
/// </remarks>
public static class VersioningService
{
    /// <summary>
    /// If <paramref name="destFile"/> exists, moves it into the <c>.versions</c> archive folder
    /// with a timestamp suffix, then prunes older versions beyond <see cref="VersioningOptions.MaxVersions"/>.
    /// </summary>
    /// <param name="destFile">Full path of the destination file that is about to be overwritten.</param>
    /// <param name="options">Versioning options. No-op if <see cref="VersioningOptions.Enabled"/> is false.</param>
    /// <returns>
    /// <c>true</c> if the caller may safely overwrite <paramref name="destFile"/> — either because versioning
    /// was disabled/skipped, because the destination did not exist, or because the existing copy was
    /// successfully archived. <c>false</c> if versioning was requested but archiving failed; in that case
    /// the caller MUST NOT destroy the destination file.
    /// </returns>
    public static bool ArchiveBeforeOverwrite(string destFile, VersioningOptions? options)
    {
        // No versioning requested → caller is free to overwrite.
        if (options == null || !options.Enabled) return true;
        // Nothing to archive → caller is free to overwrite.
        if (!File.Exists(destFile)) return true;
        // Misconfigured (no destination root) → skip archiving but allow overwrite to preserve prior behavior.
        if (string.IsNullOrEmpty(options.DestinationRoot)) return true;

        try
        {
            var rel = Path.GetRelativePath(options.DestinationRoot, destFile);

            // Guard: only archive files that actually live under the destination root.
            // If dest is outside the tree we can't place a versioned copy, so allow plain overwrite.
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
                return true;

            // Never archive the .versions folder's own contents — plain overwrite is fine.
            if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   .Any(seg => seg.Equals(VersioningOptions.VersionsFolderName, StringComparison.OrdinalIgnoreCase)))
                return true;

            var relDir = Path.GetDirectoryName(rel) ?? string.Empty;
            var versionsDir = Path.Combine(options.DestinationRoot, VersioningOptions.VersionsFolderName, relDir);
            Directory.CreateDirectory(versionsDir);

            // Mark the top-level .versions folder as hidden so it doesn't clutter the destination view
            try
            {
                var topVersionsDir = Path.Combine(options.DestinationRoot, VersioningOptions.VersionsFolderName);
                var attrs = File.GetAttributes(topVersionsDir);
                if (!attrs.HasFlag(FileAttributes.Hidden))
                    File.SetAttributes(topVersionsDir, attrs | FileAttributes.Hidden);
            }
            catch { /* best-effort hide */ }

            var nameNoExt = Path.GetFileNameWithoutExtension(destFile);
            var ext = Path.GetExtension(destFile);
            var ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var archivePath = Path.Combine(versionsDir, $"{nameNoExt}__{ts}{ext}");

            // If the same second produces two version files, disambiguate with a counter
            int counter = 1;
            while (File.Exists(archivePath))
            {
                archivePath = Path.Combine(versionsDir, $"{nameNoExt}__{ts}_{counter}{ext}");
                counter++;
            }

            File.Move(destFile, archivePath);

            PruneOldVersions(versionsDir, nameNoExt, ext, Math.Max(1, options.MaxVersions));
            return true;
        }
        catch (Exception ex)
        {
            // Archiving failed — signal the caller to preserve the destination rather than destroy an
            // un-versioned copy the user asked us to protect.
            FileLogger.Warn($"Versioning archive failed for {destFile}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Walks up the directory tree from <paramref name="file"/> looking for the first ancestor that
    /// contains a <c>.versions</c> subfolder. Returns the ancestor path (the destination root) or
    /// <c>null</c> if no versioned ancestor is found.
    /// </summary>
    /// <remarks>
    /// This is how "Previous Versions…" locates a file's version archive without knowing up-front
    /// which destination produced it — the caller just passes a live file path and we discover the
    /// archive root by walking up.
    /// </remarks>
    public static string? FindVersionsRoot(string file)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(file));
            while (!string.IsNullOrEmpty(dir))
            {
                var candidate = Path.Combine(dir, VersioningOptions.VersionsFolderName);
                if (Directory.Exists(candidate)) return dir;
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { /* best-effort discovery */ }
        return null;
    }

    /// <summary>
    /// Matches the archive filename suffix produced by <see cref="ArchiveBeforeOverwrite"/>:
    /// <c>{nameNoExt}__{yyyy-MM-dd_HH-mm-ss}[_counter]{ext}</c>. Captures the timestamp group.
    /// </summary>
    private static readonly Regex ArchiveSuffixRegex = new(
        @"__(?<ts>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})(?:_\d+)?$",
        RegexOptions.Compiled);

    /// <summary>
    /// Enumerates archived versions of the given live file, sorted newest-first.
    /// Returns an empty list if the file's destination root has no <c>.versions</c> folder
    /// or contains no archives for this specific filename.
    /// </summary>
    /// <param name="liveFile">Full path to the file whose history you want to see.</param>
    public static IReadOnlyList<ArchivedVersion> GetArchivedVersions(string liveFile)
    {
        var results = new List<ArchivedVersion>();
        try
        {
            var root = FindVersionsRoot(liveFile);
            if (root == null) return results;

            var rel = Path.GetRelativePath(root, liveFile);
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
                return results;

            var relDir = Path.GetDirectoryName(rel) ?? string.Empty;
            var versionsDir = Path.Combine(root, VersioningOptions.VersionsFolderName, relDir);
            if (!Directory.Exists(versionsDir)) return results;

            var nameNoExt = Path.GetFileNameWithoutExtension(liveFile);
            var ext = Path.GetExtension(liveFile);
            var pattern = $"{nameNoExt}__*{ext}";

            foreach (var path in Directory.EnumerateFiles(versionsDir, pattern))
            {
                // Skip anything that doesn't match our exact naming scheme — avoids surfacing
                // unrelated files that happened to start with the same base name.
                var body = Path.GetFileNameWithoutExtension(path);
                var m = ArchiveSuffixRegex.Match(body);
                if (!m.Success) continue;
                if (!body.StartsWith($"{nameNoExt}__", StringComparison.OrdinalIgnoreCase)) continue;

                if (!DateTime.TryParseExact(m.Groups["ts"].Value, "yyyy-MM-dd_HH-mm-ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
                    continue;

                long size = 0;
                try { size = new FileInfo(path).Length; } catch { /* unreadable */ }
                results.Add(new ArchivedVersion(path, liveFile, ts, size));
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Version enumeration failed for {liveFile}: {ex.Message}");
        }

        results.Sort((a, b) => b.ArchivedAt.CompareTo(a.ArchivedAt));
        return results;
    }

    /// <summary>
    /// Restores an archived version by copying it back over the live file. The current live copy
    /// (if any) is FIRST archived to <c>.versions</c> — so a restore itself is reversible.
    /// </summary>
    /// <param name="version">The version chosen from <see cref="GetArchivedVersions"/>.</param>
    /// <param name="maxVersions">Retention limit to apply after the new "pre-restore" version is archived.</param>
    /// <returns><c>true</c> on success, <c>false</c> if any step failed.</returns>
    public static bool RestoreVersion(ArchivedVersion version, int maxVersions = 5)
    {
        try
        {
            var root = FindVersionsRoot(version.OriginalPath);
            if (root == null)
            {
                FileLogger.Warn($"Cannot restore: no .versions root found for {version.OriginalPath}");
                return false;
            }

            // Archive the CURRENT live copy before overwriting, so the user can undo the restore.
            // We swallow the bool here — even if archiving fails we still let the user choose to
            // restore (they explicitly asked to roll back), but log the situation loudly.
            var preRestore = new VersioningOptions
            {
                Enabled = true,
                MaxVersions = maxVersions,
                DestinationRoot = root
            };
            if (File.Exists(version.OriginalPath))
            {
                if (!ArchiveBeforeOverwrite(version.OriginalPath, preRestore))
                    FileLogger.Warn($"Pre-restore archive failed for {version.OriginalPath}; proceeding with restore anyway.");

                // ArchiveBeforeOverwrite moved the file, but defensively ensure it's gone.
                if (File.Exists(version.OriginalPath))
                {
                    try { File.Delete(version.OriginalPath); }
                    catch (Exception ex)
                    {
                        FileLogger.Warn($"Could not clear live file before restore: {ex.Message}");
                        return false;
                    }
                }
            }

            // Ensure the target directory exists (it should, but a user may have deleted it).
            var targetDir = Path.GetDirectoryName(version.OriginalPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            File.Copy(version.ArchivedPath, version.OriginalPath, overwrite: false);
            FileLogger.Info($"Restored version {version.ArchivedAt:yyyy-MM-dd HH:mm:ss} → {version.OriginalPath}");
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.LogException($"Restore failed for {version.OriginalPath}", ex);
            return false;
        }
    }

    /// <summary>
    /// Permanently deletes a single archived version file.
    /// </summary>
    /// <returns><c>true</c> if the file was deleted or didn't exist; <c>false</c> on error.</returns>
    public static bool DeleteArchivedVersion(ArchivedVersion version)
    {
        try
        {
            if (File.Exists(version.ArchivedPath))
                File.Delete(version.ArchivedPath);
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Failed to delete archived version {version.ArchivedPath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Deletes archived versions for a given file name beyond the retention limit, keeping the most recent.
    /// </summary>
    private static void PruneOldVersions(string versionsDir, string nameNoExt, string ext, int maxVersions)
    {
        try
        {
            // Match only this file's archived versions — not unrelated siblings
            var pattern = $"{nameNoExt}__*{ext}";
            var stale = Directory.EnumerateFiles(versionsDir, pattern)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(maxVersions)
                .ToList();
            foreach (var old in stale)
            {
                try { File.Delete(old); }
                catch (Exception ex) { FileLogger.Warn($"Failed to prune old version {old}: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Version prune scan failed in {versionsDir}: {ex.Message}");
        }
    }
}
