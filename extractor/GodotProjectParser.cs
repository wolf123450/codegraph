using System.Text.RegularExpressions;

namespace CodeGraph.Extractor;

/// <summary>
/// Best-effort text parsing of the Godot side of the project:
///  - .tscn files: which scripts are attached to which scenes (sceneAttach edges),
///    and editor-made signal connections ([connection] sections -> signal edges)
///  - project.godot: [autoload] entries, surfaced for the Classifier.
/// The .tscn format is a simple sectioned text format; regex is adequate here
/// and avoids a dependency. Connections are only resolved between nodes whose
/// scripts live in the SAME scene file; instanced sub-scene targets are skipped.
/// </summary>
public sealed partial class GodotProjectParser
{
    private readonly GraphBuilder _graph;
    private readonly string _root;
    private readonly Config _config;

    public HashSet<string> AutoloadTypeIds { get; } = new();

    public GodotProjectParser(GraphBuilder graph, string projectRoot, Config? config = null)
    {
        _graph = graph;
        _root = Path.GetFullPath(projectRoot);
        _config = config ?? new Config();
    }

    [GeneratedRegex("\\[ext_resource type=\"Script\"[^\\]]*?path=\"res://([^\"]+)\"[^\\]]*?id=\"([^\"]+)\"")]
    private static partial Regex ExtScriptRx();

    [GeneratedRegex("""^\[node name="([^"]+)"[^\]]*\]""", RegexOptions.Multiline)]
    private static partial Regex NodeHeaderRx();

    [GeneratedRegex("""script = ExtResource\("([^"]+)"\)""")]
    private static partial Regex ScriptAssignRx();

    [GeneratedRegex("""\[connection signal="(\w+)" from="([^"]*)" to="([^"]*)" method="\w+"[^\]]*\]""")]
    private static partial Regex ConnectionRx();

    [GeneratedRegex("^\\s*[\\w/]+=\"\\*?res://([^\"]+\\.cs)\"", RegexOptions.Multiline)]
    private static partial Regex AutoloadRx();

    public void Parse()
    {
        foreach (var tscn in Directory.EnumerateFiles(_root, "*.tscn", SearchOption.AllDirectories))
        {
            if (tscn.Contains($"{Path.DirectorySeparatorChar}.godot{Path.DirectorySeparatorChar}")) continue;
            var rel = Path.GetRelativePath(_root, tscn).Replace('\\', '/');
            if (_config.IsExcluded(rel)) continue;
            ParseScene(tscn);
        }

        var projectGodot = Path.Combine(_root, "project.godot");
        if (File.Exists(projectGodot)) ParseAutoloads(projectGodot);
    }

    private void ParseScene(string path)
    {
        var text = File.ReadAllText(path);
        var rel = Path.GetRelativePath(_root, path).Replace('\\', '/');
        var sceneId = $"scene:{rel}";

        // ext_resource id -> script type id
        var scriptsById = new Dictionary<string, string>();
        foreach (Match m in ExtScriptRx().Matches(text))
            if (_graph.FileToTypeId.TryGetValue(m.Groups[1].Value, out var typeId))
                scriptsById[m.Groups[2].Value] = typeId;

        if (scriptsById.Count == 0 && !ConnectionRx().IsMatch(text)) return;

        var scene = _graph.GetOrAddNode(sceneId, Path.GetFileNameWithoutExtension(rel), "scene");
        scene.File = rel;
        scene.Role = "scene";
        var fi = new FileInfo(path);
        scene.Bytes = fi.Length;

        // Walk node sections in order to map node name -> attached script type.
        // The root node's canonical connection path is ".".
        var nodeScripts = new Dictionary<string, string>(); // node name/path -> type id
        string? rootName = null;
        var headers = NodeHeaderRx().Matches(text);
        for (var i = 0; i < headers.Count; i++)
        {
            var name = headers[i].Groups[1].Value;
            rootName ??= name;
            var sectionEnd = i + 1 < headers.Count ? headers[i + 1].Index : text.Length;
            var section = text[headers[i].Index..sectionEnd];
            var scriptMatch = ScriptAssignRx().Match(section);
            if (!scriptMatch.Success || !scriptsById.TryGetValue(scriptMatch.Groups[1].Value, out var typeId))
                continue;
            nodeScripts[name] = typeId;
            if (name == rootName) nodeScripts["."] = typeId;

            var typeNode = _graph.Nodes[typeId];
            typeNode.Scenes.Add(rel);
            _graph.AddEdge(sceneId, typeId, EdgeKind.SceneAttach);
        }

        // Editor-made signal connections. from/to are node paths relative to scene root;
        // we resolve only simple same-scene paths (last path segment as node name).
        foreach (Match m in ConnectionRx().Matches(text))
        {
            var signal = ToPascal(m.Groups[1].Value);
            var fromType = ResolveNodePath(nodeScripts, m.Groups[2].Value);
            var toType = ResolveNodePath(nodeScripts, m.Groups[3].Value);
            if (fromType is null || toType is null)
            {
                Console.Error.WriteLine($"[tscn] {rel}: skipped connection '{m.Groups[1].Value}' " +
                                        $"({m.Groups[2].Value} -> {m.Groups[3].Value}) — unresolved node script");
                continue;
            }
            // Listener -> declarer, matching the C# side's convention.
            _graph.AddEdge(toType, fromType, EdgeKind.Signal, signal);
        }
    }

    private static string? ResolveNodePath(Dictionary<string, string> nodeScripts, string path)
    {
        if (nodeScripts.TryGetValue(path, out var direct)) return direct;
        var last = path.Split('/')[^1];
        return nodeScripts.TryGetValue(last, out var byName) ? byName : null;
    }

    private void ParseAutoloads(string projectGodotPath)
    {
        var text = File.ReadAllText(projectGodotPath);
        var idx = text.IndexOf("[autoload]", StringComparison.Ordinal);
        if (idx < 0) return;
        var end = text.IndexOf("\n[", idx + 1, StringComparison.Ordinal);
        var section = end < 0 ? text[idx..] : text[idx..end];

        foreach (Match m in AutoloadRx().Matches(section))
            if (_graph.FileToTypeId.TryGetValue(m.Groups[1].Value, out var typeId))
                AutoloadTypeIds.Add(typeId);
        // NOTE: autoloaded *scenes* (.tscn) whose root has a script are not resolved here — TODO.
    }

    private static string ToPascal(string snake) =>
        string.Concat(snake.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
}
