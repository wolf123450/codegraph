using CodeGraph.Extractor;

namespace CodeGraph.Extractor.Tests;

public class ConfigTests
{
    [Fact]
    public void LoadWithNullPathReturnsDefaults()
    {
        var config = Config.Load(null);

        Assert.Equal("directory", config.GroupBy);
        Assert.Equal(1, config.GroupDepth);
        Assert.Empty(config.Roles);
    }

    [Fact]
    public void LoadWithMissingFileReturnsDefaults()
    {
        var config = Config.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));

        Assert.Equal("directory", config.GroupBy);
    }

    [Fact]
    public void LoadParsesRolesGroupsAndPrefixesCaseInsensitively()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, """
            {
              "groupBy": "namespace",
              "groupDepth": 2,
              "roles": { "Foo": "enemy" },
              "groups": { "Foo": "Combat" },
              "rolePathPrefixes": { "Scripts/UI/": "ui" }
            }
            """);
        try
        {
            var config = Config.Load(path);

            Assert.Equal("namespace", config.GroupBy);
            Assert.Equal(2, config.GroupDepth);
            Assert.Equal("enemy", config.Roles["Foo"]);
            Assert.Equal("Combat", config.Groups["Foo"]);
            Assert.Equal("ui", config.RolePathPrefixes["Scripts/UI/"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadToleratesCommentsAndTrailingCommas()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, """
            {
              // a comment
              "groupBy": "namespace",
            }
            """);
        try
        {
            var config = Config.Load(path);
            Assert.Equal("namespace", config.GroupBy);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
