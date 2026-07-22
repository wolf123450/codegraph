using System.Text.Json;
using CodeGraph.Extractor;

namespace CodeGraph.Extractor.Tests;

public class GraphPayloadSerializationTests
{
    [Fact]
    public void SerializesMetaNodesEdgesAsCamelCase()
    {
        var graph = new GraphBuilder();
        graph.GetOrAddNode("A", "A", "class");
        graph.GetOrAddNode("B", "B", "class");
        graph.AddEdge("A", "B", EdgeKind.Calls);

        var json = JsonSerializer.Serialize(graph.ToSerializable("/root"), GraphBuilder.JsonOptions);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("meta", out var meta));
        Assert.Equal(1, meta.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("/root", meta.GetProperty("projectRoot").GetString());
        Assert.True(doc.RootElement.TryGetProperty("nodes", out _));
        Assert.True(doc.RootElement.TryGetProperty("edges", out var edges));
        Assert.Equal(1, edges.GetArrayLength());
    }
}
