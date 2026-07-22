using CodeGraph.Extractor;

namespace CodeGraph.Extractor.Tests;

public class ClassifierTests
{
    private static Classifier NewClassifier(Config? config = null, params string[] autoloads) =>
        new(config ?? new Config(), new HashSet<string>(autoloads));

    [Fact]
    public void RoleOverrideInConfigWinsOverHeuristic()
    {
        var graph = new GraphBuilder();
        var node = graph.GetOrAddNode("Foo", "Foo", "class");
        node.GodotBase = "Control"; // would heuristically resolve to "ui"

        var config = new Config { Roles = { ["Foo"] = "custom-role" } };
        NewClassifier(config).Classify(graph);

        Assert.Equal("custom-role", node.Role);
    }

    [Theory]
    [InlineData("Resource", "data")]
    [InlineData("Control", "ui")]
    [InlineData("Node", "object")]
    public void GodotBaseDrivesRoleHeuristic(string godotBase, string expectedRole)
    {
        var graph = new GraphBuilder();
        var node = graph.GetOrAddNode("Foo", "Foo", "class");
        node.GodotBase = godotBase;

        NewClassifier().Classify(graph);

        Assert.Equal(expectedRole, node.Role);
    }

    [Fact]
    public void StaticPlainClassIsUtility()
    {
        var graph = new GraphBuilder();
        var node = graph.GetOrAddNode("Foo", "Foo", "class");
        node.IsStatic = true;

        NewClassifier().Classify(graph);

        Assert.Equal("utility", node.Role);
    }

    [Fact]
    public void NonStaticPlainClassIsPlain()
    {
        var graph = new GraphBuilder();
        var node = graph.GetOrAddNode("Foo", "Foo", "class");

        NewClassifier().Classify(graph);

        Assert.Equal("plain", node.Role);
    }

    [Fact]
    public void InterfaceKindGetsInterfaceRole()
    {
        var graph = new GraphBuilder();
        var node = graph.GetOrAddNode("IFoo", "IFoo", "interface");

        NewClassifier().Classify(graph);

        Assert.Equal("interface", node.Role);
    }

    [Fact]
    public void EnumKindGetsDataRole()
    {
        var graph = new GraphBuilder();
        var node = graph.GetOrAddNode("FooEnum", "FooEnum", "enum");

        NewClassifier().Classify(graph);

        Assert.Equal("data", node.Role);
    }

    [Fact]
    public void AutoloadTypeGetsManagerRole()
    {
        var graph = new GraphBuilder();
        var node = graph.GetOrAddNode("Foo", "Foo", "class");

        NewClassifier(autoloads: "Foo").Classify(graph);

        Assert.Equal("manager", node.Role);
    }

    [Fact]
    public void RolePathPrefixWinsOverGodotBaseHeuristic()
    {
        var graph = new GraphBuilder();
        var node = graph.GetOrAddNode("Foo", "Foo", "class");
        node.File = "Enemies/Foo.cs";
        node.GodotBase = "Resource"; // would heuristically resolve to "data"

        var config = new Config { RolePathPrefixes = { ["Enemies/"] = "enemy" } };
        NewClassifier(config).Classify(graph);

        Assert.Equal("enemy", node.Role);
    }

    [Fact]
    public void SceneNodeSkipsRoleAssignmentAndUsesDirectoryGroup()
    {
        var graph = new GraphBuilder();
        var scene = graph.GetOrAddNode("scene:Levels/Level1.tscn", "Level1", "scene");
        scene.File = "Levels/Level1.tscn";

        NewClassifier().Classify(graph);

        Assert.Equal("plain", scene.Role); // untouched default, not reclassified
        Assert.Equal(new[] { "Levels" }, scene.Groups);
    }

    [Fact]
    public void GroupByNamespaceUsesLastSegmentBeforeTypeName()
    {
        var graph = new GraphBuilder();
        var node = graph.GetOrAddNode("Game.Enemies.Goblin", "Goblin", "class");

        var config = new Config { GroupBy = "namespace" };
        NewClassifier(config).Classify(graph);

        Assert.Equal(new[] { "Game.Enemies" }, node.Groups);
    }

    [Fact]
    public void GroupByDirectoryUsesConfiguredDepthSegment()
    {
        var graph = new GraphBuilder();
        var node = graph.GetOrAddNode("Foo", "Foo", "class");
        node.File = "Scripts/Enemies/Goblin.cs";

        var config = new Config { GroupDepth = 1 };
        NewClassifier(config).Classify(graph);

        Assert.Equal(new[] { "Enemies" }, node.Groups);
    }

    [Fact]
    public void GroupOverrideInConfigWinsOverDirectoryHeuristic()
    {
        var graph = new GraphBuilder();
        var node = graph.GetOrAddNode("Foo", "Foo", "class");
        node.File = "Scripts/Enemies/Goblin.cs";

        var config = new Config { Groups = { ["Foo"] = new List<string> { "custom-group" } } };
        NewClassifier(config).Classify(graph);

        Assert.Equal(new[] { "custom-group" }, node.Groups);
    }

    [Fact]
    public void GroupOverrideCanPutANodeInMultipleGroupsAtOnce()
    {
        var graph = new GraphBuilder();
        var node = graph.GetOrAddNode("Foo", "Foo", "class");
        node.File = "Scripts/Enemies/Goblin.cs";

        var config = new Config { Groups = { ["Foo"] = new List<string> { "combat", "core" } } };
        NewClassifier(config).Classify(graph);

        Assert.Equal(new[] { "combat", "core" }, node.Groups); // SortedSet, so alphabetical
    }

    [Fact]
    public void NodeWithNoFileGetsNoneGroup()
    {
        var graph = new GraphBuilder();
        var node = graph.GetOrAddNode("Foo", "Foo", "class");

        NewClassifier().Classify(graph);

        Assert.Equal(new[] { "(none)" }, node.Groups);
    }
}
