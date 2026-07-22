using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeGraph.Extractor;

public static class EdgeKind
{
    public const string Inherits    = "inherits";
    public const string Implements  = "implements";
    public const string Calls       = "calls";
    public const string Composes    = "composes";
    public const string Signal      = "signal";
    public const string SceneAttach = "sceneAttach";
    public const string NodeRef     = "nodeRef";
}

public sealed class GraphNode
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Kind { get; set; }          // class|interface|struct|enum|scene
    public string? File { get; set; }
    public long Bytes { get; set; }
    public int Loc { get; set; }
    public string Role { get; set; } = "plain";
    public string Group { get; set; } = "";
    public bool IsStatic { get; set; }
    public string? BaseType { get; set; }
    public string? GodotBase { get; set; }
    public SortedSet<string> SignalsDeclared { get; } = new();
    public SortedSet<string> SignalsEmitted { get; } = new();
    public SortedSet<string> Scenes { get; } = new();

    [JsonIgnore] public List<string> Files { get; } = new(); // partial-class source files
}

public sealed class GraphEdge
{
    public required string From { get; init; }
    public required string To { get; init; }
    public required string Kind { get; init; }
    public int Weight { get; set; } = 1;
    public string? Signal { get; init; }
}

public sealed class GraphMeta
{
    public required int SchemaVersion { get; init; }
    public required string GeneratedAt { get; init; }
    public required string ProjectRoot { get; init; }
}

public sealed class GraphPayload
{
    public required GraphMeta Meta { get; init; }
    public required List<GraphNode> Nodes { get; init; }
    public required List<GraphEdge> Edges { get; init; }
}

public sealed class GraphBuilder
{
    public Dictionary<string, GraphNode> Nodes { get; } = new();
    private readonly Dictionary<string, GraphEdge> _edges = new();

    /// Map of normalized relative source path -> type node id (first type wins),
    /// used to join .tscn script paths back to Roslyn types.
    public Dictionary<string, string> FileToTypeId { get; } = new(StringComparer.OrdinalIgnoreCase);

    public GraphNode GetOrAddNode(string id, string label, string kind)
    {
        if (!Nodes.TryGetValue(id, out var n))
        {
            n = new GraphNode { Id = id, Label = label, Kind = kind };
            Nodes[id] = n;
        }
        return n;
    }

    public void AddEdge(string from, string to, string kind, string? signal = null)
    {
        if (from == to && kind != EdgeKind.Signal) return; // self loops add noise
        var key = $"{from}|{to}|{kind}|{signal}";
        if (_edges.TryGetValue(key, out var e)) e.Weight++;
        else _edges[key] = new GraphEdge { From = from, To = to, Kind = kind, Signal = signal };
    }

    public GraphPayload ToSerializable(string projectRoot)
    {
        // Drop edges whose endpoints didn't materialize as nodes.
        var edges = _edges.Values
            .Where(e => Nodes.ContainsKey(e.From) && Nodes.ContainsKey(e.To))
            .OrderBy(e => e.From).ThenBy(e => e.To).ThenBy(e => e.Kind)
            .ToList();

        return new GraphPayload
        {
            Meta = new GraphMeta
            {
                SchemaVersion = 1,
                GeneratedAt = DateTime.UtcNow.ToString("o"),
                ProjectRoot = projectRoot,
            },
            Nodes = Nodes.Values.OrderBy(n => n.Id).ToList(),
            Edges = edges,
        };
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };
}

public sealed class Config
{
    public string GroupBy { get; set; } = "directory";   // directory | namespace
    public int GroupDepth { get; set; } = 1;
    public Dictionary<string, string> Roles { get; set; } = new();
    public Dictionary<string, string> Groups { get; set; } = new();
    public Dictionary<string, string> RolePathPrefixes { get; set; } = new();

    /// Path prefixes (relative, forward-slash, case-insensitive) to leave out entirely --
    /// no node, no edges, no .tscn scan. Matched against the same relative paths used
    /// elsewhere (e.g. "test/", "addons/gdUnit4/").
    public List<string> Exclude { get; set; } = new();

    public bool IsExcluded(string relPath) =>
        Exclude.Any(p => relPath.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static Config Load(string? path)
    {
        if (path is null || !File.Exists(path)) return new Config();
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        return JsonSerializer.Deserialize<Config>(File.ReadAllText(path), opts) ?? new Config();
    }
}
