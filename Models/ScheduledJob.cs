using System.Globalization;
using System.Text.Json.Serialization;

namespace BeetsBackup.Models;

/// <summary>
/// Persisted configuration for a scheduled or one-time backup job.
/// Serialized to JSON in the app's local data directory.
/// </summary>
public sealed class ScheduledJob
{
    /// <summary>Unique identifier for this job.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>User-assigned display name for this job.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>List of source directories or files to back up.</summary>
    public List<string> SourcePaths { get; set; } = new();

    /// <summary>Root destination directory.</summary>
    public string DestinationPath { get; set; } = string.Empty;

    /// <summary>Semicolon-joined source paths for display in the UI.</summary>
    [JsonIgnore]
    public string SourcePathsDisplay => string.Join("; ", SourcePaths);

    /// <summary>
    /// Backward-compatibility shim: older JSON used a single <c>SourcePath</c> field.
    /// Getter returns the first source path; setter populates the list from a single value.
    /// </summary>
    public string SourcePath
    {
        get => SourcePaths.Count > 0 ? SourcePaths[0] : string.Empty;
        set
        {
            if (SourcePaths.Count == 0 && !string.IsNullOrEmpty(value))
                SourcePaths.Add(value);
            else if (SourcePaths.Count > 0)
                SourcePaths[0] = value;
        }
    }

    /// <summary>Whether to strip NTFS permissions from copied files.</summary>
    public bool StripPermissions { get; set; }

    /// <summary>Whether to verify SHA-256 checksums after each file copy.</summary>
    public bool VerifyChecksums { get; set; }

    /// <summary>Conflict resolution strategy for this job.</summary>
    public TransferMode TransferMode { get; set; } = TransferMode.SkipExisting;

    /// <summary>Glob patterns for files/folders to exclude (e.g. "*.tmp", "node_modules").</summary>
    public List<string> ExclusionFilters { get; set; } = new();

    /// <summary>Speed limit in MB/s. 0 means unlimited.</summary>
    public int ThrottleMBps { get; set; }

    /// <summary>Whether this job repeats on a schedule.</summary>
    public bool IsRecurring { get; set; }

    /// <summary>Interval between recurring runs (null for one-time jobs).</summary>
    [JsonConverter(typeof(NullableTimeSpanConverter))]
    public TimeSpan? RecurInterval { get; set; }

    /// <summary>When this job should next execute.</summary>
    public DateTime NextRun { get; set; }

    /// <summary>When this job last completed (null if never run).</summary>
    public DateTime? LastRun { get; set; }

    /// <summary>Whether this job is active and should be considered by the scheduler.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Advances <see cref="NextRun"/> past the current time by repeatedly adding <see cref="RecurInterval"/>.
    /// Only applies to recurring jobs with a positive interval.
    /// </summary>
    public void UpdateNextRun()
    {
        if (IsRecurring && RecurInterval.HasValue && RecurInterval.Value > TimeSpan.Zero)
        {
            var now = DateTime.Now;
            while (NextRun <= now)
                NextRun = NextRun.Add(RecurInterval.Value);
        }
    }
}

/// <summary>
/// Custom JSON converter that serializes <see cref="TimeSpan"/> as an ISO 8601 duration string
/// (constant format "c"), handling null values gracefully.
/// </summary>
public sealed class NullableTimeSpanConverter : JsonConverter<TimeSpan?>
{
    /// <inheritdoc/>
    public override TimeSpan? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Null) return null;
        var str = reader.GetString();
        return str is not null ? TimeSpan.Parse(str, CultureInfo.InvariantCulture) : null;
    }

    /// <inheritdoc/>
    public override void Write(System.Text.Json.Utf8JsonWriter writer, TimeSpan? value, System.Text.Json.JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteStringValue(value.Value.ToString("c", CultureInfo.InvariantCulture));
        else
            writer.WriteNullValue();
    }
}
