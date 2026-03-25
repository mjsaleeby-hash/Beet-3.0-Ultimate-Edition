using BeetsBackup.Services;
using FluentAssertions;
using System.Text.Json;

namespace BeetsBackup.Tests.Services;

/// <summary>
/// SettingsService uses a hardcoded path, so we test the data model
/// serialization and default behavior. Full file I/O integration tests
/// require path injection (deferred to interface extraction phase).
/// </summary>
public class SettingsServiceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void SettingsData_DefaultIsDarkMode_True()
    {
        var data = new SettingsData();
        data.IsDarkMode.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SettingsData_Serialization_RoundTrip()
    {
        var data = new SettingsData { IsDarkMode = false };

        var json = JsonSerializer.Serialize(data);
        var loaded = JsonSerializer.Deserialize<SettingsData>(json);

        loaded.Should().NotBeNull();
        loaded!.IsDarkMode.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SettingsData_CorruptJson_ThrowsJsonException()
    {
        var act = () => JsonSerializer.Deserialize<SettingsData>("garbage");
        act.Should().Throw<JsonException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SettingsData_EmptyJson_DeserializesToPropertyDefault()
    {
        var loaded = JsonSerializer.Deserialize<SettingsData>("{}");
        loaded.Should().NotBeNull();
        // Empty JSON uses the property initializer default (IsDarkMode = true)
        loaded!.IsDarkMode.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SettingsService_NewInstance_DefaultsAreDarkMode()
    {
        var service = new SettingsService();
        service.IsDarkMode.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SettingsService_SetIsDarkMode_UpdatesData()
    {
        var service = new SettingsService();
        service.IsDarkMode = false;

        service.Data.IsDarkMode.Should().BeFalse();
        service.IsDarkMode.Should().BeFalse();
    }
}
