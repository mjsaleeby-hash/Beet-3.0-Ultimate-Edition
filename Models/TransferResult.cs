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
    public int DirectoriesFailed { get; set; }
    public int FilesDeleted { get; set; }
    public int DirectoriesDeleted { get; set; }
}
