using EDButtkicker.Models;

namespace EDButtkicker.Services;

public static class IntensityCurveProcessor
{
    /// <summary>
    /// Calculate intensity at a specific time point based on the curve type
    /// </summary>
    /// <param name="curve">The curve type to use</param>
    /// <param name="time">Time progress (0.0 to 1.0)</param>
    /// <param name="baseIntensity">Base intensity (0.0 to 1.0)</param>
    /// <param name="customPoints">Custom curve points (for Custom curve type)</param>
    /// <returns>Calculated intensity (0.0 to 1.0)</returns>
    public static float CalculateIntensity(IntensityCurve curve, float time, float baseIntensity, List<CurvePoint>? customPoints = null)
    {
        // Clamp time to valid range
        time = Math.Max(0.0f, Math.Min(1.0f, time));
        
        float curveValue = curve switch
        {
            IntensityCurve.Linear => CalculateLinear(time),
            IntensityCurve.Exponential => CalculateExponential(time),
            IntensityCurve.Logarithmic => CalculateLogarithmic(time),
            IntensityCurve.Sine => CalculateSine(time),
            IntensityCurve.Bounce => CalculateBounce(time),
            IntensityCurve.Custom => CalculateCustom(time, customPoints ?? new List<CurvePoint>()),
            _ => time // Default to linear
        };
        
        return Math.Max(0.0f, Math.Min(1.0f, baseIntensity * curveValue));
    }
    
    private static float CalculateLinear(float time)
    {
        return time;
    }
    
    private static float CalculateExponential(float time)
    {
        // Exponential curve: starts slow, accelerates rapidly
        return (float)(Math.Pow(time, 2.5));
    }
    
    private static float CalculateLogarithmic(float time)
    {
        // Logarithmic curve: starts fast, decelerates
        if (time <= 0) return 0;
        return (float)(Math.Log(time * 9 + 1) / Math.Log(10));
    }
    
    private static float CalculateSine(float time)
    {
        // Smooth sine wave from 0 to peak and back
        return (float)(Math.Sin(time * Math.PI));
    }
    
    private static float CalculateBounce(float time)
    {
        // Bouncy effect - good for impacts
        const float c1 = 1.70158f;
        const float c2 = c1 * 1.525f;
        
        if (time < 0.5f)
        {
            return (float)(Math.Pow(2 * time, 2) * ((c2 + 1) * 2 * time - c2) / 2);
        }
        else
        {
            return (float)((Math.Pow(2 * time - 2, 2) * ((c2 + 1) * (time * 2 - 2) + c2) + 2) / 2);
        }
    }
    
    private static float CalculateCustom(float time, List<CurvePoint> points)
    {
        if (!points.Any()) return time; // Fallback to linear
        
        // Sort points by time
        var sortedPoints = points.OrderBy(p => p.Time).ToList();
        
        // Add start and end points if not present
        if (!sortedPoints.Any(p => p.Time <= 0.001f))
        {
            sortedPoints.Insert(0, new CurvePoint { Time = 0.0f, Intensity = 0.0f });
        }
        if (!sortedPoints.Any(p => p.Time >= 0.999f))
        {
            sortedPoints.Add(new CurvePoint { Time = 1.0f, Intensity = 1.0f });
        }
        
        // Find the two points to interpolate between
        for (int i = 0; i < sortedPoints.Count - 1; i++)
        {
            var p1 = sortedPoints[i];
            var p2 = sortedPoints[i + 1];
            
            if (time >= p1.Time && time <= p2.Time)
            {
                // Linear interpolation between the two points
                float t = (time - p1.Time) / (p2.Time - p1.Time);
                return p1.Intensity + t * (p2.Intensity - p1.Intensity);
            }
        }
        
        return time; // Fallback
    }
    
    /// <summary>
    /// Generate a preview of curve values for visualization
    /// </summary>
    public static List<float> GenerateCurvePreview(IntensityCurve curve, List<CurvePoint>? customPoints = null, int samples = 100)
    {
        var values = new List<float>();
        
        for (int i = 0; i < samples; i++)
        {
            float time = (float)i / (samples - 1);
            float intensity = CalculateIntensity(curve, time, 1.0f, customPoints);
            values.Add(intensity);
        }
        
        return values;
    }
}