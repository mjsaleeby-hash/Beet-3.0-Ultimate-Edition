using BeetsBackup.Models;
using BeetsBackup.Services;
using BeetsBackup.Tests.Infrastructure;
using FluentAssertions;

namespace BeetsBackup.Tests.Services;

/// <summary>
/// Coverage for file versioning, with emphasis on retention pruning. Pruning must rank versions by
/// the archive timestamp baked into each filename — NOT by content mtime, which is preserved across
/// a copy and therefore reflects when the user last edited the file, not when it was archived.
/// </summary>
public class VersioningServiceTests
{
    private const string VersionsFolder = ".versions";

    [Fact]
    [Trait("Category", "Integration")]
    public void ArchiveBeforeOverwrite_Prune_RanksByArchiveTimestamp_NotContentMtime()
    {
        using var tmp = new TempDirectory();
        var root = tmp.Path;
        var versionsDir = Path.Combine(root, VersionsFolder);
        Directory.CreateDirectory(versionsDir);

        // Pre-seed four archived versions of doc.txt. Filename timestamps run oldest -> newest by
        // YEAR, but we set each file's mtime in the OPPOSITE order. A prune that (incorrectly) sorts
        // by mtime would keep the wrong files; sorting by the filename timestamp keeps the newest
        // archives regardless of mtime.
        SeedVersion(versionsDir, "doc", "2020-01-01_00-00-00", mtimeUtc: new DateTime(2030, 1, 1));
        SeedVersion(versionsDir, "doc", "2021-01-01_00-00-00", mtimeUtc: new DateTime(2029, 1, 1));
        SeedVersion(versionsDir, "doc", "2022-01-01_00-00-00", mtimeUtc: new DateTime(2028, 1, 1));
        SeedVersion(versionsDir, "doc", "2023-01-01_00-00-00", mtimeUtc: new DateTime(2027, 1, 1));

        // The live file that's about to be overwritten — archiving it adds a fifth, "now"-stamped
        // version (the newest by filename), after which retention prunes down to MaxVersions.
        var destFile = Path.Combine(root, "doc.txt");
        File.WriteAllText(destFile, "current");

        var options = new VersioningOptions
        {
            Enabled = true,
            MaxVersions = 3,
            DestinationRoot = root,
        };

        var ok = VersioningService.ArchiveBeforeOverwrite(destFile, options);

        ok.Should().BeTrue();

        // Exactly MaxVersions archives survive: the just-created one plus the two newest by filename
        // timestamp (2023, 2022). The two oldest by filename timestamp (2021, 2020) are pruned —
        // even though 2020/2021 carry the NEWEST mtimes, which the old mtime-based sort would keep.
        var survivors = Directory.GetFiles(versionsDir, "doc__*.txt").Select(Path.GetFileName).ToList();
        survivors.Should().HaveCount(3);

        survivors.Should().Contain(n => n!.Contains("2023-01-01"),
            "the newest pre-seeded archive must be kept despite having an old mtime");
        survivors.Should().Contain(n => n!.Contains("2022-01-01"));
        survivors.Should().NotContain(n => n!.Contains("2020-01-01"),
            "the oldest archive must be pruned despite having the newest mtime");
        survivors.Should().NotContain(n => n!.Contains("2021-01-01"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void ArchiveBeforeOverwrite_ThenGetArchivedVersions_FindsTheArchivedCopy()
    {
        using var tmp = new TempDirectory();
        var destFile = Path.Combine(tmp.Path, "report.txt");
        File.WriteAllText(destFile, "v1 contents");

        var options = new VersioningOptions
        {
            Enabled = true,
            MaxVersions = 5,
            DestinationRoot = tmp.Path,
        };

        VersioningService.ArchiveBeforeOverwrite(destFile, options).Should().BeTrue();
        // Live file was moved into .versions, so the caller is now free to write the new copy.
        File.Exists(destFile).Should().BeFalse();

        var versions = VersioningService.GetArchivedVersions(destFile);

        versions.Should().ContainSingle();
        File.ReadAllText(versions[0].ArchivedPath).Should().Be("v1 contents");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GetArchivedVersions_ReturnsNewestFirst_ByArchiveTimestamp()
    {
        using var tmp = new TempDirectory();
        var root = tmp.Path;
        var versionsDir = Path.Combine(root, VersionsFolder);
        Directory.CreateDirectory(versionsDir);

        // Seed out of order; mtimes again contradict the filename order to prove the sort key.
        SeedVersion(versionsDir, "log", "2022-06-01_12-00-00", mtimeUtc: new DateTime(2000, 1, 1));
        SeedVersion(versionsDir, "log", "2024-06-01_12-00-00", mtimeUtc: new DateTime(2001, 1, 1));
        SeedVersion(versionsDir, "log", "2023-06-01_12-00-00", mtimeUtc: new DateTime(2002, 1, 1));

        var versions = VersioningService.GetArchivedVersions(Path.Combine(root, "log.txt"));

        versions.Select(v => v.ArchivedAt.Year).Should().ContainInOrder(2024, 2023, 2022);
    }

    /// <summary>Writes a single archive file matching the production naming scheme and stamps its
    /// content mtime to a value chosen to contradict the filename timestamp.</summary>
    private static void SeedVersion(string versionsDir, string nameNoExt, string timestamp, DateTime mtimeUtc)
    {
        var path = Path.Combine(versionsDir, $"{nameNoExt}__{timestamp}.txt");
        File.WriteAllText(path, $"archived {timestamp}");
        File.SetLastWriteTimeUtc(path, DateTime.SpecifyKind(mtimeUtc, DateTimeKind.Utc));
    }
}
