namespace CodeGraph.Extractor;

/// <summary>
/// Assigns node.Role and node.Groups. Precedence: explicit config override >
/// path-prefix rule > heuristic. Grouping defaults to a directory path segment.
/// A node normally gets exactly one heuristic-derived group, but a config
/// override can put it in several at once.
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
                node.Groups.UnionWith(ResolveGroups(node.Id, () => DirectoryGroup(node.File)));
                continue;
            }
            node.Role = ResolveRole(node);
            node.Groups.UnionWith(ResolveGroups(node.Id, () => ResolveGroup(node)));
        }
    }

    private IEnumerable<string> ResolveGroups(string nodeId, Func<string> heuristic) =>
        _config.Groups.TryGetValue(nodeId, out var over) ? over : new[] { heuristic() };

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

    private string ResolveGroup(GraphNode node)
    {
        if (_config.GroupBy == "namespace")
        {
            var lastDot = node.Id.LastIndexOf('.');
            return lastDot > 0 ? node.Id[..lastDot] : "(global)";
        }
        return DirectoryGroup(node.File);
    }

    private string DirectoryGroup(string? file)
    {
        if (file is null) return "(none)";
        var segments = file.Split('/');
        if (segments.Length <= 1) return "(root)";
        var depth = Math.Clamp(_config.GroupDepth, 0, segments.Length - 2);
        return segments[depth];
    }
}
