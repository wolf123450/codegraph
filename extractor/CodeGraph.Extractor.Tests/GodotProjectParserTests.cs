using CodeGraph.Extractor;

namespace CodeGraph.Extractor.Tests;

public sealed class TempProjectDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codegraph-test-" + Guid.NewGuid());

    public TempProjectDir() => Directory.CreateDirectory(Path);

    public void WriteFile(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

public class GodotProjectParserTests
{
    [Fact]
    public void ParseCreatesSceneAttachEdgeForScriptedNode()
    {
        using var dir = new TempProjectDir();
        var graph = new GraphBuilder();
        // Simulate a Roslyn pass having already registered the script's type.
        graph.GetOrAddNode("Player", "Player", "class");
        graph.FileToTypeId["Player.cs"] = "Player";

        dir.WriteFile("Level1.tscn", """
            [gd_scene load_steps=2 format=3]

            [ext_resource type="Script" path="res://Player.cs" id="1_abc"]

            [node name="Player" type="CharacterBody2D"]
            script = ExtResource("1_abc")
            """);

        new GodotProjectParser(graph, dir.Path).Parse();

        var sceneNode = Assert.Single(graph.Nodes.Values, n => n.Kind == "scene");
        Assert.Equal("Level1", sceneNode.Label);
        var payload = graph.ToSerializable(dir.Path);
        Assert.Contains(payload.Edges, e => e.From == sceneNode.Id && e.To == "Player" && e.Kind == EdgeKind.SceneAttach);
        Assert.Contains("Level1.tscn", graph.Nodes["Player"].Scenes);
    }

    [Fact]
    public void ParseSkipsScenesWithNoResolvedScriptsAndNoConnections()
    {
        using var dir = new TempProjectDir();
        var graph = new GraphBuilder();

        dir.WriteFile("Empty.tscn", """
            [gd_scene load_steps=1 format=3]

            [node name="Root" type="Node2D"]
            """);

        new GodotProjectParser(graph, dir.Path).Parse();

        Assert.Empty(graph.Nodes);
    }

    [Fact]
    public void ParseCreatesSignalEdgeFromConnectionListenerToDeclarer()
    {
        using var dir = new TempProjectDir();
        var graph = new GraphBuilder();
        graph.GetOrAddNode("Button", "Button", "class");
        graph.GetOrAddNode("Hud", "Hud", "class");
        graph.FileToTypeId["Button.cs"] = "Button";
        graph.FileToTypeId["Hud.cs"] = "Hud";

        dir.WriteFile("Ui.tscn", """
            [gd_scene load_steps=3 format=3]

            [ext_resource type="Script" path="res://Hud.cs" id="1_hud"]
            [ext_resource type="Script" path="res://Button.cs" id="2_btn"]

            [node name="Hud" type="Control"]
            script = ExtResource("1_hud")

            [node name="StartButton" type="Button" parent="."]
            script = ExtResource("2_btn")

            [connection signal="pressed" from="StartButton" to="." method="_on_start_button_pressed"]
            """);

        new GodotProjectParser(graph, dir.Path).Parse();

        var payload = graph.ToSerializable(dir.Path);
        // Listener (Hud, the "to" root) -> declarer (Button), per the C#-side convention.
        Assert.Contains(payload.Edges, e =>
            e.From == "Hud" && e.To == "Button" && e.Kind == EdgeKind.Signal && e.Signal == "Pressed");
    }

    [Fact]
    public void ParseLogsUnresolvedConnectionAndSkipsIt()
    {
        using var dir = new TempProjectDir();
        var graph = new GraphBuilder();
        graph.GetOrAddNode("Hud", "Hud", "class");
        graph.FileToTypeId["Hud.cs"] = "Hud";

        dir.WriteFile("Ui.tscn", """
            [gd_scene load_steps=2 format=3]

            [ext_resource type="Script" path="res://Hud.cs" id="1_hud"]

            [node name="Hud" type="Control"]
            script = ExtResource("1_hud")

            [node name="StartButton" type="Button" parent="."]

            [connection signal="pressed" from="StartButton" to="." method="_on_start_button_pressed"]
            """);

        var originalError = Console.Error;
        try
        {
            using var sw = new StringWriter();
            Console.SetError(sw);

            new GodotProjectParser(graph, dir.Path).Parse();

            Assert.Contains("skipped connection", sw.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }

        var payload = graph.ToSerializable(dir.Path);
        Assert.DoesNotContain(payload.Edges, e => e.Kind == EdgeKind.Signal);
    }

    [Fact]
    public void ParseResolvesAutoloadTypeIdsFromProjectGodot()
    {
        using var dir = new TempProjectDir();
        var graph = new GraphBuilder();
        graph.GetOrAddNode("GameManager", "GameManager", "class");
        graph.FileToTypeId["GameManager.cs"] = "GameManager";

        dir.WriteFile("project.godot", """
            [application]

            config/name="Game"

            [autoload]

            GameManager="*res://GameManager.cs"
            """);

        var parser = new GodotProjectParser(graph, dir.Path);
        parser.Parse();

        Assert.Contains("GameManager", parser.AutoloadTypeIds);
    }

    [Fact]
    public void ParseIgnoresScenesUnderDotGodotDirectory()
    {
        using var dir = new TempProjectDir();
        var graph = new GraphBuilder();
        graph.GetOrAddNode("Player", "Player", "class");
        graph.FileToTypeId["Player.cs"] = "Player";

        dir.WriteFile(System.IO.Path.Combine(".godot", "imported", "Cached.tscn"), """
            [gd_scene load_steps=2 format=3]

            [ext_resource type="Script" path="res://Player.cs" id="1_abc"]

            [node name="Player" type="CharacterBody2D"]
            script = ExtResource("1_abc")
            """);

        new GodotProjectParser(graph, dir.Path).Parse();

        Assert.DoesNotContain(graph.Nodes.Values, n => n.Kind == "scene");
    }

    [Fact]
    public void ParseSkipsScenesUnderAConfiguredExcludePrefix()
    {
        using var dir = new TempProjectDir();
        var graph = new GraphBuilder();
        graph.GetOrAddNode("GdUnitPanel", "GdUnitPanel", "class");
        graph.FileToTypeId["addons/gdUnit4/Panel.cs"] = "GdUnitPanel";

        dir.WriteFile(System.IO.Path.Combine("addons", "gdUnit4", "Panel.tscn"), """
            [gd_scene load_steps=2 format=3]

            [ext_resource type="Script" path="res://addons/gdUnit4/Panel.cs" id="1_abc"]

            [node name="Panel" type="Control"]
            script = ExtResource("1_abc")
            """);

        var config = new Config { Exclude = { "addons/gdUnit4/" } };
        new GodotProjectParser(graph, dir.Path, config).Parse();

        Assert.DoesNotContain(graph.Nodes.Values, n => n.Kind == "scene");
    }
}
