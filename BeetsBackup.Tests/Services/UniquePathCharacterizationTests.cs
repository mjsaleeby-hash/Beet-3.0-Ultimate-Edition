using System.Collections.Generic;
using System.IO;
using BeetsBackup.Services;
using BeetsBackup.Tests.Infrastructure;
using FluentAssertions;

namespace BeetsBackup.Tests.Services;

/// <summary>
/// CHARACTERIZATION tests. These pin what the uniqueness helpers do TODAY — including one known
/// defect — so Wave 2.6b's collapse cannot change behaviour without a test going red. They are
/// not specifications of desired behaviour, and they were written to pass before any refactor.
/// </summary>
public class UniquePathCharacterizationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void GetUniqueFolderPath_FirstCollision_AppendsDashOne()
    {
        using var tmp = new TempDirectory();
        var target = Path.Combine(tmp.Path, "Photos");
        Directory.CreateDirectory(target);

        TransferService.GetUniqueFolderPath(target)
            .Should().Be(Path.Combine(tmp.Path, "Photos-1"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GetUniqueFolderPath_SkipsNamesAlreadyTaken()
    {
        using var tmp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "Photos"));
        Directory.CreateDirectory(Path.Combine(tmp.Path, "Photos-1"));
        Directory.CreateDirectory(Path.Combine(tmp.Path, "Photos-2"));

        TransferService.GetUniqueFolderPath(Path.Combine(tmp.Path, "Photos"))
            .Should().Be(Path.Combine(tmp.Path, "Photos-3"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GetUniqueFolderPath_DottedFolderName_KeepsTheWholeNameIntact()
    {
        // THE test that protects the Task 8 merge. Folders split their name with
        // Path.GetFileName; files split it with GetFileNameWithoutExtension + GetExtension.
        // Merging the two loops on the FILE-style split would turn "my.folder" into
        // "my-1.folder" instead of "my.folder-1".
        using var tmp = new TempDirectory();
        var target = Path.Combine(tmp.Path, "my.folder");
        Directory.CreateDirectory(target);

        TransferService.GetUniqueFolderPath(target)
            .Should().Be(Path.Combine(tmp.Path, "my.folder-1"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GetUniqueFilePath_OnAFreeName_STILL_AppendsDashOne()
    {
        // KNOWN DEFECT, pinned deliberately. The candidate is built before existence is tested,
        // so the original name can never be returned. The KeepBoth caller guards with
        // File.Exists and is therefore correct; the archive caller does not, which is why every
        // compressed archive is suffixed. See notes/bugs.md 2026-08-14.
        // DO NOT "fix" this test. Fixing the behaviour is a separate, user-visible decision.
        using var tmp = new TempDirectory();

        TransferService.GetUniqueFilePath(Path.Combine(tmp.Path, "Backup.zip"))
            .Should().Be(Path.Combine(tmp.Path, "Backup-1.zip"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GetUniqueFolderPathReserved_WhenFreeAndUnclaimed_ReturnsTheOriginal()
    {
        // This is the contract that makes the Reserved variant DIFFERENT from the plain one,
        // and the reason the report's "collapse 4 helpers to 2" would have changed where files
        // land. The plain variant always starts at -1; this one returns the original.
        using var tmp = new TempDirectory();
        var reserved = new HashSet<string>();
        var target = Path.Combine(tmp.Path, "Documents");

        TransferService.GetUniqueFolderPathReserved(target, reserved)
            .Should().Be(target);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GetUniqueFolderPathReserved_SecondSourceSameRun_GetsRenamedNotMerged()
    {
        // Two sources contributing a folder of the same name within ONE run. Neither exists on
        // disk, so a filesystem-only check would hand both the same destination and silently
        // merge them. The reservation set is what prevents that.
        using var tmp = new TempDirectory();
        var reserved = new HashSet<string>();
        var target = Path.Combine(tmp.Path, "Documents");

        var first = TransferService.GetUniqueFolderPathReserved(target, reserved);
        var second = TransferService.GetUniqueFolderPathReserved(target, reserved);

        first.Should().Be(target);
        second.Should().Be(Path.Combine(tmp.Path, "Documents-1"));
        second.Should().NotBe(first);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReserveUniquePrefix_UsesParenthesesNotDashes()
    {
        // A DIFFERENT suffix format from the path helpers — " (2)", not "-1". This is why
        // ReserveUniquePrefix cannot be folded in with them: doing so would change archive
        // naming, which is user-visible.
        var used = new HashSet<string>();

        TransferService.ReserveUniquePrefix("Documents", used).Should().Be("Documents");
        TransferService.ReserveUniquePrefix("Documents", used).Should().Be("Documents (2)");
        TransferService.ReserveUniquePrefix("Documents", used).Should().Be("Documents (3)");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReserveUniquePrefix_EmptyName_FallsBackToRoot()
    {
        // A drive root trimmed of its separator has no file name at all.
        var used = new HashSet<string>();

        TransferService.ReserveUniquePrefix("", used).Should().Be("root");
    }
}
