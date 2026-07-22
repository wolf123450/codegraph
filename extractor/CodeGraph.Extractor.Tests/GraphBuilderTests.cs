using System.Linq;
using CodeGraph.Extractor;

namespace CodeGraph.Extractor.Tests;

public class GraphBuilderTests
{
    [Fact]
    public void GetOrAddNodeReturnsSameInstanceForRepeatedId()
    {
        var graph = new GraphBuilder();
        var first = graph.GetOrAddNode("A", "A", "class");
        var second = graph.GetOrAddNode("A", "A", "class");

        Assert.Same(first, second);
        Assert.Single(graph.Nodes);
    }

    [Fact]
    public void AddEdgeSkipsSelfLoopsExceptSignal()
    {
        var graph = new GraphBuilder();
        graph.GetOrAddNode("A", "A", "class");
        graph.AddEdge("A", "A", EdgeKind.Calls);
        graph.AddEdge("A", "A", EdgeKind.Signal, "Ready");

        var payload = graph.ToSerializable("/root");
        Assert.Single(payload.Edges);
    }

    [Fact]
    public void AddEdgeIncrementsWeightForDuplicateEdge()
    {
        var graph = new GraphBuilder();
        graph.GetOrAddNode("A", "A", "class");
        graph.GetOrAddNode("B", "B", "class");
        graph.AddEdge("A", "B", EdgeKind.Calls);
        graph.AddEdge("A", "B", EdgeKind.Calls);

        var payload = graph.ToSerializable("/root");
        var edge = payload.Edges.Single();
        Assert.Equal(2, edge.Weight);
    }

    [Fact]
    public void ToSerializableDropsEdgesWithMissingEndpoints()
    {
        var graph = new GraphBuilder();
        graph.GetOrAddNode("A", "A", "class");
        // "B" never added as a node.
        graph.AddEdge("A", "B", EdgeKind.Calls);

        var payload = graph.ToSerializable("/root");
        Assert.Empty(payload.Edges);
    }
}
