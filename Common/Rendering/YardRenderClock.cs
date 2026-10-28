using System.Diagnostics;

namespace TerrariaDynamics.Common.Rendering;

internal static class YardRenderClock
{
    public static double Seconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
