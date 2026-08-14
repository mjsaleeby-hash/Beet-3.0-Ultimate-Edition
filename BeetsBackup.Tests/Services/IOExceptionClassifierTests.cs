using System.IO;
using BeetsBackup.Services;
using FluentAssertions;

namespace BeetsBackup.Tests.Services;

public class IOExceptionClassifierTests
{
    /// <summary>
    /// IOException exposes HResult only through a constructor overload, so every case builds
    /// the exception it wants rather than provoking a real one from the filesystem.
    /// </summary>
    private static IOException WithHResult(int hresult) => new("synthetic", hresult);

    [Fact]
    [Trait("Category", "Unit")]
    public void IsSharingViolation_Win32SharingViolation_IsTrue()
    {
        IOExceptionClassifier.IsSharingViolation(WithHResult(unchecked((int)0x80070020)))
            .Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsDiskFull_DoesNotClaimASharingViolation()
    {
        // The two codes must not overlap: a locked file is retryable via VSS, a full disk is
        // not, and confusing them would send a doomed copy down the snapshot path.
        IOExceptionClassifier.IsDiskFull(WithHResult(unchecked((int)0x80070020)))
            .Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsDiskFull_Win32DiskFull_IsTrue()
    {
        IOExceptionClassifier.IsDiskFull(WithHResult(unchecked((int)0x80070070)))
            .Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsSharingViolation_DoesNotClaimADiskFull()
    {
        IOExceptionClassifier.IsSharingViolation(WithHResult(unchecked((int)0x80070070)))
            .Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsSharingViolation_BareWin32Code_StillClassifies()
    {
        // The 0xFFFF mask is the whole point: a Win32 error can arrive either as a bare code
        // or wrapped in an 0x8007xxxx HRESULT, and both mean the same thing.
        IOExceptionClassifier.IsSharingViolation(WithHResult(0x0020)).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsSharingViolation_AdjacentCode_IsFalse()
    {
        // Guards an off-by-one in the constant. 0x21 is ERROR_LOCK_VIOLATION — a different
        // condition that must not be swept into the VSS retry path.
        IOExceptionClassifier.IsSharingViolation(WithHResult(unchecked((int)0x80070021)))
            .Should().BeFalse();
    }
}
