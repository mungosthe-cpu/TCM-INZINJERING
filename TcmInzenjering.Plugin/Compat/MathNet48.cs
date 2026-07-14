namespace System;

/// <summary>Portable clamp for net48 (Math.Clamp is netcoreapp2.0+).</summary>
internal static class MathNet48
{
    public static double Clamp(double value, double min, double max)
    {
#if NET8_0_OR_GREATER
        return Math.Clamp(value, min, max);
#else
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
#endif
    }
}
