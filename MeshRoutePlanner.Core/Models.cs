using System.Text.Json.Serialization;

namespace MeshRoutePlanner.Core;

public class NodeSummary
{
    [JsonPropertyName("public_key")] public required string PublicKey { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("role")] public string? Role { get; init; }
    [JsonPropertyName("lat")] public double? Lat { get; init; }
    [JsonPropertyName("lon")] public double? Lon { get; init; }
    [JsonPropertyName("last_seen")] public DateTimeOffset? LastSeen { get; init; }
    [JsonPropertyName("first_seen")] public DateTimeOffset? FirstSeen { get; init; }
    [JsonPropertyName("advert_count")] public int AdvertCount { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? PublicKey[..8] : Name;
}

public class NodeSearchResponse
{
    [JsonPropertyName("nodes")] public List<NodeSummary> Nodes { get; init; } = [];
}

public class NodeListResponse
{
    [JsonPropertyName("nodes")] public List<NodeSummary> Nodes { get; init; } = [];
    // "total" is NOT a stable grand total despite the docs - in practice it just mirrors
    // however many nodes this page returned, so pagination relies on "has_more" instead.
    [JsonPropertyName("has_more")] public bool HasMore { get; init; }
}

public class NodeDetailResponse
{
    [JsonPropertyName("node")] public NodeSummary? Node { get; init; }
}

public class PathHop
{
    [JsonPropertyName("prefix")] public required string Prefix { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("pubkey")] public string? Pubkey { get; init; }
    [JsonPropertyName("lat")] public double? Lat { get; init; }
    [JsonPropertyName("lon")] public double? Lon { get; init; }
}

public class PathEntry
{
    [JsonPropertyName("hops")] public List<PathHop> Hops { get; init; } = [];
    [JsonPropertyName("count")] public int Count { get; init; }
    [JsonPropertyName("lastSeen")] public DateTimeOffset? LastSeen { get; init; }
    [JsonPropertyName("sampleHash")] public string? SampleHash { get; init; }
}

public class NodePathsResponse
{
    [JsonPropertyName("node")] public NodeRef? Node { get; init; }
    [JsonPropertyName("paths")] public List<PathEntry> Paths { get; init; } = [];
    [JsonPropertyName("totalPaths")] public int TotalPaths { get; init; }
    [JsonPropertyName("totalTransmissions")] public int TotalTransmissions { get; init; }
}

public class NodeRef
{
    [JsonPropertyName("public_key")] public string? PublicKey { get; init; }
    [JsonPropertyName("pubkey")] public string? Pubkey { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("lat")] public double? Lat { get; init; }
    [JsonPropertyName("lon")] public double? Lon { get; init; }
}

public class ReachLink
{
    [JsonPropertyName("pubkey")] public required string Pubkey { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("role")] public string? Role { get; init; }
    [JsonPropertyName("lat")] public double? Lat { get; init; }
    [JsonPropertyName("lon")] public double? Lon { get; init; }
    [JsonPropertyName("we_hear")] public int WeHear { get; init; }
    [JsonPropertyName("they_hear")] public int TheyHear { get; init; }
    [JsonPropertyName("bottleneck")] public int Bottleneck { get; init; }
    [JsonPropertyName("bidir")] public bool Bidirectional { get; init; }
    [JsonPropertyName("distance_km")] public double? DistanceKm { get; init; }
}

public class NodeReachResponse
{
    [JsonPropertyName("node")] public NodeRef? Node { get; init; }
    [JsonPropertyName("reliable_tokens")] public List<string> ReliableTokens { get; init; } = [];
    [JsonPropertyName("links")] public List<ReachLink> Links { get; init; } = [];
}

public enum RouteSource
{
    /// <summary>A literal path CoreScope has actually seen carry traffic end-to-end.</summary>
    Observed,

    /// <summary>Stitched together from several observed path fragments that overlap at a shared node.</summary>
    Stitched,

    /// <summary>Derived from pairwise neighbor-link data (reach), never confirmed as a full end-to-end route.</summary>
    Computed,
}

public class RouteHop
{
    public required string Pubkey { get; init; }
    public string? Name { get; set; }
    public double? Lat { get; set; }
    public double? Lon { get; set; }
}

public class RouteCandidate
{
    public required List<RouteHop> Hops { get; init; }
    public required RouteSource Source { get; init; }
    /// <summary>Most recent time any segment of this route was observed, if known.</summary>
    public DateTimeOffset? LastSeen { get; init; }
    /// <summary>How many times the weakest segment of this route has been observed.</summary>
    public int MinObservationCount { get; init; }
    public string? Note { get; init; }

    public int HopCount => Math.Max(0, Hops.Count - 1);
}

public class RouteSearchResult
{
    /// <summary>The single recommended route, or null when nothing was found at all.</summary>
    public RouteCandidate? Best { get; init; }
    /// <summary>Every other candidate worth showing, best-first, excluding <see cref="Best"/>.</summary>
    public List<RouteCandidate> Alternatives { get; init; } = [];
}
