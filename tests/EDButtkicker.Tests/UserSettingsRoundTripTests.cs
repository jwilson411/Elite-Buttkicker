using EDButtkicker.Configuration;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Saved settings must survive a restart: what is written has to come back identical, a missing or
/// damaged file has to fall back to defaults rather than throw, and preferences have to map onto
/// AppSettings both ways. Everything runs against a temporary directory, never the real profile.
/// </summary>
public class UserSettingsRoundTripTests : IDisposable
{
    private readonly TempDirectory _dir = new("edbk-settings");
    private readonly UserSettingsService _service;

    public UserSettingsRoundTripTests()
    {
        _service = new UserSettingsService(NullLogger<UserSettingsService>.Instance, _dir.Path);
    }

    [Fact]
    public void SettingsPath_IsInsideTheGivenDirectory()
    {
        Assert.Equal(Path.Combine(_dir.Path, "user-settings.json"), _service.GetUserSettingsPath());
        Assert.False(_service.UserSettingsExist());
    }

    [Fact]
    public async Task Preferences_RoundTripThroughDisk()
    {
        var saved = new UserPreferences
        {
            AudioDeviceId = 3,
            AudioDeviceName = "ButtKicker Gamer Pro",
            MaxIntensity = 65,
            DefaultFrequency = 42,
            JournalPath = _dir.File("journals"),
            MonitorLatestOnly = false,
            ContextualIntelligence = new UserContextualIntelligencePreferences
            {
                Enabled = true,
                EnableAdaptiveIntensity = false,
                EnablePredictivePatterns = true,
                EnableContextualVoice = false
            },
            LastSaved = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc),
            Version = "1.0.0"
        };

        await _service.SaveUserPreferencesAsync(saved);
        Assert.True(_service.UserSettingsExist());

        var loaded = await _service.LoadUserPreferencesAsync();

        Assert.Equal(saved.AudioDeviceId, loaded.AudioDeviceId);
        Assert.Equal(saved.AudioDeviceName, loaded.AudioDeviceName);
        Assert.Equal(saved.MaxIntensity, loaded.MaxIntensity);
        Assert.Equal(saved.DefaultFrequency, loaded.DefaultFrequency);
        Assert.Equal(saved.JournalPath, loaded.JournalPath);
        Assert.Equal(saved.MonitorLatestOnly, loaded.MonitorLatestOnly);
        Assert.Equal(saved.Version, loaded.Version);
        Assert.Equal(saved.LastSaved, loaded.LastSaved);

        Assert.NotNull(loaded.ContextualIntelligence);
        Assert.True(loaded.ContextualIntelligence!.Enabled);
        Assert.False(loaded.ContextualIntelligence.EnableAdaptiveIntensity);
        Assert.True(loaded.ContextualIntelligence.EnablePredictivePatterns);
        Assert.False(loaded.ContextualIntelligence.EnableContextualVoice);
    }

    [Fact]
    public async Task Preferences_AreRewrittenOnEverySave()
    {
        await _service.SaveUserPreferencesAsync(new UserPreferences { MaxIntensity = 90 });
        await _service.SaveUserPreferencesAsync(new UserPreferences { MaxIntensity = 30 });

        var loaded = await _service.LoadUserPreferencesAsync();

        Assert.Equal(30, loaded.MaxIntensity);
    }

    [Fact]
    public async Task MissingFile_YieldsDefaults()
    {
        var loaded = await _service.LoadUserPreferencesAsync();

        Assert.Null(loaded.AudioDeviceId);
        Assert.Null(loaded.MaxIntensity);
        Assert.Null(loaded.JournalPath);
    }

    [Fact]
    public async Task CorruptFile_YieldsDefaultsInsteadOfThrowing()
    {
        await File.WriteAllTextAsync(_service.GetUserSettingsPath(), "{ not valid json");

        var loaded = await _service.LoadUserPreferencesAsync();

        Assert.Null(loaded.AudioDeviceId);
        Assert.Null(loaded.AudioDeviceName);
    }

    [Fact]
    public async Task FileContainingJsonNull_YieldsDefaults()
    {
        await File.WriteAllTextAsync(_service.GetUserSettingsPath(), "null");

        var loaded = await _service.LoadUserPreferencesAsync();

        Assert.Null(loaded.MaxIntensity);
    }

    [Fact]
    public async Task AppSettings_RoundTripThroughPreferences()
    {
        var original = new AppSettings
        {
            Audio =
            {
                AudioDeviceId = 7,
                AudioDeviceName = "Test Device",
                MaxIntensity = 55,
                DefaultFrequency = 38
            },
            EliteDangerous =
            {
                JournalPath = _dir.File("journals"),
                MonitorLatestOnly = false
            },
            ContextualIntelligence = new ContextualIntelligenceConfiguration
            {
                Enabled = true,
                EnableAdaptiveIntensity = false,
                EnablePredictivePatterns = false,
                EnableContextualVoice = true
            }
        };

        await _service.SaveUserPreferencesAsync(_service.CreatePreferencesFromAppSettings(original));

        var restored = new AppSettings();
        _service.ApplyUserPreferencesToAppSettings(await _service.LoadUserPreferencesAsync(), restored);

        Assert.Equal(original.Audio.AudioDeviceId, restored.Audio.AudioDeviceId);
        Assert.Equal(original.Audio.AudioDeviceName, restored.Audio.AudioDeviceName);
        Assert.Equal(original.Audio.MaxIntensity, restored.Audio.MaxIntensity);
        Assert.Equal(original.Audio.DefaultFrequency, restored.Audio.DefaultFrequency);
        Assert.Equal(original.EliteDangerous.JournalPath, restored.EliteDangerous.JournalPath);
        Assert.Equal(original.EliteDangerous.MonitorLatestOnly, restored.EliteDangerous.MonitorLatestOnly);
        Assert.True(restored.ContextualIntelligence!.Enabled);
        Assert.False(restored.ContextualIntelligence.EnableAdaptiveIntensity);
        Assert.False(restored.ContextualIntelligence.EnablePredictivePatterns);
        Assert.True(restored.ContextualIntelligence.EnableContextualVoice);
    }

    [Fact]
    public void UnsetPreferences_LeaveAppSettingsUntouched()
    {
        var settings = new AppSettings();
        var defaults = new AppSettings();

        _service.ApplyUserPreferencesToAppSettings(new UserPreferences(), settings);

        Assert.Equal(defaults.Audio.MaxIntensity, settings.Audio.MaxIntensity);
        Assert.Equal(defaults.Audio.DefaultFrequency, settings.Audio.DefaultFrequency);
        Assert.Equal(defaults.Audio.AudioDeviceId, settings.Audio.AudioDeviceId);
        Assert.Equal(defaults.EliteDangerous.JournalPath, settings.EliteDangerous.JournalPath);
        Assert.Equal(defaults.EliteDangerous.MonitorLatestOnly, settings.EliteDangerous.MonitorLatestOnly);
    }

    [Fact]
    public async Task GameContext_RoundTripsThroughDisk()
    {
        var snapshot = new GameContextSnapshot
        {
            PlayerAggressiveness = 0.62,
            PlayerCautiousness = 0.31,
            SystemsVisited = 17,
            BodiesScanned = 42,
            LastHullIntegrity = 0.75,
            LastKnownSystem = "Shinrarta Dezhra",
            RecentEventFrequency = { ["HullDamage"] = 4, ["FSDJump"] = 9 },
            StateTimeSpent = { ["Combat"] = TimeSpan.FromMinutes(3) },
            SavedAt = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc)
        };

        await _service.SaveGameContextAsync(snapshot);
        var loaded = await _service.LoadGameContextAsync();

        Assert.NotNull(loaded);
        Assert.Equal(snapshot.PlayerAggressiveness, loaded!.PlayerAggressiveness, 6);
        Assert.Equal(snapshot.PlayerCautiousness, loaded.PlayerCautiousness, 6);
        Assert.Equal(snapshot.SystemsVisited, loaded.SystemsVisited);
        Assert.Equal(snapshot.BodiesScanned, loaded.BodiesScanned);
        Assert.Equal(snapshot.LastHullIntegrity, loaded.LastHullIntegrity, 6);
        Assert.Equal(snapshot.LastKnownSystem, loaded.LastKnownSystem);
        Assert.Equal(4, loaded.RecentEventFrequency["HullDamage"]);
        Assert.Equal(TimeSpan.FromMinutes(3), loaded.StateTimeSpent["Combat"]);
        Assert.Equal(snapshot.SavedAt, loaded.SavedAt);
    }

    [Fact]
    public async Task MissingGameContext_LoadsAsNull()
    {
        Assert.Null(await _service.LoadGameContextAsync());
    }

    [Fact]
    public async Task CorruptGameContext_LoadsAsNullInsteadOfThrowing()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir.Path, "game-context.json"), "}{");

        Assert.Null(await _service.LoadGameContextAsync());
    }

    [Fact]
    public void SettingsDirectory_IsCreatedIfMissing()
    {
        var nested = Path.Combine(_dir.Path, "nested", "settings");

        var service = new UserSettingsService(NullLogger<UserSettingsService>.Instance, nested);

        Assert.True(Directory.Exists(nested));
        Assert.Equal(Path.Combine(nested, "user-settings.json"), service.GetUserSettingsPath());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptySettingsDirectory_IsRejected(string directory)
    {
        Assert.Throws<ArgumentException>(() =>
            new UserSettingsService(NullLogger<UserSettingsService>.Instance, directory));
    }

    public void Dispose() => _dir.Dispose();
}
