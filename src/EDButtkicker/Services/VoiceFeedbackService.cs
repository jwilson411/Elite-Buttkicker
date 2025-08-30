using Microsoft.Extensions.Logging;
using System.Speech.Synthesis;
using NAudio.Wave;
using EDButtkicker.Models;
using EDButtkicker.Configuration;
using System.Runtime.Versioning;

namespace EDButtkicker.Services;

[SupportedOSPlatform("windows")]
public class VoiceFeedbackService : IDisposable
{
    private readonly ILogger<VoiceFeedbackService> _logger;
    private readonly AppSettings _settings;
    private readonly SpeechSynthesizer _synthesizer;
    private readonly Dictionary<string, string> _eventMessages = new();
    private readonly Dictionary<string, DateTime> _lastAnnouncementTimes = new();
    private bool _isInitialized = false;

    public VoiceFeedbackService(ILogger<VoiceFeedbackService> logger, AppSettings settings)
    {
        _logger = logger;
        _settings = settings;
        _synthesizer = new SpeechSynthesizer();
        InitializeEventMessages();
    }

    public void Initialize()
    {
        try
        {
            _logger.LogInformation("Initializing Voice Feedback Service");

            // Configure synthesizer
            _synthesizer.Volume = 80; // 0-100
            _synthesizer.Rate = 0;    // -10 to 10 (normal speed)
            
            // Try to set a suitable voice
            var voices = _synthesizer.GetInstalledVoices();
            var preferredVoice = voices.FirstOrDefault(v => 
                v.VoiceInfo.Name.Contains("Microsoft") && 
                v.VoiceInfo.Culture.Name.StartsWith("en"));
            
            if (preferredVoice != null)
            {
                _synthesizer.SelectVoice(preferredVoice.VoiceInfo.Name);
                _logger.LogInformation("Selected voice: {VoiceName}", preferredVoice.VoiceInfo.Name);
            }
            else
            {
                _logger.LogInformation("Using default voice: {VoiceName}", _synthesizer.Voice.Name);
            }

            _isInitialized = true;
            _logger.LogInformation("Voice Feedback Service initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize voice feedback service");
        }
    }

    public async Task AnnounceEvent(string eventType, JournalEvent? journalEvent = null)
    {
        if (!_isInitialized) return;

        try
        {
            // Rate limiting for announcements
            if (ShouldRateLimit(eventType))
            {
                _logger.LogDebug("Rate limiting voice announcement for: {EventType}", eventType);
                return;
            }

            string message = GenerateEventMessage(eventType, journalEvent);
            if (string.IsNullOrEmpty(message)) return;

            _logger.LogDebug("Voice announcement: {Message}", message);
            
            // Announce asynchronously to avoid blocking
            await Task.Run(() =>
            {
                try
                {
                    _synthesizer.Speak(message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during speech synthesis");
                }
            });

            _lastAnnouncementTimes[eventType] = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error announcing event: {EventType}", eventType);
        }
    }

    public async Task PlayAudioCue(string cueFile)
    {
        if (string.IsNullOrEmpty(cueFile)) return;

        try
        {
            string cuePath = Path.Combine("audio_cues", cueFile);
            if (!File.Exists(cuePath))
            {
                _logger.LogWarning("Audio cue file not found: {CueFile}", cuePath);
                return;
            }

            // Play audio cue using NAudio
            await Task.Run(() =>
            {
                try
                {
                    using var audioFile = new AudioFileReader(cuePath);
                    using var outputDevice = new WaveOutEvent();
                    outputDevice.Init(audioFile);
                    outputDevice.Play();
                    
                    while (outputDevice.PlaybackState == PlaybackState.Playing)
                    {
                        Thread.Sleep(100);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error playing audio cue: {CueFile}", cueFile);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error playing audio cue: {CueFile}", cueFile);
        }
    }

    public async Task ProcessPatternVoiceFeedback(HapticPattern pattern, JournalEvent? journalEvent = null)
    {
        var tasks = new List<Task>();

        // Voice announcement
        if (pattern.EnableVoiceAnnouncement && !string.IsNullOrEmpty(pattern.VoiceMessage))
        {
            string message = ProcessMessageTemplate(pattern.VoiceMessage, journalEvent);
            tasks.Add(AnnounceCustomMessage(message));
        }

        // Audio cue
        if (pattern.EnableAudioCue && !string.IsNullOrEmpty(pattern.AudioCueFile))
        {
            tasks.Add(PlayAudioCue(pattern.AudioCueFile));
        }

        if (tasks.Any())
        {
            await Task.WhenAll(tasks);
        }
    }

    private async Task AnnounceCustomMessage(string message)
    {
        if (!_isInitialized || string.IsNullOrEmpty(message)) return;

        try
        {
            await Task.Run(() =>
            {
                try
                {
                    _synthesizer.Speak(message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during custom message synthesis");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error announcing custom message");
        }
    }

    private string GenerateEventMessage(string eventType, JournalEvent? journalEvent)
    {
        if (_eventMessages.TryGetValue(eventType, out string? baseMessage))
        {
            return ProcessMessageTemplate(baseMessage, journalEvent);
        }

        return eventType switch
        {
            "FSDJump" => "Hyperspace jump initiated",
            "Docked" => GetDockingMessage(journalEvent),
            "Undocked" => "Undocking complete",
            "HullDamage" => GetHullDamageMessage(journalEvent),
            "ShieldDown" => "Shields offline",
            "ShieldsUp" => "Shields online",
            "UnderAttack" => "Under attack!",
            "HeatWarning" => "Heat warning",
            "HeatDamage" => "Heat damage detected",
            "Interdicted" => "Interdiction detected",
            "JetConeBoost" => "Neutron boost acquired",
            "Touchdown" => GetLandingMessage(journalEvent),
            "Liftoff" => "Liftoff complete",
            _ => string.Empty
        };
    }

    private string ProcessMessageTemplate(string template, JournalEvent? journalEvent)
    {
        if (journalEvent == null) return template;

        string processed = template;
        processed = processed.Replace("{ship}", journalEvent.Ship ?? "ship");
        processed = processed.Replace("{health}", ((int)((journalEvent.Health ?? 1.0) * 100)).ToString());
        processed = processed.Replace("{station}", journalEvent.StationName ?? "station");
        processed = processed.Replace("{system}", journalEvent.StarSystem ?? "system");
        
        return processed;
    }

    private string GetDockingMessage(JournalEvent? journalEvent)
    {
        if (journalEvent?.StationName != null)
        {
            return $"Docked at {journalEvent.StationName}";
        }
        return "Docking complete";
    }

    private string GetHullDamageMessage(JournalEvent? journalEvent)
    {
        if (journalEvent?.Health.HasValue == true)
        {
            int healthPercent = (int)(journalEvent.Health.Value * 100);
            if (healthPercent < 25)
                return "Critical hull damage!";
            else if (healthPercent < 50)
                return "Significant hull damage";
            else
                return "Hull damage detected";
        }
        return "Hull damage detected";
    }

    private string GetLandingMessage(JournalEvent? journalEvent)
    {
        if (journalEvent?.AdditionalData?.ContainsKey("Body") == true)
        {
            string bodyName = journalEvent.AdditionalData["Body"]?.ToString() ?? "planetary surface";
            return $"Landed on {bodyName}";
        }
        return "Planetary landing complete";
    }

    private bool ShouldRateLimit(string eventType)
    {
        var rateLimits = new Dictionary<string, TimeSpan>
        {
            ["HullDamage"] = TimeSpan.FromSeconds(5),
            ["HeatWarning"] = TimeSpan.FromSeconds(10),
            ["HeatDamage"] = TimeSpan.FromSeconds(3),
            ["UnderAttack"] = TimeSpan.FromSeconds(2),
            ["ShieldDown"] = TimeSpan.FromSeconds(5),
            ["ShieldsUp"] = TimeSpan.FromSeconds(5)
        };

        if (!rateLimits.TryGetValue(eventType, out var minInterval))
            return false;

        if (!_lastAnnouncementTimes.TryGetValue(eventType, out var lastTime))
            return false;

        return DateTime.UtcNow - lastTime < minInterval;
    }

    private void InitializeEventMessages()
    {
        // Load custom messages from configuration if available
        // For now, using default messages
        _eventMessages["FSDJump"] = "Hyperspace jump initiated";
        _eventMessages["Docked"] = "Docking complete at {station}";
        _eventMessages["Undocked"] = "Undocking from {station}";
        _eventMessages["HullDamage"] = "Hull integrity at {health} percent";
        _eventMessages["ShieldDown"] = "Shields are offline";
        _eventMessages["ShieldsUp"] = "Shields are online";
        _eventMessages["UnderAttack"] = "Warning: Under attack!";
        _eventMessages["HeatWarning"] = "Heat levels critical";
        _eventMessages["HeatDamage"] = "Heat damage detected";
        _eventMessages["Interdicted"] = "Interdiction in progress";
        _eventMessages["JetConeBoost"] = "Frame shift drive supercharged";
        _eventMessages["Touchdown"] = "Touchdown confirmed";
        _eventMessages["Liftoff"] = "Liftoff complete";
    }

    public void Dispose()
    {
        try
        {
            _synthesizer?.Dispose();
            _logger.LogInformation("Voice Feedback Service disposed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing Voice Feedback Service");
        }
    }
}