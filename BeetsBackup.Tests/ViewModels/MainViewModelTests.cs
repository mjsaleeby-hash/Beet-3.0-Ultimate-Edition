using BeetsBackup.Models;
using BeetsBackup.Services;
using BeetsBackup.Tests.Infrastructure;
using BeetsBackup.ViewModels;
using FluentAssertions;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Media;

namespace BeetsBackup.Tests.ViewModels;

public class MainViewModelTests
{
    private static MainViewModel CreateViewModel()
    {
        var settings = new SettingsService();
        var theme = new ThemeService(settings);
        var fs = new FileSystemService();
        var transfer = new TransferService(fs);
        var log = new BackupLogService();
        var scheduler = new SchedulerService(transfer, log);
        var update = new UpdateService(settings);
        return new MainViewModel(theme, fs, transfer, scheduler, log, settings, update);
    }

    private static FileSystemItem MakeFileItem(string dir, string name, long size)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[size]);
        return new FileSystemItem(new FileInfo(path));
    }

    private static FileSystemItem MakeDirItem(string parentDir, string name, long calculatedSize)
    {
        var path = Path.Combine(parentDir, name);
        Directory.CreateDirectory(path);
        var item = new FileSystemItem(new DirectoryInfo(path));
        // Use reflection to set _size (normally set by CalculateDirectorySizeAsync)
        var field = typeof(FileSystemItem).GetField("_size", BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(item, calculatedSize);
        return item;
    }

    // ============================================================
    //  FORMAT TRANSFER RESULT
    // ============================================================

    [Fact]
    [Trait("Category", "Unit")]
    public void FormatTransferResult_AllZero_ReturnsDone()
    {
        var result = new TransferResult();
        MainViewModel.FormatTransferResult(result).Should().Be("Done.");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FormatTransferResult_FilesCopied_IncludesCopiedCount()
    {
        var result = new TransferResult { FilesCopied = 3 };
        MainViewModel.FormatTransferResult(result).Should().Contain("3 copied");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FormatTransferResult_ChecksumMismatches_IncludesExclamation()
    {
        var result = new TransferResult { ChecksumMismatches = 2 };
        MainViewModel.FormatTransferResult(result).Should().Contain("2 checksum mismatches!");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FormatTransferResult_MultipleCounters_AllIncluded()
    {
        var result = new TransferResult
        {
            FilesCopied = 5,
            FilesSkipped = 2,
            FilesFailed = 1,
            DirectoriesFailed = 1,
            FilesLocked = 1,
            DiskFullErrors = 1,
            ChecksumMismatches = 1
        };
        var output = MainViewModel.FormatTransferResult(result);

        output.Should().Contain("5 copied");
        output.Should().Contain("2 skipped");
        output.Should().Contain("1 failed");
        output.Should().Contain("1 folders failed");
        output.Should().Contain("1 locked");
        output.Should().Contain("1 disk full errors");
        output.Should().Contain("1 checksum mismatches!");
    }

    // ============================================================
    //  BUILD PIE SLICES
    // ============================================================

    [Fact]
    [Trait("Category", "Integration")]
    public void BuildPieSlices_EmptyItems_ProducesNoSlices()
    {
        var vm = CreateViewModel();
        var items = new ObservableCollection<FileSystemItem>();

        vm.BuildPieSlices(items, true);

        vm.TopPieSlices.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void BuildPieSlices_SingleItem_Gets360Degrees()
    {
        using var tmp = new TempDirectory();
        var vm = CreateViewModel();
        var items = new ObservableCollection<FileSystemItem>
        {
            MakeFileItem(tmp.Path, "only.bin", 1000)
        };

        vm.BuildPieSlices(items, true);

        var slices = vm.TopPieSlices;
        slices.Should().HaveCount(1);
        slices[0].SweepAngle.Should().BeApproximately(360.0, 0.01);
        slices[0].Percentage.Should().BeApproximately(100.0, 0.01);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void BuildPieSlices_Exactly10Items_NoOtherSlice()
    {
        using var tmp = new TempDirectory();
        var vm = CreateViewModel();
        var items = new ObservableCollection<FileSystemItem>();
        for (int i = 1; i <= 10; i++)
            items.Add(MakeFileItem(tmp.Path, $"file{i}.bin", i * 100));

        vm.BuildPieSlices(items, true);

        vm.TopPieSlices.Should().HaveCount(10);
        vm.TopPieSlices.Should().NotContain(s => s.Name == "Other");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void BuildPieSlices_MoreThan10Items_HasOtherSlice()
    {
        using var tmp = new TempDirectory();
        var vm = CreateViewModel();
        var items = new ObservableCollection<FileSystemItem>();
        for (int i = 1; i <= 15; i++)
            items.Add(MakeFileItem(tmp.Path, $"file{i}.bin", i * 100));

        vm.BuildPieSlices(items, true);

        var slices = vm.TopPieSlices;
        // 10 top slices + 1 "Other"
        slices.Should().HaveCount(11);
        slices.Last().Name.Should().Be("Other");
        slices.Last().SizeBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void BuildPieSlices_MixedFilesAndDirs_BothIncluded()
    {
        using var tmp = new TempDirectory();
        var vm = CreateViewModel();
        var items = new ObservableCollection<FileSystemItem>
        {
            MakeDirItem(tmp.Path, "Documents", 5000),
            MakeFileItem(tmp.Path, "photo.jpg", 3000),
            MakeDirItem(tmp.Path, "Music", 2000),
        };

        vm.BuildPieSlices(items, true);

        var slices = vm.TopPieSlices;
        slices.Should().HaveCount(3);
        slices.Select(s => s.Name).Should().Contain("Documents");
        slices.Select(s => s.Name).Should().Contain("photo.jpg");
        slices.Select(s => s.Name).Should().Contain("Music");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void BuildPieSlices_AnglesContinuous_SumTo360()
    {
        using var tmp = new TempDirectory();
        var vm = CreateViewModel();
        var items = new ObservableCollection<FileSystemItem>
        {
            MakeFileItem(tmp.Path, "a.bin", 500),
            MakeFileItem(tmp.Path, "b.bin", 300),
            MakeFileItem(tmp.Path, "c.bin", 200),
        };

        vm.BuildPieSlices(items, true);

        var slices = vm.TopPieSlices;
        double totalSweep = slices.Sum(s => s.SweepAngle);
        totalSweep.Should().BeApproximately(360.0, 0.01);

        // Each slice starts where the previous ended
        for (int i = 1; i < slices.Count; i++)
        {
            slices[i].StartAngle.Should().BeApproximately(
                slices[i - 1].StartAngle + slices[i - 1].SweepAngle, 0.01);
        }
    }
}
