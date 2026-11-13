namespace MeshRoutePlanner.Core;

public enum RouteSearchEventType { Progress, Done }

/// <summary>Why a streamed search stopped producing new candidates.</summary>
public enum RouteSearchStopReason { Completed, TimedOut, Stopped }

/// <summary>
/// One line of a streamed `/api/route` search. A run emits zero or more <see cref="RouteSearchEventType.Progress"/>
/// events (so the UI can show that something is happening) followed by exactly one
/// <see cref="RouteSearchEventType.Done"/> event carrying the final result.
/// </summary>
public class RouteSearchEvent
{
    public required RouteSearchEventType Type { get; init; }

    // Progress
    public int? ExploredCount { get; init; }
    public int? CandidatesFoundCount { get; init; }
    public double? ElapsedSeconds { get; init; }

    // Done
    public RouteSearchResult? Result { get; init; }
    public RouteSearchStopReason? StopReason { get; init; }
}
