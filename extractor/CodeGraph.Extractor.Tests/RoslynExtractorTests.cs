using System.Linq;
using CodeGraph.Extractor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeGraph.Extractor.Tests;

/// Builds an in-memory Roslyn Project from source snippets, without MSBuildWorkspace
/// or real Godot assemblies. GodotBase detection only matches on base-type *names*
/// (see RoslynExtractor.NearestGodotBase), so a local stub named "Node"/"Resource"/
/// etc. is sufficient stand-in for GodotObject/Node/Resource/Control/RefCounted.
public static class RoslynTestProject
{
    private const string GodotStubs = """
        namespace Godot {
            public class GodotObject {}
            public class RefCounted : GodotObject {}
            public class Node : GodotObject {}
            public class Node2D : Node {}
            public class CanvasItem : Node {}
            public class Control : CanvasItem {}
            public class Resource : RefCounted {}
            public class SignalAttribute : System.Attribute {}
            namespace SignalName {}
        }
        """;

    public static Project Create(params string[] sources)
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Attribute).Assembly.Location),
            MetadataReference.CreateFromFile(
                System.Reflection.Assembly.Load("System.Runtime").Location),
        };

        var projectInfo = ProjectInfo.Create(
            projectId, VersionStamp.Default, "TestProject", "TestProject", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: refs);

        var solution = workspace.CurrentSolution.AddProject(projectInfo);
        solution = solution.AddDocument(DocumentId.CreateNewId(projectId), "Godot.cs", GodotStubs,
            filePath: "/root/Godot.cs");
        for (var i = 0; i < sources.Length; i++)
            solution = solution.AddDocument(DocumentId.CreateNewId(projectId), $"Source{i}.cs", sources[i],
                filePath: $"/root/Source{i}.cs");

        return solution.GetProject(projectId)!;
    }

    /// Like Create, but lets each source specify its own path relative to "/root".
    public static Project CreateAtPaths(params (string relativePath, string source)[] files)
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Attribute).Assembly.Location),
            MetadataReference.CreateFromFile(
                System.Reflection.Assembly.Load("System.Runtime").Location),
        };

        var projectInfo = ProjectInfo.Create(
            projectId, VersionStamp.Default, "TestProject", "TestProject", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: refs);

        var solution = workspace.CurrentSolution.AddProject(projectInfo);
        solution = solution.AddDocument(DocumentId.CreateNewId(projectId), "Godot.cs", GodotStubs,
            filePath: "/root/Godot.cs");
        foreach (var (relativePath, source) in files)
            solution = solution.AddDocument(DocumentId.CreateNewId(projectId), Path.GetFileName(relativePath), source,
                filePath: $"/root/{relativePath}");

        return solution.GetProject(projectId)!;
    }
}

public class RoslynExtractorTests
{
    private static async Task<GraphBuilder> ExtractAsync(params string[] sources)
    {
        var project = RoslynTestProject.Create(sources);
        var graph = new GraphBuilder();
        var extractor = new RoslynExtractor(graph, "/root");
        await extractor.CollectTypesAsync(project);
        await extractor.ExtractEdgesAsync(project);
        return graph;
    }

    [Fact]
    public async Task CollectsClassAsNode()
    {
        var graph = await ExtractAsync("public class Player {}");

        Assert.Contains(graph.Nodes.Values, n => n.Label == "Player" && n.Kind == "class");
    }

    [Fact]
    public async Task ExcludedPathIsSkippedEntirelyInBothPasses()
    {
        var project = RoslynTestProject.CreateAtPaths(
            ("Player.cs", "public class Player {}"),
            ("test/PlayerCaptureTest.cs", """
                public class PlayerCaptureTest
                {
                    public void Run() { new Player(); }
                }
                """));
        var graph = new GraphBuilder();
        var config = new Config { Exclude = { "test/" } };
        var extractor = new RoslynExtractor(graph, "/root", config);
        await extractor.CollectTypesAsync(project);
        await extractor.ExtractEdgesAsync(project);

        Assert.Contains(graph.Nodes.Values, n => n.Label == "Player");
        Assert.DoesNotContain(graph.Nodes.Values, n => n.Label == "PlayerCaptureTest");
        // Nothing under test/ should even show up as a caller of Player.
        var payload = graph.ToSerializable("/root");
        Assert.DoesNotContain(payload.Edges, e => e.Kind == EdgeKind.Calls);
    }

    [Fact]
    public async Task CollectsInterfaceStructEnumWithCorrectKind()
    {
        var graph = await ExtractAsync("""
            public interface IDamageable {}
            public struct Vec2I { public int X; }
            public enum Team { Red, Blue }
            """);

        Assert.Contains(graph.Nodes.Values, n => n.Label == "IDamageable" && n.Kind == "interface");
        Assert.Contains(graph.Nodes.Values, n => n.Label == "Vec2I" && n.Kind == "struct");
        Assert.Contains(graph.Nodes.Values, n => n.Label == "Team" && n.Kind == "enum");
    }

    [Fact]
    public async Task DetectsNearestGodotBaseThroughInheritanceChain()
    {
        var graph = await ExtractAsync("""
            public class Player : Godot.Node2D {}
            public class Item : Godot.Resource {}
            public class Hud : Godot.Control {}
            """);

        Assert.Equal("Node", graph.Nodes.Values.Single(n => n.Label == "Player").GodotBase);
        Assert.Equal("Resource", graph.Nodes.Values.Single(n => n.Label == "Item").GodotBase);
        Assert.Equal("Control", graph.Nodes.Values.Single(n => n.Label == "Hud").GodotBase);
    }

    [Fact]
    public async Task MarksStaticClassAsStatic()
    {
        var graph = await ExtractAsync("public static class Utils {}");

        Assert.True(graph.Nodes.Values.Single(n => n.Label == "Utils").IsStatic);
    }

    [Fact]
    public async Task InheritsEdgeAddedForBaseClass()
    {
        var graph = await ExtractAsync("""
            public class Enemy {}
            public class Goblin : Enemy {}
            """);

        var goblinId = graph.Nodes.Values.Single(n => n.Label == "Goblin").Id;
        var enemyId = graph.Nodes.Values.Single(n => n.Label == "Enemy").Id;
        var payload = graph.ToSerializable("/root");
        Assert.Contains(payload.Edges, e => e.From == goblinId && e.To == enemyId && e.Kind == EdgeKind.Inherits);
    }

    [Fact]
    public async Task ImplementsEdgeAddedForInterface()
    {
        var graph = await ExtractAsync("""
            public interface IDamageable {}
            public class Goblin : IDamageable {}
            """);

        var goblinId = graph.Nodes.Values.Single(n => n.Label == "Goblin").Id;
        var ifaceId = graph.Nodes.Values.Single(n => n.Label == "IDamageable").Id;
        var payload = graph.ToSerializable("/root");
        Assert.Contains(payload.Edges, e => e.From == goblinId && e.To == ifaceId && e.Kind == EdgeKind.Implements);
    }

    [Fact]
    public async Task ComposesEdgeAddedForFieldAndGenericArgument()
    {
        var graph = await ExtractAsync("""
            using System.Collections.Generic;
            public class ItemData {}
            public class Inventory
            {
                private ItemData _equipped;
                private List<ItemData> _items;
            }
            """);

        var invId = graph.Nodes.Values.Single(n => n.Label == "Inventory").Id;
        var itemId = graph.Nodes.Values.Single(n => n.Label == "ItemData").Id;
        var payload = graph.ToSerializable("/root");
        Assert.Contains(payload.Edges, e => e.From == invId && e.To == itemId && e.Kind == EdgeKind.Composes);
    }

    [Fact]
    public async Task CallsEdgeAddedForMethodInvocation()
    {
        var graph = await ExtractAsync("""
            public class Logger { public void Log() {} }
            public class Player
            {
                public void TakeDamage()
                {
                    new Logger().Log();
                }
            }
            """);

        var playerId = graph.Nodes.Values.Single(n => n.Label == "Player").Id;
        var loggerId = graph.Nodes.Values.Single(n => n.Label == "Logger").Id;
        var payload = graph.ToSerializable("/root");
        Assert.Contains(payload.Edges, e => e.From == playerId && e.To == loggerId && e.Kind == EdgeKind.Calls);
    }

    [Fact]
    public async Task SignalDeclarationRecordedFromSignalAttributeDelegate()
    {
        var graph = await ExtractAsync("""
            public class Player
            {
                [Godot.SignalAttribute]
                public delegate void HealthChangedEventHandler(int amount);
            }
            """);

        var player = graph.Nodes.Values.Single(n => n.Label == "Player");
        Assert.Contains("HealthChanged", player.SignalsDeclared);
    }

    [Fact]
    public async Task EmitSignalStringLiteralRecordsSignalEmitted()
    {
        var graph = await ExtractAsync("""
            public class Player : Godot.GodotObject
            {
                public void Die()
                {
                    EmitSignal("died");
                }
                public void EmitSignal(string name) {}
            }
            """);

        var player = graph.Nodes.Values.Single(n => n.Label == "Player");
        Assert.Contains("Died", player.SignalsEmitted);
    }

    [Fact]
    public async Task EventSubscriptionAddsSignalEdge()
    {
        var graph = await ExtractAsync("""
            public class Publisher { public event System.Action Ready; }
            public class Subscriber
            {
                public void Wire(Publisher p)
                {
                    p.Ready += OnReady;
                }
                private void OnReady() {}
            }
            """);

        var subId = graph.Nodes.Values.Single(n => n.Label == "Subscriber").Id;
        var pubId = graph.Nodes.Values.Single(n => n.Label == "Publisher").Id;
        var payload = graph.ToSerializable("/root");
        Assert.Contains(payload.Edges, e => e.From == subId && e.To == pubId && e.Kind == EdgeKind.Signal && e.Signal == "Ready");
    }

    [Fact]
    public async Task GenericTypedGetNodeAddsNodeRefEdge()
    {
        var graph = await ExtractAsync("""
            public class Hud : Godot.Node {}
            public class Player : Godot.Node
            {
                public void Setup()
                {
                    GetNode<Hud>("Hud");
                }
                public T GetNode<T>(string path) where T : Godot.Node => default!;
            }
            """);

        var playerId = graph.Nodes.Values.Single(n => n.Label == "Player").Id;
        var hudId = graph.Nodes.Values.Single(n => n.Label == "Hud").Id;
        var payload = graph.ToSerializable("/root");
        Assert.Contains(payload.Edges, e => e.From == playerId && e.To == hudId && e.Kind == EdgeKind.NodeRef);
    }
}
