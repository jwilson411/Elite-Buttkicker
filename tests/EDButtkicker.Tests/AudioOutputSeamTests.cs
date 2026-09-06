using EDButtkicker.Configuration;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// A saved audio device that cannot be opened must not take the app down: the engine falls back to
/// the default output instead. Both the enumeration and the opening go through seams here, so this
/// runs on a machine (and a CI agent) with no WASAPI and no output device at all.
/// </summary>
public class AudioOutputSeamTests
{
    [Fact]
    public void AnEndpointThatWillNotOpen_FallsBackToTheDefaultOutput()
    {
        var settings = new AppSettings();
        settings.Audio.AudioDeviceEndpointId = FakeAudioDeviceCatalog.EndpointIdFor(0);
        settings.Audio.AudioDeviceName = "Buttkicker";

        var factory = new FakeAudioOutputFactory();
        var engine = new AudioEngineService(
            NullLogger<AudioEngineService>.Instance,
            settings,
            FakeAudioDeviceCatalog.With("Buttkicker"),
            factory);

        // The saved endpoint throws on open; initialization still has to complete.
        engine.Initialize();

        Assert.True(factory.OpenEndpointCalls >= 1, "the saved endpoint should have been attempted");
        Assert.True(factory.OpenDefaultCalls >= 1, "the failed endpoint should have fallen back to the default");
    }

    /// <summary>The endpoint the settings name is gone; the default output is all that is left.</summary>
    private sealed class FakeAudioOutputFactory : IAudioOutputFactory
    {
        private int _openEndpointCalls;
        private int _openDefaultCalls;

        public int OpenEndpointCalls => Volatile.Read(ref _openEndpointCalls);

        public int OpenDefaultCalls => Volatile.Read(ref _openDefaultCalls);

        public AudioOutputHandle OpenEndpoint(string endpointId)
        {
            Interlocked.Increment(ref _openEndpointCalls);
            throw new InvalidOperationException($"endpoint {endpointId} is not available");
        }

        public AudioOutputHandle OpenDefault()
        {
            Interlocked.Increment(ref _openDefaultCalls);
            return new AudioOutputHandle(new FakeWavePlayer(), "FakeDefault", null, "Fake Default Device");
        }
    }

    /// <summary>An output that accepts everything and plays nothing.</summary>
    private sealed class FakeWavePlayer : IWavePlayer
    {
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;

        public float Volume { get; set; } = 1f;

        public PlaybackState PlaybackState => PlaybackState.Stopped;

        public WaveFormat? OutputWaveFormat { get; private set; }

        public void Init(IWaveProvider waveProvider) => OutputWaveFormat = waveProvider.WaveFormat;

        public void Play()
        {
        }

        public void Pause()
        {
        }

        public void Stop() => PlaybackStopped?.Invoke(this, new StoppedEventArgs());

        public void Dispose()
        {
        }
    }
}
