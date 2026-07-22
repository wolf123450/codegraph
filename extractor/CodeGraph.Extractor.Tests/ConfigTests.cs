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
              "groups": { "Foo": ["Combat", "Core"] },
              "rolePathPrefixes": { "Scripts/UI/": "ui" }
            }
            """);
        try
        {
            var config = Config.Load(path);

            Assert.Equal("namespace", config.GroupBy);
            Assert.Equal(2, config.GroupDepth);
            Assert.Equal("enemy", config.Roles["Foo"]);
            Assert.Equal(new[] { "Combat", "Core" }, config.Groups["Foo"]);
            Assert.Equal("ui", config.RolePathPrefixes["Scripts/UI/"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("test/PlayerCapture.cs", true)]
    [InlineData("TEST/PlayerCapture.cs", true)]
    [InlineData("addons/gdUnit4/test/core/discovery/Foo.cs", true)]
    [InlineData("scripts/test/Foo.cs", false)] // "test/" prefix must match from the root, not anywhere
    [InlineData("Scripts/Player.cs", false)]
    public void IsExcludedMatchesConfiguredPrefixesCaseInsensitively(string relPath, bool expected)
    {
        var config = new Config { Exclude = { "test/", "addons/gdUnit4/" } };

        Assert.Equal(expected, config.IsExcluded(relPath));
    }

    [Fact]
    public void IsExcludedIsFalseWhenNoExcludesConfigured()
    {
        var config = new Config();

        Assert.False(config.IsExcluded("test/Anything.cs"));
    }

    [Fact]
    public void LoadParsesExcludeList()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, """
            { "exclude": ["test/", "addons/gdUnit4/"] }
            """);
        try
        {
            var config = Config.Load(path);
            Assert.Equal(new[] { "test/", "addons/gdUnit4/" }, config.Exclude);
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
