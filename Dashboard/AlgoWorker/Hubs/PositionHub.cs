using Microsoft.AspNetCore.SignalR;

namespace AlgoWorker.Hubs;

/// <summary>
/// >>> NEW (Task 4): broadcast-only SignalR hub. The Dashboard's browser JS
/// connects here directly (different port/process) to get real-time position
/// updates -- no server-invokable methods needed yet (manual "close position"
/// goes through the Command API in Task 5, not through this hub). Worker-side
/// code broadcasts via IHubContext&lt;PositionHub&gt; from VirtualPositionTracker
/// and BreakoutPositionTracker whenever a position opens/updates/closes.
/// </summary>
public sealed class PositionHub : Hub
{
    // Intentionally empty -- see class remarks. If the Dashboard ever needs
    // to invoke something ON the hub directly (rather than via the Command
    // API), add methods here.
}