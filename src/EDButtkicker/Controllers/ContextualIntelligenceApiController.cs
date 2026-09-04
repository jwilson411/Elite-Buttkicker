using Microsoft.AspNetCore.Http;
using System.Text.Json;
using EDButtkicker.Configuration;
using EDButtkicker.Models;
using EDButtkicker.Services;
using Microsoft.Extensions.Logging;

namespace EDButtkicker.Controllers;

public class ContextualIntelligenceApiController
{
	private readonly ILogger<ContextualIntelligenceApiController> _logger;
	private readonly AppSettings _settings;
	private readonly ContextualIntelligenceService _contextualIntelligence;
	private readonly SettingsPersistenceService _settingsPersistence;

	public ContextualIntelligenceApiController(
		ILogger<ContextualIntelligenceApiController> logger,
		AppSettings settings,
		ContextualIntelligenceService contextualIntelligence,
		SettingsPersistenceService settingsPersistence)
	{
		_logger = logger;
		_settings = settings;
		_contextualIntelligence = contextualIntelligence;
		_settingsPersistence = settingsPersistence;
	}

	public async Task GetContextualIntelligenceStatus(HttpContext context)
	{
		try
		{
			var gameContext = _contextualIntelligence.GetCurrentContext();
			var config = _settings.ContextualIntelligence ?? new ContextualIntelligenceConfiguration();

			var response = new
			{
				configuration = new
				{
					enabled = config.Enabled,
					learning_rate = config.LearningRate,
					prediction_threshold = config.PredictionThreshold,
					adaptive_intensity = config.EnableAdaptiveIntensity,
					predictive_patterns = config.EnablePredictivePatterns,
					contextual_voice = config.EnableContextualVoice,
					log_analysis = config.LogContextAnalysis
				},
				current_context = new
				{
					game_state = gameContext.CurrentState.ToString(),
					state_duration = gameContext.StateActivityDuration.TotalMinutes,
					current_system = gameContext.CurrentSystem,
					current_station = gameContext.CurrentStation,
					hull_integrity = gameContext.HullIntegrity,
					shield_strength = gameContext.ShieldStrength,
					threat_level = gameContext.ThreatLevel.ToString(),
					combat_intensity = gameContext.CombatIntensity,
					exploration_mode = gameContext.ExplorationActivity.ToString(),
					is_carrying_cargo = gameContext.IsCarryingCargo,
					cargo_value = gameContext.CargoValue,
					player_aggressiveness = gameContext.PlayerAggressiveness,
					player_cautiousness = gameContext.PlayerCautiousness,
					intensity_multiplier = gameContext.GetContextualIntensityMultiplier(),
					is_dangerous_situation = gameContext.IsInDangerousSituation(),
					is_routine_activity = gameContext.IsInRoutineActivity()
				},
				predictions = new
				{
					predicted_next_state = gameContext.PredictedNextState?.ToString(),
					prediction_confidence = gameContext.PredictionConfidence,
					likely_upcoming_events = gameContext.LikelyUpcomingEvents
				},
				statistics = new
				{
					systems_visited = gameContext.SystemsVisited,
					bodies_scanned = gameContext.BodiesScanned,
					recent_event_types = gameContext.RecentEventFrequency.Keys.Take(10).ToArray(),
					state_time_spent = gameContext.StateTimeSpent.Select(kvp => new
					{
						state = kvp.Key.ToString(),
						time_minutes = kvp.Value.TotalMinutes
					}).ToArray()
				}
			};

			context.Response.ContentType = "application/json";
			await context.Response.WriteAsync(JsonSerializer.Serialize(response, new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error getting contextual intelligence status");
			if (!context.Response.HasStarted)
			{
				context.Response.StatusCode = 500;
				context.Response.ContentType = "application/json";
				await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
			}
		}
	}

	public async Task UpdateContextualIntelligenceConfig(HttpContext context)
	{
		try
		{
			using var reader = new StreamReader(context.Request.Body);
			var json = await reader.ReadToEndAsync();

			if (string.IsNullOrEmpty(json))
			{
				context.Response.StatusCode = 400;
				context.Response.ContentType = "application/json";
				await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Request body is empty" }));
				return;
			}

			var configUpdate = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
			if (configUpdate == null)
			{
				context.Response.StatusCode = 400;
				context.Response.ContentType = "application/json";
				await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Invalid JSON format" }));
				return;
			}

			var update = new SettingsUpdate();

			if (configUpdate.ContainsKey("enabled") && bool.TryParse(configUpdate["enabled"].ToString(), out bool enabled))
				update.ContextualIntelligenceEnabled = enabled;

			// Both rates are clamped rather than refused, as they always were: they arrive from
			// sliders, and a slider at its end stop is not a mistake worth an error.
			if (configUpdate.ContainsKey("learning_rate") && double.TryParse(configUpdate["learning_rate"].ToString(), out double learningRate))
				update.LearningRate = Math.Clamp(learningRate, 0.01, 1.0);

			if (configUpdate.ContainsKey("prediction_threshold") && double.TryParse(configUpdate["prediction_threshold"].ToString(), out double predictionThreshold))
				update.PredictionThreshold = Math.Clamp(predictionThreshold, 0.1, 1.0);

			if (configUpdate.ContainsKey("adaptive_intensity") && bool.TryParse(configUpdate["adaptive_intensity"].ToString(), out bool adaptiveIntensity))
				update.EnableAdaptiveIntensity = adaptiveIntensity;

			if (configUpdate.ContainsKey("predictive_patterns") && bool.TryParse(configUpdate["predictive_patterns"].ToString(), out bool predictivePatterns))
				update.EnablePredictivePatterns = predictivePatterns;

			if (configUpdate.ContainsKey("contextual_voice") && bool.TryParse(configUpdate["contextual_voice"].ToString(), out bool contextualVoice))
				update.EnableContextualVoice = contextualVoice;

			if (configUpdate.ContainsKey("log_analysis") && bool.TryParse(configUpdate["log_analysis"].ToString(), out bool logAnalysis))
				update.LogContextAnalysis = logAnalysis;

			// Same persistence path as every other settings change, so these choices are still in
			// place after a restart instead of living only in this process.
			var result = await _settingsPersistence.ApplyAsync(update);

			if (!result.Valid)
			{
				context.Response.StatusCode = 400;
				context.Response.ContentType = "application/json";
				await context.Response.WriteAsync(JsonSerializer.Serialize(new
				{
					error = result.Message,
					validation_errors = result.ValidationErrors
				}));
				return;
			}

			var config = _settings.ContextualIntelligence ?? new ContextualIntelligenceConfiguration();

			_logger.LogInformation(
				"Contextual Intelligence configuration updated via web interface - Enabled: {Enabled}: {Message}",
				config.Enabled, result.Message);

			context.Response.StatusCode = result.Saved ? 200 : 500;
			context.Response.ContentType = "application/json";
			await context.Response.WriteAsync(JsonSerializer.Serialize(new
			{
				success = result.Saved,
				message = result.Message,
				enabled = config.Enabled,
				settings = result.ToPayload()
			}));
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error updating contextual intelligence configuration");
			if (!context.Response.HasStarted)
			{
				context.Response.StatusCode = 500;
				context.Response.ContentType = "application/json";
				await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
			}
		}
	}

	public async Task GetGameContextPredictions(HttpContext context)
	{
		try
		{
			var predictions = _contextualIntelligence.GetPredictedUpcomingEvents();
			var gameContext = _contextualIntelligence.GetCurrentContext();

			var response = new
			{
				predictions = predictions,
				current_state = gameContext.CurrentState.ToString(),
				predicted_next_state = gameContext.PredictedNextState?.ToString(),
				confidence = gameContext.PredictionConfidence,
				context_factors = new
				{
					threat_level = gameContext.ThreatLevel.ToString(),
					hull_integrity = gameContext.HullIntegrity,
					time_in_state = gameContext.StateActivityDuration.TotalMinutes,
					carrying_cargo = gameContext.IsCarryingCargo,
					exploration_activity = gameContext.ExplorationActivity.ToString()
				}
			};

			context.Response.ContentType = "application/json";
			await context.Response.WriteAsync(JsonSerializer.Serialize(response, new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error getting game context predictions");
			if (!context.Response.HasStarted)
			{
				context.Response.StatusCode = 500;
				context.Response.ContentType = "application/json";
				await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
			}
		}
	}
}