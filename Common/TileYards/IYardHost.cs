using System;

namespace TerrariaDynamics.Common.TileYards;

// World operations stay behind the host so an instance never owns the global
// tile arrays or bypasses the world's save lock.
internal interface IYardHost
{
    bool CanBeginReturn(Yard yard, out string message);
    bool TryReturn(Yard yard, out string message);
    string ReturnMovedMessage { get; }
    void ReportObserverError(Exception error);
}
