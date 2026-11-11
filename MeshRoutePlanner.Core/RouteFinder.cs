namespace MeshRoutePlanner.Core;

/// <summary>
/// Finds plausible routes between two MeshCore nodes using CoreScope's historical path data
/// (https://meshcore.meshat.se), the same data the site's "Paths Through This Node" panel shows.
///
/// The core idea mirrors how you'd do this by hand on the site: look at the destination's known
/// paths, see if the origin already appears earlier in one of them (a direct hit), and if not,
/// step back to whichever node most recently relayed into the destination and repeat from there.
/// This is a backward BFS over an implicit graph whose edges are "node X appeared immediately
/// before node Y in some observed transmission's path".
/// </summary>
public class RouteFinder
{
    private readonly CoreScopeClient _client;
    private readonly Dictionary<string, NodePathsResponse?> _pathsCache = new(StringComparer.OrdinalIgnoreCase);

    public RouteFinder(CoreScopeClient client)
    {
        _client = client;
    }

    public async Task<List<RouteCandidate>> FindRoutesAsync(
        string fromPubkey,
        string toPubkey,
        int maxHops = 6,
        int maxNodesExplored = 60,
        int maxCandidates = 5,
        CancellationToken ct = default)
    {
        var candidates = new List<RouteCandidate>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { toPubkey };

        // Each queue entry is a frontier node plus the chain already built from it to the
        // destination (frontier node first, destination last), and the weakest/oldest stats
        // seen along that chain so far.
        var queue = new Queue<Frontier>();
        queue.Enqueue(new Frontier(toPubkey, [new RouteHop { Pubkey = toPubkey }], int.MaxValue, null));

        var explored = 0;
        while (queue.Count > 0 && explored < maxNodesExplored && candidates.Count < maxCandidates)
        {
            var frontier = queue.Dequeue();
            if (frontier.ChainToDestination.Count - 1 >= maxHops)
                continue;

            var paths = await GetPathsCachedAsync(frontier.Pubkey, ct);
            explored++;
            if (paths is null)
                continue;

            foreach (var path in paths.Paths)
            {
                // A path's hop list is truncated at whichever node its own `/paths` endpoint was
                // queried for, so the true destination can only show up here as either that
                // node itself (frontier == destination, first iteration) or as an earlier,
                // already-passed hop within the SAME single real transmission.
                var selfIndex = path.Hops.FindIndex(h => Matches(h, frontier.Pubkey));
                if (selfIndex < 0)
                    continue;

                var destIndex = path.Hops.FindIndex(h => Matches(h, toPubkey));
                var fromIndex = path.Hops.FindIndex(h => Matches(h, fromPubkey));

                if (fromIndex >= 0 && destIndex >= 0 && fromIndex < destIndex)
                {
                    // One real transmission already carried traffic the whole way from origin to
                    // destination - a confirmed hit, independent of whatever chain got us here.
                    var segment = path.Hops.Skip(fromIndex).Take(destIndex - fromIndex + 1).Select(ToHop).ToList();
                    AddCandidate(candidates, segment, RouteSource.Observed, path.LastSeen, path.Count);
                }
                else if (destIndex < 0 && fromIndex >= 0 && fromIndex < selfIndex)
                {
                    // This transmission only reaches as far as `frontier`, not the destination -
                    // safe to stitch onto the chain already built from `frontier` to the destination.
                    var segment = path.Hops.Skip(fromIndex).Take(selfIndex - fromIndex + 1).Select(ToHop).ToList();
                    var fullChain = segment.SkipLast(1).Concat(frontier.ChainToDestination).ToList();
                    AddCandidate(candidates, fullChain, RouteSource.Stitched,
                        Older(path.LastSeen, frontier.OldestLastSeen), Math.Min(path.Count, frontier.MinCount));
                }

                // Keep exploring backward regardless, in case a shorter or fresher route exists.
                if (selfIndex == 0)
                    continue;

                var predecessor = path.Hops[selfIndex - 1];
                var predecessorKey = predecessor.Pubkey;
                if (string.IsNullOrEmpty(predecessorKey) || !visited.Add(predecessorKey))
                    continue;

                var extendedChain = new List<RouteHop> { ToHop(predecessor) };
                extendedChain.AddRange(frontier.ChainToDestination);
                queue.Enqueue(new Frontier(
                    predecessorKey,
                    extendedChain,
                    Math.Min(path.Count, frontier.MinCount),
                    Older(path.LastSeen, frontier.OldestLastSeen)));
            }
        }

        if (candidates.Count == 0)
        {
            var computed = await TryComputedRouteAsync(fromPubkey, toPubkey, maxHops, ct);
            if (computed is not null)
                candidates.Add(computed);
        }

        return DeduplicateByHopSequence(candidates)
            .OrderBy(c => c.Source)
            .ThenBy(c => c.HopCount)
            .ThenByDescending(c => c.MinObservationCount)
            .ThenByDescending(c => c.LastSeen ?? DateTimeOffset.MinValue)
            .Take(maxCandidates)
            .ToList();
    }

    /// <summary>
    /// Several path records can describe the exact same hop sequence (e.g. the same direct
    /// edge, logged separately per occurrence) - collapse those into one, keeping the freshest
    /// lastSeen and highest observation count seen for it.
    /// </summary>
    private static IEnumerable<RouteCandidate> DeduplicateByHopSequence(List<RouteCandidate> candidates)
    {
        var bySequence = new Dictionary<string, RouteCandidate>();
        foreach (var candidate in candidates)
        {
            var key = string.Join(">", candidate.Hops.Select(h => h.Pubkey.ToLowerInvariant()));
            if (!bySequence.TryGetValue(key, out var existing))
            {
                bySequence[key] = candidate;
                continue;
            }

            bySequence[key] = new RouteCandidate
            {
                Hops = existing.Hops,
                Source = (RouteSource)Math.Min((int)existing.Source, (int)candidate.Source),
                LastSeen = Newer(existing.LastSeen, candidate.LastSeen),
                MinObservationCount = existing.MinObservationCount + candidate.MinObservationCount,
                Note = existing.Note ?? candidate.Note,
            };
        }

        return bySequence.Values;
    }

    private static DateTimeOffset? Newer(DateTimeOffset? a, DateTimeOffset? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a > b ? a : b;
    }

    /// <summary>
    /// Fallback when no transmission has ever been observed carrying traffic all the way from
    /// origin to destination: walk CoreScope's per-node neighbor-link graph (reach) instead.
    /// This finds a route that is RF-plausible (real bidirectional links exist along it) but
    /// never confirmed end-to-end - clearly flagged as such via RouteSource.Computed.
    /// </summary>
    private async Task<RouteCandidate?> TryComputedRouteAsync(string fromPubkey, string toPubkey, int maxHops, CancellationToken ct)
    {
        var cameFrom = new Dictionary<string, (string Prev, ReachLink Link)>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fromPubkey };
        var queue = new Queue<(string Pubkey, int Depth)>();
        queue.Enqueue((fromPubkey, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= maxHops)
                continue;

            var reach = await _client.GetNodeReachAsync(current, ct: ct);
            if (reach is null)
                continue;

            foreach (var link in reach.Links.OrderByDescending(l => l.Bottleneck))
            {
                if (!visited.Add(link.Pubkey))
                    continue;

                cameFrom[link.Pubkey] = (current, link);
                if (string.Equals(link.Pubkey, toPubkey, StringComparison.OrdinalIgnoreCase))
                {
                    return BuildComputedRoute(fromPubkey, toPubkey, cameFrom);
                }

                queue.Enqueue((link.Pubkey, depth + 1));
            }
        }

        return null;
    }

    private static RouteCandidate BuildComputedRoute(
        string fromPubkey, string toPubkey, Dictionary<string, (string Prev, ReachLink Link)> cameFrom)
    {
        var hops = new List<RouteHop> { new() { Pubkey = toPubkey } };
        var minBottleneck = int.MaxValue;
        var node = toPubkey;
        while (!string.Equals(node, fromPubkey, StringComparison.OrdinalIgnoreCase))
        {
            var (prev, link) = cameFrom[node];
            minBottleneck = Math.Min(minBottleneck, link.Bottleneck);
            hops.Add(new RouteHop { Pubkey = prev, Name = null });
            node = prev;
        }
        hops.Reverse();

        return new RouteCandidate
        {
            Hops = hops,
            Source = RouteSource.Computed,
            LastSeen = null,
            MinObservationCount = minBottleneck == int.MaxValue ? 0 : minBottleneck,
            Note = "No transmission has ever been observed end-to-end along this route. " +
                   "It's stitched from pairwise RF link data (CoreScope's 'reach' reports) " +
                   "between consecutive hops, so treat it as a plausible candidate, not a confirmed path.",
        };
    }

    private async Task<NodePathsResponse?> GetPathsCachedAsync(string pubkey, CancellationToken ct)
    {
        if (_pathsCache.TryGetValue(pubkey, out var cached))
            return cached;

        var result = await _client.GetNodePathsAsync(pubkey, ct);
        _pathsCache[pubkey] = result;
        return result;
    }

    private static bool Matches(PathHop hop, string pubkey) =>
        string.Equals(hop.Pubkey, pubkey, StringComparison.OrdinalIgnoreCase);

    /// <summary>Guards against ever surfacing a route that revisits the same node twice.</summary>
    private static void AddCandidate(
        List<RouteCandidate> candidates, List<RouteHop> hops, RouteSource source, DateTimeOffset? lastSeen, int minCount)
    {
        var distinctPubkeys = hops.Select(h => h.Pubkey.ToLowerInvariant()).Distinct().Count();
        if (distinctPubkeys != hops.Count)
            return;

        candidates.Add(new RouteCandidate
        {
            Hops = hops,
            Source = source,
            LastSeen = lastSeen,
            MinObservationCount = minCount,
        });
    }

    private static RouteHop ToHop(PathHop hop) => new()
    {
        Pubkey = hop.Pubkey ?? hop.Prefix,
        Name = hop.Name,
        Lat = hop.Lat,
        Lon = hop.Lon,
    };

    private static DateTimeOffset? Older(DateTimeOffset? a, DateTimeOffset? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a < b ? a : b;
    }

    private sealed record Frontier(string Pubkey, List<RouteHop> ChainToDestination, int MinCount, DateTimeOffset? OldestLastSeen);

    /// <summary>
    /// Picks one recommended route out of several candidates. Confirmed (Observed) routes always
    /// beat Stitched or Computed ones, but within the same tier this balances freshness against
    /// hop count rather than just taking the shortest: a route nobody has used in weeks is a
    /// worse bet than one with an extra hop that saw traffic an hour ago, since mesh topology
    /// drifts (nodes move, go offline, change antennas) and recency is the best signal that a
    /// route still actually works. Each extra hop costs roughly two days' worth of staleness,
    /// so hop count still wins when recency is close.
    /// </summary>
    public static RouteSearchResult SelectBest(List<RouteCandidate> candidates)
    {
        if (candidates.Count == 0)
            return new RouteSearchResult();

        var ranked = candidates.OrderBy(BestScore).ToList();
        var best = ranked[0];
        return new RouteSearchResult { Best = best, Alternatives = ranked.Skip(1).ToList() };
    }

    private static double BestScore(RouteCandidate c)
    {
        const double HoursPerHopPenalty = 48;
        const double MaxRecencyHours = 9999;

        var recencyHours = c.LastSeen is { } seen
            ? Math.Clamp((DateTimeOffset.UtcNow - seen).TotalHours, 0, MaxRecencyHours)
            : MaxRecencyHours;

        return (int)c.Source * 100_000
            + recencyHours
            + c.HopCount * HoursPerHopPenalty
            - Math.Min(c.MinObservationCount, 50) * 2;
    }
}
