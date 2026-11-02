namespace Highbridge.Scanning;

/// <summary>Picks the value a scanner allows that is closest to the one requested (e.g. a resolution).</summary>
public static class NearestValue
{
    /// <summary>Closest of <paramref name="allowed"/>; on a tie, the larger value. Null when the list is empty.</summary>
    public static double? FromList(IEnumerable<double> allowed, double wanted)
    {
        double? best = null;
        foreach (double value in allowed)
        {
            if (best is null
                || Math.Abs(value - wanted) < Math.Abs(best.Value - wanted)
                || (Math.Abs(value - wanted) == Math.Abs(best.Value - wanted) && value > best.Value))
            {
                best = value;
            }
        }

        return best;
    }

    /// <summary>Closest value in [min, max] that is min plus a whole number of steps.</summary>
    public static double FromRange(double min, double max, double step, double wanted)
    {
        if (max < min)
        {
            (min, max) = (max, min);
        }

        double clamped = Math.Clamp(wanted, min, max);
        if (step <= 0)
        {
            return clamped;
        }

        double snapped = min + (Math.Round((clamped - min) / step, MidpointRounding.AwayFromZero) * step);
        return snapped > max ? snapped - step : snapped;
    }
}
