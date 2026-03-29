using System.Text.Json.Serialization;

namespace BeetsBackup.Models;

public class ScheduledJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public List<string> SourcePaths { get; set; } = new();
    public string DestinationPath { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public string SourcePathsDisplay => string.Join("; ", SourcePaths);

    // Backward-compat: old JSON had single SourcePath
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
    public bool StripPermissions { get; set; }
    public bool VerifyChecksums { get; set; }
    public TransferMode TransferMode { get; set; } = TransferMode.SkipExisting;
    public List<string> ExclusionFilters { get; set; } = new();
    /// <summary>Speed limit in MB/s. 0 means unlimited.</summary>
    public int ThrottleMBps { get; set; }
    public bool IsRecurring { get; set; }

    [JsonConverter(typeof(NullableTimeSpanConverter))]
    public TimeSpan? RecurInterval { get; set; }
    public DateTime NextRun { get; set; }
    public DateTime? LastRun { get; set; }
    public bool IsEnabled { get; set; } = true;

    public void UpdateNextRun()
    {
        if (IsRecurring && RecurInterval.HasValue)
        {
            var now = DateTime.Now;
            while (NextRun <= now)
                NextRun = NextRun.Add(RecurInterval.Value);
        }
    }
}

public class NullableTimeSpanConverter : JsonConverter<TimeSpan?>
{
    public override TimeSpan? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Null) return null;
        var str = reader.GetString();
        return str != null ? TimeSpan.Parse(str) : null;
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, TimeSpan? value, System.Text.Json.JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteStringValue(value.Value.ToString());
        else
            writer.WriteNullValue();
    }
}
