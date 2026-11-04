namespace CodeGraph.Extractor;

/// <summary>
/// Assigns node.Role and node.Groups. Precedence: explicit config override >
/// path-prefix rule > heuristic. Grouping defaults to a directory path segment,
/// plus (GroupNestDepth) nested groups for the directories below it, so a node
/// in src/systems/shipgen/ is in both "systems" and "systems/shipgen". A config
/// override replaces the heuristic groups and can list several groups at once.
/// </summary>
public sealed class Classifier
{
    private readonly Config _config;
    private readonly HashSet<string> _autoloads;

    public Classifier(Config config, HashSet<string> autoloadTypeIds)
    {
        _config = config;
        _autoloads = autoloadTypeIds;
    }

    public void Classify(GraphBuilder graph)
    {
        foreach (var node in graph.Nodes.Values)
        {
            node.Groups.Clear();
            if (node.Kind == "scene")
            {
                node.Groups.UnionWith(ResolveGroups(node.Id, () => DirectoryGroups(node.File)));
                continue;
            }
            node.Role = ResolveRole(node);
            node.Groups.UnionWith(ResolveGroups(node.Id, () => ResolveGroup(node)));
        }
        PruneNestedGroups(graph);
    }

    private IEnumerable<string> ResolveGroups(string nodeId, Func<IEnumerable<string>> heuristic)
    {
        if (_config.Groups.TryGetValue(nodeId, out var over)) return over;
        var groups = heuristic().ToList();
        _nested.UnionWith(groups.Where(g => g.Contains('/')));
        return groups;
    }

    // heuristic nested groups seen this run (config-override groups are never pruned)
    private readonly HashSet<string> _nested = new();

    /// Drops nested groups that are too small, or identical in membership to
    /// their parent (a directory whose only content is one subdirectory).
    private void PruneNestedGroups(GraphBuilder graph)
    {
        var members = new Dictionary<string, HashSet<string>>();
        foreach (var node in graph.Nodes.Values)
            foreach (var g in node.Groups)
                (members.TryGetValue(g, out var s) ? s : members[g] = new()).Add(node.Id);

        // deepest first, so a dropped child doesn't count toward an identical-parent check
        var drop = new HashSet<string>();
        foreach (var g in _nested.Where(members.ContainsKey).OrderByDescending(g => g.Count(c => c == '/')))
        {
            var parent = g[..g.LastIndexOf('/')];
            if (members[g].Count < _config.MinNestedGroupSize ||
                (members.TryGetValue(parent, out var p) && p.SetEquals(members[g])))
                drop.Add(g);
        }
        if (drop.Count == 0) return;
        foreach (var node in graph.Nodes.Values) node.Groups.ExceptWith(drop);
    }

    private string ResolveRole(GraphNode node)
    {
        if (_config.Roles.TryGetValue(node.Id, out var over)) return over;

        if (node.File is not null)
            foreach (var (prefix, role) in _config.RolePathPrefixes)
                if (node.File.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return role;

        if (_autoloads.Contains(node.Id)) return "manager";
        if (node.Kind == "interface") return "interface";
        if (node.Kind == "enum") return "data";
        return node.GodotBase switch
        {
            "Resource" => "data",
            "Control" => "ui",
            "Node" => "object",
            _ => node.IsStatic ? "utility" : "plain",
        };
    }

    private IEnumerable<string> ResolveGroup(GraphNode node)
    {
        if (_config.GroupBy == "namespace")
        {
            var lastDot = node.Id.LastIndexOf('.');
            return new[] { lastDot > 0 ? node.Id[..lastDot] : "(global)" };
        }
        return DirectoryGroups(node.File);
    }

    /// The GroupDepth directory segment, then up to GroupNestDepth deeper
    /// directories as path-named nested groups ("systems/shipgen").
    private IEnumerable<string> DirectoryGroups(string? file)
    {
        if (file is null) return new[] { "(none)" };
        var segments = file.Split('/');
        if (segments.Length <= 1) return new[] { "(root)" };
        var depth = Math.Clamp(_config.GroupDepth, 0, segments.Length - 2);
        var groups = new List<string> { segments[depth] };
        var dirCount = segments.Length - 1; // last segment is the file name
        for (var d = depth + 1; d < dirCount && d - depth <= _config.GroupNestDepth; d++)
            groups.Add(string.Join('/', segments[depth..(d + 1)]));
        return groups;
    }
}
