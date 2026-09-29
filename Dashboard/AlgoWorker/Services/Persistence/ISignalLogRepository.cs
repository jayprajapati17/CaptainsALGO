using AlgoData.Models;

namespace AlgoWorker.Services.Persistence;

/// <summary>
/// >>> NEW (Task 3): logs every confirmed signal (whether or not it led to a
/// trade) to the shared database. Called from the Position Trackers (NOT the
/// Signal Engines) -- the tracker is where "was a position actually opened,
/// or skipped because a slot was busy / instrument didn't resolve" is known.
/// </summary>
public interface ISignalLogRepository
{
    Task LogAsync(SignalLogEntity entry, CancellationToken ct);
}