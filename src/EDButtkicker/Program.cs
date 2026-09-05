using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.Wasapi;
using EDButtkicker.Configuration;
using EDButtkicker.Hosting;
using EDButtkicker.Models;
using EDButtkicker.Services;
using EDButtkicker.Controllers;

namespace EDButtkicker;

class Program
{
    static async Task Main(string[] args)
    {
        // Check for debug flag
        bool debugMode = args.Contains("--debug") || args.Contains("-d");
        bool helpMode = args.Contains("--help") || args.Contains("-h");
        
        if (helpMode)
        {
            ShowHelp();
            return;
        }
        
        Console.WriteLine("Elite Dangerous Buttkicker Extension");
        Console.WriteLine("====================================");
        if (debugMode)
        {
            Console.WriteLine("🔍 DEBUG MODE ENABLED");
            Console.WriteLine("Detailed logging will be displayed");
        }
        Console.WriteLine();

        var hostBuilder = CreateHostBuilder(args, debugMode);
        var host = hostBuilder.Build();

        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        var config = host.Services.GetRequiredService<IConfiguration>();
        var appSettings = host.Services.GetRequiredService<AppSettings>();

        logger.LogInformation("Starting Elite Dangerous Buttkicker Extension (Debug: {DebugMode})", debugMode);

        try
        {
            // Auto-configure with default settings
            // Load and apply user settings
            var userSettingsService = host.Services.GetRequiredService<UserSettingsService>();
            await LoadAndApplyUserSettings(appSettings, userSettingsService, logger, debugMode);

            // Load patterns at startup
            logger.LogInformation("Loading pattern files...");
            var patternFileService = host.Services.GetRequiredService<PatternFileService>();
            await patternFileService.LoadAllPatternsAsync();

            // Start services and web UI
            logger.LogInformation("Starting services and web interface...");
            var setupState = await host.Services.GetRequiredService<SetupStateService>().LoadAsync();
            ShowStartupInfo(appSettings, setupState, debugMode);
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Application terminated unexpectedly");
            Console.WriteLine($"Error: {ex.Message}");
            if (debugMode)
            {
                Console.WriteLine();
                Console.WriteLine("=== Debug Information ===");
                Console.WriteLine($"Exception Type: {ex.GetType().Name}");
                Console.WriteLine($"Stack Trace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                }
            }
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
    }

    static void ShowHelp()
    {
        Console.WriteLine("Elite Dangerous Buttkicker Extension");
        Console.WriteLine("====================================");
        Console.WriteLine();
        Console.WriteLine("Usage: EDButtkicker [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -d, --debug     Enable debug mode with detailed logging");
        Console.WriteLine("  -h, --help      Show this help message");
        Console.WriteLine();
        Console.WriteLine("Debug Mode:");
        Console.WriteLine("  When enabled, shows detailed information about:");
        Console.WriteLine("  • Audio device enumeration and selection");
        Console.WriteLine("  • NAudio initialization and configuration");
        Console.WriteLine("  • Pattern playback and mixer operations");
        Console.WriteLine("  • Error diagnosis and troubleshooting");
        Console.WriteLine();
        Console.WriteLine("Example: EDButtkicker --debug");
        Console.WriteLine();
    }

    static IHostBuilder CreateHostBuilder(string[] args, bool debugMode = false) =>
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
            .UseDefaultServiceProvider(options =>
            {
                // One graph, and it has to be resolvable: a missing controller dependency is a
                // startup failure instead of a 500 on the first request that needs it.
                options.ValidateScopes = true;
                options.ValidateOnBuild = true;
            })
            .ConfigureServices((hostContext, services) =>
            {
                // Bind configuration
                var appSettings = new AppSettings();
                hostContext.Configuration.Bind(appSettings);

                // Runtime services, web controllers and their dependencies - one registration
                // method, shared with the integration tests.
                services.AddEliteButtkicker(appSettings);

                // Add hosted services
                services.AddHostedService<JournalMonitorService>();
                services.AddHostedService<WebConfigurationService>();
				services.AddHostedService<StatusMonitorService>();
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                // The web app lives in the primary service provider - there is no second container.
                webBuilder.UseKestrel(options =>
                {
                    options.ListenLocalhost(WebUiConfiguration.Port);

                    // First line of the body bound: the server refuses an oversize request before a
                    // handler sees it. Every handler enforces the same cap on what it actually reads.
                    options.Limits.MaxRequestBodySize = RequestLimits.MaxRequestBodyBytes;
                });
                webBuilder.Configure(WebUiConfiguration.Configure);
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole(options =>
                {
                    options.IncludeScopes = debugMode;
                    options.TimestampFormat = debugMode ? "yyyy-MM-dd HH:mm:ss.fff " : null;
                });
                logging.AddDebug();
                
                if (debugMode)
                {
                    // Override log levels for debug mode
                    logging.SetMinimumLevel(LogLevel.Debug);
                }
            });

    static async Task LoadAndApplyUserSettings(AppSettings settings, UserSettingsService userSettingsService, ILogger logger, bool debugMode = false)
    {
        try
        {
            // Load user preferences
            var userPreferences = await userSettingsService.LoadUserPreferencesAsync();
            
            if (userSettingsService.UserSettingsExist())
            {
                // Apply saved preferences to app settings
                userSettingsService.ApplyUserPreferencesToAppSettings(userPreferences, settings);
                
                if (debugMode)
                {
                    Console.WriteLine("💾 Loaded saved user settings:");
                    Console.WriteLine("----------------------------");
                    Console.WriteLine($"Settings file: {userSettingsService.GetUserSettingsPath()}");
                    Console.WriteLine($"Audio Device: {userPreferences.AudioDeviceName ?? "Default"} (ID: {userPreferences.AudioDeviceId ?? -1})");
                    Console.WriteLine($"Journal Path: {userPreferences.JournalPath ?? "Auto-detect"}");
                    Console.WriteLine($"Last Saved: {userPreferences.LastSaved?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Unknown"}");
                    Console.WriteLine();
                }
                
                logger.LogInformation("Applied saved user settings");
            }
            else
            {
                // No saved settings, use auto-configuration
                await AutoConfigureDefaults(settings, logger, debugMode);
                
                if (debugMode)
                {
                    Console.WriteLine("🏠 No saved settings found - using defaults");
                    Console.WriteLine("Settings will be saved when changed via web interface");
                    Console.WriteLine();
                }
                
                logger.LogInformation("No user settings found, using defaults");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading user settings, falling back to defaults");
            await AutoConfigureDefaults(settings, logger, debugMode);
        }
    }

    static async Task AutoConfigureDefaults(AppSettings settings, ILogger logger, bool debugMode = false)
    {
        if (debugMode)
        {
            Console.WriteLine("Auto-configuring with defaults...");
            Console.WriteLine();
        }

        // Auto-configure audio device to default
        await AutoConfigureAudioDevice(settings, logger, debugMode);

        // Auto-configure journal path
        AutoConfigureJournalPath(settings, logger, debugMode);

        if (debugMode)
        {
            Console.WriteLine("Auto-configuration complete!");
            Console.WriteLine();
        }
    }

    static void ShowStartupInfo(AppSettings settings, SetupState setupState, bool debugMode = false)
    {
        var journalPath = settings.EliteDangerous.JournalPath;
        var journalFolderExists = !string.IsNullOrWhiteSpace(journalPath) && Directory.Exists(journalPath);

        Console.WriteLine("✓ Elite Dangerous Buttkicker Extension is running!");
        Console.WriteLine();
        Console.WriteLine($"🌍 Web Interface: http://localhost:{WebUiConfiguration.Port}");
        Console.WriteLine($"🎵 Audio: {(string.IsNullOrWhiteSpace(settings.Audio.AudioDeviceName) ? "System default device" : settings.Audio.AudioDeviceName)}");
        // Say what the journal folder actually is, and whether it is there - the old text claimed
        // auto-detection had worked no matter what.
        Console.WriteLine($"📁 Journal: {journalPath} ({(journalFolderExists ? "found" : "not found yet")})");
        Console.WriteLine(setupState.Completed
            ? "🧭 Setup: completed - reopen the wizard from the dashboard whenever you need it"
            : "🧭 Setup: not completed - the first-run wizard opens in the web interface");
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
        Console.WriteLine("🔧 Open http://localhost:47811 to configure audio device and test patterns");
        Console.WriteLine("⏹️  Press Ctrl+C to stop");
        Console.WriteLine();
    }

    static async Task AutoConfigureAudioDevice(AppSettings settings, ILogger logger, bool debugMode = false)
    {
        try
        {
            // Set to default device automatically
            settings.Audio.AudioDeviceId = -1;
            settings.Audio.AudioDeviceName = "Default";
            
            if (debugMode)
            {
                Console.WriteLine("🎵 Audio Configuration:");
                Console.WriteLine("----------------------");
                Console.WriteLine("✓ Using system default audio device");
                Console.WriteLine("  (Can be changed via web interface at http://localhost:47811)");
                Console.WriteLine();
                
                // Still show device enumeration in debug mode
                var devices = GetAvailableAudioDevices(debugMode);
                Console.WriteLine($"Available devices ({devices.Count} found):");
                foreach (var line in AudioDeviceDiagnostics.DescribeEnumeration(devices, "WASAPI render"))
                {
                    Console.WriteLine($"  {line}");
                }
                Console.WriteLine($"  {AudioDeviceDiagnostics.DescribeConfiguredSelection(devices, settings.Audio.AudioDeviceId, settings.Audio.AudioDeviceName)}");
                // The resolver is what playback actually follows, so the console says the same
                // thing the log will.
                Console.WriteLine($"  {AudioDeviceResolver.Resolve(devices, settings.Audio.AudioDeviceEndpointId, settings.Audio.AudioDeviceName, settings.Audio.AudioDeviceId).Reason}");
                Console.WriteLine();
            }
            
            logger.LogInformation("Auto-configured audio to use default system device");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error during audio device auto-configuration, using fallback");
            settings.Audio.AudioDeviceId = -1;
            settings.Audio.AudioDeviceName = "Default";
        }
    }

    static void AutoConfigureJournalPath(AppSettings settings, ILogger logger, bool debugMode = false)
    {
        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Saved Games", "Frontier Developments", "Elite Dangerous");

        if (debugMode)
        {
            Console.WriteLine("📁 Journal Configuration:");
            Console.WriteLine("-----------------------");
            Console.WriteLine($"Checking: {defaultPath}");
        }
        
        if (Directory.Exists(defaultPath))
        {
            settings.EliteDangerous.JournalPath = defaultPath;
            if (debugMode)
            {
                Console.WriteLine("✓ Elite Dangerous journal folder found and configured");
                Console.WriteLine();
            }
            logger.LogInformation("Auto-configured journal path: {JournalPath}", defaultPath);
        }
        else
        {
            // Use the configured path or default path as fallback
            settings.EliteDangerous.JournalPath = defaultPath; 
            if (debugMode)
            {
                Console.WriteLine("⚠️  Elite Dangerous folder not found at default location");
                Console.WriteLine("   Journal monitoring will start when Elite Dangerous is launched");
                Console.WriteLine();
            }
            logger.LogWarning("Elite Dangerous journal folder not found, using default path: {JournalPath}", defaultPath);
        }
    }

    static Task ConfigureAudioDevice(AppSettings settings, ILogger logger, bool debugMode = false)
    {
        Console.WriteLine("Audio Device Selection:");
        Console.WriteLine("----------------------");

        try
        {
            var devices = GetAvailableAudioDevices(debugMode);
            
            if (!devices.Any())
            {
                Console.WriteLine("No audio devices found!");
                return Task.CompletedTask;
            }

            Console.WriteLine("Available audio devices:");
            for (int i = 0; i < devices.Count; i++)
            {
                // Menu numbers are 1-based for input; the DeviceId that gets saved is not.
                Console.WriteLine($"{i + 1}. {devices[i]} [DeviceId={devices[i].DeviceId}]");
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
                        settings.Audio.AudioDeviceEndpointId = string.Empty;
                        settings.Audio.AudioDeviceName = "Default";
                        Console.WriteLine("✓ Using default audio device.");
                        if (debugMode)
                        {
                            Console.WriteLine($"[DEBUG] Device ID set to: {settings.Audio.AudioDeviceId}");
                            Console.WriteLine($"[DEBUG] Device Name set to: '{settings.Audio.AudioDeviceName}'");
                        }
                    }
                    else
                    {
                        var selectedDevice = devices[selection - 1];
                        settings.Audio.AudioDeviceId = selectedDevice.DeviceId;
                        settings.Audio.AudioDeviceEndpointId = selectedDevice.EndpointId;
                        settings.Audio.AudioDeviceName = selectedDevice.Name;
                        Console.WriteLine($"✓ Selected: {selectedDevice.Name}");
                        if (debugMode)
                        {
                            Console.WriteLine($"[DEBUG] Device ID set to: {settings.Audio.AudioDeviceId}");
                            Console.WriteLine($"[DEBUG] Device Name set to: '{settings.Audio.AudioDeviceName}'");
                            Console.WriteLine($"[DEBUG] Device Driver: {selectedDevice.Driver}");
                            Console.WriteLine($"[DEBUG] Device Available: {selectedDevice.IsAvailable}");
                            Console.WriteLine($"[DEBUG] Device Default: {selectedDevice.IsDefault}");
                        }
                        
                        if (!selectedDevice.IsAvailable)
                        {
                            Console.WriteLine("⚠️  WARNING: Selected device is not currently active!");
                        }
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

    static List<AudioDevice> GetAvailableAudioDevices(bool debugMode = false)
    {
        var devices = new List<AudioDevice>();

        try
        {
            if (debugMode)
                Console.WriteLine("[DEBUG] Enumerating audio devices...");
            
            // Use MMDevice enumerator for better device detection
            var deviceEnumerator = new MMDeviceEnumerator();
            var devices_collection = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            
            if (debugMode)
                Console.WriteLine($"[DEBUG] Found {devices_collection.Count} WASAPI render devices");
            
            var defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (debugMode)
                Console.WriteLine($"[DEBUG] System default device: '{defaultDevice.FriendlyName}' (ID: {defaultDevice.ID})");
            
            for (int i = 0; i < devices_collection.Count; i++)
            {
                var device = devices_collection[i];
                var isDefault = device.ID == defaultDevice.ID;
                
                if (debugMode)
                    Console.WriteLine($"[DEBUG] Device {i}: '{device.FriendlyName}' - State: {device.State}, Default: {isDefault}");
                
                devices.Add(new AudioDevice
                {
                    // The endpoint id is the identity; the index is only where it sits today.
                    EndpointId = device.ID,
                    DeviceId = i,
                    Name = device.FriendlyName,
                    Driver = "WASAPI",
                    Channels = 2, // Default assumption
                    IsDefault = isDefault,
                    IsAvailable = device.State == DeviceState.Active
                });
            }
            
            // Debug logging already handled by MMDevice enumeration above
            if (debugMode)
            {
                Console.WriteLine($"[DEBUG] Device enumeration completed using WASAPI/MMDevice API");
            }
        }
        catch (Exception ex)
        {
            if (debugMode)
            {
                Console.WriteLine($"[DEBUG] Error enumerating devices: {ex.Message}");
                Console.WriteLine($"[DEBUG] Exception type: {ex.GetType().Name}");
            }
            else
            {
                Console.WriteLine($"Warning: Failed to enumerate audio devices using MMDevice: {ex.Message}");
            }
            
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

        if (debugMode)
            Console.WriteLine($"[DEBUG] Returning {devices.Count} available devices");
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