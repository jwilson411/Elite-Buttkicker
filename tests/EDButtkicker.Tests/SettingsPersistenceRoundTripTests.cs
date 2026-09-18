using System.Text.Json;
using EDButtkicker.Configuration;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// A settings change is only worth anything if it is still there after a restart, and only honest if
/// the answer to "is this live yet?" is the truth. These tests drive the one service every settings
/// route goes through: a good change has to reach disk and come back on a fresh read, a format change
/// has to admit it needs a restart, and a rejected value must leave both the running configuration
/// and the saved one exactly as they were - including the last known good copy.
///
/// Everything runs against a temporary directory and never opens an audio device: the audio engine is
/// constructed but no device-change path is exercised, so nothing here depends on WASAPI.
/// </summary>
public class SettingsPersistenceRoundTripTests : IDisposable
{
    private readonly TempDirectory _dir = new("edbk-settings-persistence");
    private readonly AppSettings _settings = new();
    private readonly UserSettingsService _userSettings;
    private readonly SettingsPersistenceService _persistence;

    public SettingsPersistenceRoundTripTests()
    {
        _userSettings = new UserSettingsService(NullLogger<UserSettingsService>.Instance, _dir.Path);

        _persistence = new SettingsPersistenceService(
            NullLogger<SettingsPersistenceService>.Instance,
            _settings,
            _userSettings,
            new AudioEngineService(NullLogger<AudioEngineService>.Instance, _settings),
            new JournalMonitorStatus(TimeProvider.System));
    }

    /// <summary>A second UserSettingsService on the same directory: what the next start would read.</summary>
    private UserSettingsService AfterRestart() =>
        new(NullLogger<UserSettingsService>.Instance, _dir.Path);

    private string SettingsFile => _userSettings.GetUserSettingsPath();

    private string BackupFile => _userSettings.GetUserSettingsBackupPath();

    [Fact]
    public async Task IntensityAndFrequency_SurviveARestart()
    {
        var result = await _persistence.ApplyAsync(new SettingsUpdate
        {
            MaxIntensity = 65,
            DefaultFrequency = 42
        });

        Assert.True(result.Valid);
        Assert.True(result.Saved);
        Assert.False(result.RestartRequired);
        Assert.Equal("immediately", result.AppliedState);

        // Live in this session...
        Assert.Equal(65, _settings.Audio.MaxIntensity);
        Assert.Equal(42, _settings.Audio.DefaultFrequency);

        // ...and still there for the next one.
        Assert.True(File.Exists(SettingsFile));
        var reloaded = await AfterRestart().LoadUserPreferencesAsync();

        Assert.Equal(65, reloaded.MaxIntensity);
        Assert.Equal(42, reloaded.DefaultFrequency);

        var restored = new AppSettings();
        AfterRestart().ApplyUserPreferencesToAppSettings(reloaded, restored);

        Assert.Equal(65, restored.Audio.MaxIntensity);
        Assert.Equal(42, restored.Audio.DefaultFrequency);
    }

    [Fact]
    public async Task OutputFormat_IsSavedButNotLiveUntilARestart()
    {
        var result = await _persistence.ApplyAsync(new SettingsUpdate
        {
            SampleRate = 48000,
            BufferSize = 2048
        });

        Assert.True(result.Valid);
        Assert.True(result.Saved);
        Assert.False(result.AppliedNowFor("audio.sampleRate"));
        Assert.False(result.AppliedNowFor("audio.bufferSize"));
        Assert.True(result.RestartRequired);
        Assert.Equal("after_restart", result.AppliedState);
        Assert.Equal(
            new[] { "audio.sampleRate", "audio.bufferSize" },
            result.RestartRequiredSettings.ToArray());

        // The point of saying "after a restart" is that the restart actually brings it back.
        var reloaded = await AfterRestart().LoadUserPreferencesAsync();
        Assert.Equal(48000, reloaded.SampleRate);
        Assert.Equal(2048, reloaded.BufferSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task RejectedIntensity_ChangesNeitherTheSessionNorTheFile(int intensity)
    {
        var before = _settings.Audio.MaxIntensity;

        var result = await _persistence.ApplyAsync(new SettingsUpdate { MaxIntensity = intensity });

        Assert.False(result.Valid);
        Assert.False(result.Saved);
        Assert.Empty(result.Changes);
        Assert.NotEmpty(result.ValidationErrors);

        Assert.Equal(before, _settings.Audio.MaxIntensity);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task RejectedIntensity_LeavesTheSavedSettingsAndTheBackupIntact()
    {
        // Two good saves, so there is both a settings file and a last known good copy to protect.
        await _persistence.ApplyAsync(new SettingsUpdate { MaxIntensity = 65, DefaultFrequency = 42 });
        await _persistence.ApplyAsync(new SettingsUpdate { MaxIntensity = 30 });

        Assert.True(File.Exists(SettingsFile));
        Assert.True(File.Exists(BackupFile));

        var settingsBefore = await File.ReadAllTextAsync(SettingsFile);
        var backupBefore = await File.ReadAllTextAsync(BackupFile);

        var result = await _persistence.ApplyAsync(new SettingsUpdate { MaxIntensity = 101 });

        Assert.False(result.Valid);
        Assert.False(result.Saved);

        // Neither the running configuration nor either file moved.
        Assert.Equal(30, _settings.Audio.MaxIntensity);
        Assert.Equal(settingsBefore, await File.ReadAllTextAsync(SettingsFile));
        Assert.Equal(backupBefore, await File.ReadAllTextAsync(BackupFile));

        var reloaded = await AfterRestart().LoadUserPreferencesAsync();
        Assert.Equal(30, reloaded.MaxIntensity);
    }

    [Fact]
    public async Task RejectedFrequency_ChangesNothing()
    {
        await _persistence.ApplyAsync(new SettingsUpdate { DefaultFrequency = 42 });

        var result = await _persistence.ApplyAsync(new SettingsUpdate
        {
            DefaultFrequency = 5,
            MaxIntensity = 65
        });

        Assert.False(result.Valid);

        // A rejected field does not let a valid one through beside it: nothing was applied.
        Assert.Equal(42, _settings.Audio.DefaultFrequency);
        Assert.Equal(new AppSettings().Audio.MaxIntensity, _settings.Audio.MaxIntensity);

        var reloaded = await AfterRestart().LoadUserPreferencesAsync();
        Assert.Equal(42, reloaded.DefaultFrequency);
    }

    [Fact]
    public async Task SecondSave_KeepsThePreviousValuesAsTheLastKnownGoodCopy()
    {
        await _persistence.ApplyAsync(new SettingsUpdate { MaxIntensity = 65, DefaultFrequency = 42 });
        await _persistence.ApplyAsync(new SettingsUpdate { MaxIntensity = 30 });

        Assert.True(File.Exists(BackupFile));

        var backup = JsonSerializer.Deserialize<UserPreferences>(
            await File.ReadAllTextAsync(BackupFile),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.NotNull(backup);
        Assert.Equal(65, backup!.MaxIntensity);
        Assert.Equal(42, backup.DefaultFrequency);
    }

    [Fact]
    public async Task CorruptSettingsFile_FallsBackToTheLastKnownGoodCopy()
    {
        await _persistence.ApplyAsync(new SettingsUpdate { MaxIntensity = 65, DefaultFrequency = 42 });
        await _persistence.ApplyAsync(new SettingsUpdate { MaxIntensity = 30 });

        // Whatever damaged it - a crash, a full disk, a hand-edit - the working copy is still there.
        await File.WriteAllTextAsync(SettingsFile, "{ not valid json");

        var reloaded = await AfterRestart().LoadUserPreferencesAsync();

        Assert.Equal(65, reloaded.MaxIntensity);
        Assert.Equal(42, reloaded.DefaultFrequency);
    }

    [Fact]
    public async Task VoiceVolumeAndRate_SurviveARestart()
    {
        var result = await _persistence.ApplyAsync(new SettingsUpdate
        {
            VoiceVolume = 60,
            VoiceRate = 3
        });

        Assert.True(result.Valid);
        Assert.True(result.Saved);

        // Live in this session...
        Assert.Equal(60, _settings.Voice.Volume);
        Assert.Equal(3, _settings.Voice.Rate);

        // ...and still there for the next one.
        Assert.True(File.Exists(SettingsFile));
        var reloaded = await AfterRestart().LoadUserPreferencesAsync();

        Assert.Equal(60, reloaded.VoiceVolume);
        Assert.Equal(3, reloaded.VoiceRate);

        var restored = new AppSettings();
        AfterRestart().ApplyUserPreferencesToAppSettings(reloaded, restored);

        Assert.Equal(60, restored.Voice.Volume);
        Assert.Equal(3, restored.Voice.Rate);
    }

    [Fact]
    public async Task ContextualIntelligenceSettings_SurviveARestart()
    {
        var result = await _persistence.ApplyAsync(new SettingsUpdate
        {
            ContextualIntelligenceEnabled = true,
            LearningRate = 0.3,
            PredictionThreshold = 0.85
        });

        Assert.True(result.Valid);
        Assert.True(result.Saved);

        // Live in this session...
        Assert.NotNull(_settings.ContextualIntelligence);
        Assert.True(_settings.ContextualIntelligence!.Enabled);
        Assert.Equal(0.3, _settings.ContextualIntelligence.LearningRate);
        Assert.Equal(0.85, _settings.ContextualIntelligence.PredictionThreshold);

        // ...and still there for the next one.
        Assert.True(File.Exists(SettingsFile));
        var reloaded = await AfterRestart().LoadUserPreferencesAsync();

        Assert.NotNull(reloaded.ContextualIntelligence);
        Assert.True(reloaded.ContextualIntelligence!.Enabled);
        Assert.Equal(0.3, reloaded.ContextualIntelligence.LearningRate);
        Assert.Equal(0.85, reloaded.ContextualIntelligence.PredictionThreshold);

        var restored = new AppSettings();
        AfterRestart().ApplyUserPreferencesToAppSettings(reloaded, restored);

        Assert.NotNull(restored.ContextualIntelligence);
        Assert.True(restored.ContextualIntelligence!.Enabled);
        Assert.Equal(0.3, restored.ContextualIntelligence.LearningRate);
        Assert.Equal(0.85, restored.ContextualIntelligence.PredictionThreshold);
    }

    [Theory]
    [InlineData(0.005)]
    [InlineData(1.5)]
    public async Task ContextualIntelligence_OutOfRangeLearningRate_IsRejected(double learningRate)
    {
        double before = new AppSettings().ContextualIntelligence!.LearningRate;

        var result = await _persistence.ApplyAsync(new SettingsUpdate { LearningRate = learningRate });

        Assert.False(result.Valid);
        Assert.False(result.Saved);
        Assert.Empty(result.Changes);
        Assert.NotEmpty(result.ValidationErrors);

        Assert.Equal(before, _settings.ContextualIntelligence!.LearningRate);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task NoChange_WritesNothingAndSaysSo()
    {
        var result = await _persistence.ApplyAsync(new SettingsUpdate
        {
            MaxIntensity = _settings.Audio.MaxIntensity
        });

        Assert.True(result.Valid);
        Assert.Empty(result.Changes);
        Assert.Equal("no_changes", result.AppliedState);
        Assert.False(File.Exists(SettingsFile));
    }

    /// <summary>
    /// The way out of a bad configuration has to actually take it away: settings the user saved are
    /// gone from the running session and from the file, so the next start comes up on defaults too.
    /// </summary>
    [Fact]
    public async Task ResetToDefaultsAsync_ClearsPersistedSettings()
    {
        var defaults = new AppSettings();

        await _persistence.ApplyAsync(new SettingsUpdate { MaxIntensity = 99, DefaultFrequency = 25 });

        Assert.True(_userSettings.UserSettingsExist());
        Assert.Equal(99, _settings.Audio.MaxIntensity);
        Assert.Equal(25, _settings.Audio.DefaultFrequency);

        var result = await _persistence.ResetToDefaultsAsync();

        Assert.True(result.Valid);
        Assert.True(result.Saved);

        // Gone from this session...
        Assert.Equal(defaults.Audio.MaxIntensity, _settings.Audio.MaxIntensity);
        Assert.Equal(defaults.Audio.DefaultFrequency, _settings.Audio.DefaultFrequency);

        // ...and from what the next start would read, so the 99 does not come back.
        var reloaded = await AfterRestart().LoadUserPreferencesAsync();

        Assert.Equal(defaults.Audio.MaxIntensity, reloaded.MaxIntensity);
        Assert.Equal(defaults.Audio.DefaultFrequency, reloaded.DefaultFrequency);
    }

    public void Dispose() => _dir.Dispose();
}

internal static class SettingsUpdateResultAssertions
{
    /// <summary>Whether the one change named <paramref name="setting"/> reports itself as live.</summary>
    public static bool AppliedNowFor(this SettingsUpdateResult result, string setting) =>
        result.Changes.Single(c => c.Setting == setting).AppliedNow;
}
