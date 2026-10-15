using Microsoft.AspNetCore.SignalR;

namespace AlgoWorker.Hubs;

/// <summary>
/// The one place that pushes live events to the Dashboard over <see cref="PositionHub"/>. Every tracker and both
/// pipelines used to carry their own copy of this try/catch; a SignalR hiccup must never interrupt trading logic
/// or Telegram alerts, so failures are only logged.
/// </summary>
public static class PositionBroadcast
{
    /// <summary>"PositionChanged": a position opened, updated (premium / stop-loss) or closed.</summary>
    public static async Task PublishAsync(IHubContext<PositionHub> hub, PositionUpdateDto dto, ILogger logger, CancellationToken ct)
    {
        try
        {
            await hub.Clients.All.SendAsync("PositionChanged", dto, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to broadcast PositionChanged for PositionId {Id}.", dto.PositionId);
        }
    }

    /// <summary>
    /// "SpotChanged": the index price for the Live page header. Nifty's event has no <c>underlying</c> field (the
    /// Dashboard treats a missing field as Nifty); Bank Nifty's carries <c>underlying = "BANKNIFTY"</c>.
    /// </summary>
    public static async Task PublishSpotAsync(IHubContext<PositionHub> hub, decimal price, DateTimeOffset time, string? underlying, ILogger logger)
    {
        try
        {
            if (underlying is null)
                await hub.Clients.All.SendAsync("SpotChanged", new { price, time });
            else
                await hub.Clients.All.SendAsync("SpotChanged", new { price, time, underlying });
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to broadcast SpotChanged ({Underlying}).", underlying ?? "NIFTY50");
        }
    }
}
