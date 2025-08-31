using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.Wasapi;
using EDButtkicker.Configuration;
using EDButtkicker.Models;
using EDButtkicker.Services;

namespace EDButtkicker;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Elite Dangerous Buttkicker Extension");
        Console.WriteLine("====================================");
        Console.WriteLine();

        var hostBuilder = CreateHostBuilder(args);
        var host = hostBuilder.Build();

        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        var config = host.Services.GetRequiredService<IConfiguration>();
        var appSettings = host.Services.GetRequiredService<AppSettings>();

        logger.LogInformation("Starting Elite Dangerous Buttkicker Extension");

        try
        {
            // Show setup UI
            await ShowSetupUI(appSettings, logger);

            // Start services
            logger.LogInformation("Configuration complete. Starting services...");
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Application terminated unexpectedly");
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
    }

    static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                // Try to load embedded appsettings.json first
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using var resourceStream = assembly.GetManifestResourceStream("appsettings.json");
                bool embeddedConfigLoaded = false;
                
                if (resourceStream != null)
                {
                    try
                    {
                        // Read the embedded resource as string and parse as JSON
                        using var reader = new StreamReader(resourceStream);
                        var jsonContent = reader.ReadToEnd();
                        
                        // Parse JSON and convert to key-value pairs for in-memory configuration
                        var jsonDoc = System.Text.Json.JsonDocument.Parse(jsonContent);
                        var configDict = new Dictionary<string, string>();
                        FlattenJson(jsonDoc.RootElement, configDict, "");
                        
                        config.AddInMemoryCollection(configDict);
                        embeddedConfigLoaded = true;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to load embedded configuration: {ex.Message}");
                    }
                }
                
                if (!embeddedConfigLoaded)
                {
                    // Fallback to file-based configuration for development
                    config.SetBasePath(Directory.GetCurrentDirectory())
                          .AddJsonFile("config/appsettings.json", optional: true, reloadOnChange: true)
                          .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                }
                
                config.AddJsonFile("patterns/default-patterns.json", optional: true, reloadOnChange: true)
                      .AddEnvironmentVariables()
                      .AddCommandLine(args);
            })
            .ConfigureServices((hostContext, services) =>
            {
                // Bind configuration
                var appSettings = new AppSettings();
                hostContext.Configuration.Bind(appSettings);
                services.AddSingleton(appSettings);

                // Add core services
                services.AddSingleton<AudioEngineService>();
                services.AddSingleton<PatternSequencer>();
                services.AddSingleton<ContextualIntelligenceService>();
                services.AddSingleton<EventMappingService>();
                // IntensityCurveProcessor is a static class, no need to register
                // AdvancedWaveformGenerator and MultiLayerPatternGenerator are created as needed
                
                // Add hosted services
                services.AddHostedService<JournalMonitorService>();
                services.AddHostedService<WebConfigurationService>();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.AddDebug();
            });

    static async Task ShowSetupUI(AppSettings settings, ILogger logger)
    {
        Console.WriteLine("Initial Setup");
        Console.WriteLine("=============");
        Console.WriteLine();

        // Audio Device Selection
        await ConfigureAudioDevice(settings, logger);
        Console.WriteLine();

        // Journal Path Configuration
        ConfigureJournalPath(settings, logger);
        Console.WriteLine();

        Console.WriteLine("Setup complete! Starting monitoring...");
        Console.WriteLine();
        Console.WriteLine("Supported Events:");
        Console.WriteLine("┌─ Core Events:");
        Console.WriteLine("│  • FSDJump (Hyperspace jump with buildup rumble)");
        Console.WriteLine("│  • Docked/Undocked (Station docking impact)");
        Console.WriteLine("│  • HullDamage (Damage-scaled pulses)");
        Console.WriteLine("│  • ShipTargeted (Target lock pulses)");
        Console.WriteLine("├─ Combat Events:");
        Console.WriteLine("│  • UnderAttack (Intense combat pulses)");
        Console.WriteLine("│  • ShieldDown/ShieldsUp (Shield status)");
        Console.WriteLine("│  • FighterDestroyed (Explosion bursts)");
        Console.WriteLine("├─ Planetary Operations:");
        Console.WriteLine("│  • Touchdown/Liftoff (Planetary landings)");
        Console.WriteLine("├─ Heat & Fuel Management:");
        Console.WriteLine("│  • HeatWarning/HeatDamage (Oscillating heat alerts)");
        Console.WriteLine("│  • FuelScoop (Star scooping sustained rumble)");
        Console.WriteLine("├─ Fighter Operations:");
        Console.WriteLine("│  • LaunchFighter/DockFighter (Fighter bay operations)");
        Console.WriteLine("├─ Navigation:");
        Console.WriteLine("│  • JetConeBoost (Neutron star boost)");
        Console.WriteLine("│  • Interdicted/Interdiction (Interdiction events)");
        Console.WriteLine("└─");
        Console.WriteLine();
        Console.WriteLine("Press Ctrl+C to stop.");
        Console.WriteLine();
    }

    static Task ConfigureAudioDevice(AppSettings settings, ILogger logger)
    {
        Console.WriteLine("Audio Device Selection:");
        Console.WriteLine("----------------------");

        try
        {
            var devices = GetAvailableAudioDevices();
            
            if (!devices.Any())
            {
                Console.WriteLine("No audio devices found!");
                return Task.CompletedTask;
            }

            Console.WriteLine("Available audio devices:");
            for (int i = 0; i < devices.Count; i++)
            {
                Console.WriteLine($"{i + 1}. {devices[i]}");
            }

            Console.WriteLine($"{devices.Count + 1}. Use default device");
            Console.WriteLine();

            while (true)
            {
                Console.Write($"Select audio device (1-{devices.Count + 1}): ");
                var input = Console.ReadLine();
                
                if (int.TryParse(input, out int selection) && selection >= 1 && selection <= devices.Count + 1)
                {
                    if (selection == devices.Count + 1)
                    {
                        settings.Audio.AudioDeviceId = -1;
                        settings.Audio.AudioDeviceName = "Default";
                        Console.WriteLine("Using default audio device.");
                    }
                    else
                    {
                        var selectedDevice = devices[selection - 1];
                        settings.Audio.AudioDeviceId = selectedDevice.DeviceId;
                        settings.Audio.AudioDeviceName = selectedDevice.Name;
                        Console.WriteLine($"Selected: {selectedDevice.Name}");
                    }
                    break;
                }
                else
                {
                    Console.WriteLine("Invalid selection. Please try again.");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error configuring audio device");
            Console.WriteLine("Error detecting audio devices. Using default device.");
            settings.Audio.AudioDeviceId = -1;
            settings.Audio.AudioDeviceName = "Default";
        }
        
        return Task.CompletedTask;
    }

    static List<AudioDevice> GetAvailableAudioDevices()
    {
        var devices = new List<AudioDevice>();

        try
        {
            // Use MMDevice enumerator for better device detection
            var deviceEnumerator = new MMDeviceEnumerator();
            var devices_collection = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            
            for (int i = 0; i < devices_collection.Count; i++)
            {
                var device = devices_collection[i];
                devices.Add(new AudioDevice
                {
                    DeviceId = i,
                    Name = device.FriendlyName,
                    Driver = "WASAPI",
                    Channels = 2, // Default assumption
                    IsDefault = device.ID == deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID,
                    IsAvailable = true
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to enumerate audio devices using MMDevice: {ex.Message}");
            Console.WriteLine("Trying alternative device enumeration...");
            
            // Skip fallback enumeration for now to avoid issues with published version
            
            // Final fallback - add a default device entry if no devices were found
            if (devices.Count == 0)
            {
                devices.Add(new AudioDevice
                {
                    DeviceId = -1,
                    Name = "Default Audio Device",
                    Driver = "Default",
                    Channels = 2,
                    IsDefault = true,
                    IsAvailable = true
                });
            }
        }

        return devices;
    }

    static void ConfigureJournalPath(AppSettings settings, ILogger logger)
    {
        Console.WriteLine("Elite Dangerous Journal Path:");
        Console.WriteLine("----------------------------");

        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Saved Games", "Frontier Developments", "Elite Dangerous");

        Console.WriteLine($"Default path: {defaultPath}");
        
        if (Directory.Exists(defaultPath))
        {
            Console.WriteLine("✓ Default path exists and will be used.");
            settings.EliteDangerous.JournalPath = defaultPath;
        }
        else
        {
            Console.WriteLine("✗ Default path not found.");
            Console.WriteLine();
            Console.WriteLine("Please enter the path to your Elite Dangerous journal files:");
            Console.WriteLine("(This is typically in your saved games folder)");
            Console.WriteLine();

            while (true)
            {
                Console.Write("Journal path: ");
                var input = Console.ReadLine()?.Trim();
                
                        if (string.IsNullOrEmpty(input))
                {
                    Console.WriteLine("Please enter a valid path.");
                    continue;
                }

                if (input.Contains("%USERPROFILE%"))
                {
                    input = input.Replace("%USERPROFILE%", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                }

                if (Directory.Exists(input))
                {
                    settings.EliteDangerous.JournalPath = input;
                    Console.WriteLine($"✓ Path set to: {input}");
                    break;
                }
                else
                {
                    Console.WriteLine("✗ Path does not exist. Please check and try again.");
                    Console.WriteLine("  (You can also press Ctrl+C to exit and run the game first)");
                }
            }
        }
    }

    private static void FlattenJson(System.Text.Json.JsonElement element, Dictionary<string, string> result, string prefix)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var key = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}:{property.Name}";
                    FlattenJson(property.Value, result, key);
                }
                break;
            case System.Text.Json.JsonValueKind.Array:
                int index = 0;
                foreach (var arrayElement in element.EnumerateArray())
                {
                    FlattenJson(arrayElement, result, $"{prefix}:{index}");
                    index++;
                }
                break;
            default:
                result[prefix] = element.ToString();
                break;
        }
    }
}