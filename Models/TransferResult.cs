namespace BeetsBackup.Models;

public class TransferResult
{
    public int FilesCopied { get; set; }
    public int FilesSkipped { get; set; }
    public int FilesFailed { get; set; }
    public long BytesTransferred { get; set; }
    public int TotalFiles { get; set; }
    public int ChecksumMismatches { get; set; }
    public int DiskFullErrors { get; set; }
    public int FilesLocked { get; set; }
    public int FilesCopiedViaVss { get; set; }
    public int DirectoriesFailed { get; set; }
    public int FilesDeleted { get; set; }
    public int DirectoriesDeleted { get; set; }

    /// <summary>Per-file error details. Capped at 200 entries to avoid unbounded growth.</summary>
    public List<FileError> FileErrors { get; } = new();

    private const int MaxFileErrors = 200;

    public void AddFileError(string path, string reason)
    {
        if (FileErrors.Count < MaxFileErrors)
            FileErrors.Add(new FileError { Path = path, Reason = reason });
    }
}

public class FileError
{
    public string Path { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
