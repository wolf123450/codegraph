using System.Text.Json;
using CodeGraph.Extractor;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

if (args.Length < 1 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("""
        usage: codegraph-extract <path/to/Game.csproj|.sln> [options]
          --root <dir>     Godot project root (default: directory of the csproj/sln).
                           Used for .tscn/project.godot scanning and relative paths.
          --out <file>     Output path (default: graph.json)
          --config <file>  Optional codegraph.config.json (roles/groups overrides)
        """);
    return 1;
}

var inputPath = Path.GetFullPath(args[0]);
string? Opt(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var root = Path.GetFullPath(Opt("--root") ?? Path.GetDirectoryName(inputPath)!);
var outPath = Opt("--out") ?? "graph.json";
var config = Config.Load(Opt("--config"));

// Must run before any Microsoft.CodeAnalysis.MSBuild types are touched.
MSBuildLocator.RegisterDefaults();
return await RunAsync();

async Task<int> RunAsync()
{
    using var workspace = MSBuildWorkspace.Create();
    workspace.WorkspaceFailed += (_, e) =>
        Console.Error.WriteLine($"[workspace] {e.Diagnostic.Kind}: {e.Diagnostic.Message}");

    Console.Error.WriteLine($"Loading {inputPath} ...");
    var projects = new List<Project>();
    if (inputPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
    {
        var solution = await workspace.OpenSolutionAsync(inputPath);
        projects.AddRange(solution.Projects.Where(p => p.Language == LanguageNames.CSharp));
    }
    else
    {
        projects.Add(await workspace.OpenProjectAsync(inputPath));
    }

    var graph = new GraphBuilder();
    var extractor = new RoslynExtractor(graph, root);

    foreach (var p in projects)
    {
        Console.Error.WriteLine($"  pass 1 (types): {p.Name}");
        await extractor.CollectTypesAsync(p);
    }
    foreach (var p in projects)
    {
        Console.Error.WriteLine($"  pass 2 (edges): {p.Name}");
        await extractor.ExtractEdgesAsync(p);
    }

    Console.Error.WriteLine("  godot: scenes + autoloads");
    var godot = new GodotProjectParser(graph, root);
    godot.Parse();

    new Classifier(config, godot.AutoloadTypeIds).Classify(graph);

    var payload = graph.ToSerializable(root);
    await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(payload, GraphBuilder.JsonOptions));
    Console.Error.WriteLine($"Wrote {outPath}: {graph.Nodes.Count} nodes.");
    return 0;
}
