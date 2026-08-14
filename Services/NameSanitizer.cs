namespace BeetsBackup.Services;

/// <summary>
/// Shared helpers for converting user-supplied job names into safe filename fragments.
/// Both <see cref="SchedulerService"/> (compressed scheduled runs) and
/// <see cref="ViewModels.MainViewModel"/> (compressed manual runs) build archive
/// names from the same job string and used to maintain near-identical helpers —
/// any future tweak to the sanitization rules now lands in one place.
/// </summary>
public static class NameSanitizer
{
    /// <summary>
    /// Replaces invalid filename characters with underscores and trims trailing whitespace,
    /// periods, and tabs (Windows refuses to open filenames ending in those). Returns
    /// <c>"backup"</c> if the input sanitizes to an empty string.
    /// </summary>
    public static string Sanitize(string name)
    {
        var safe = string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        safe = safe.TrimEnd(' ', '.', '\t');
        return string.IsNullOrWhiteSpace(safe) ? "backup" : safe;
    }

    /// <summary>
    /// Builds a timestamped <c>.zip</c> archive filename from a job name —
    /// <c>"MyBackup_2026-05-20_11-37-04.zip"</c>. Second resolution.
    /// The caller passes this through <c>TransferService.GetUniqueFilePath</c>, which appends a
    /// <c>-1</c>, <c>-2</c>, … suffix. Note that it appends one UNCONDITIONALLY, so the archive
    /// that actually lands is always suffixed — see the archive-naming entry in notes/bugs.md.
    /// </summary>
    public static string BuildArchiveName(string jobName)
    {
        return $"{Sanitize(jobName)}_{System.DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip";
    }
}
