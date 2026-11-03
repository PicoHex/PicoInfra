namespace PicoCfg.Yaml.Tests;

/// <summary>
/// The key-collision contract for the YAML backend: a path segment that itself contains the
/// separator ':' flattens onto the same key as real nesting, and that may not happen silently.
/// <c>a:\n  b: 1</c> and <c>"a:b": 2</c> are both <c>a:b</c>; before the fix the later value simply
/// won.
/// </summary>
public class YamlKeyCollisionTests
{
    [Test]
    public async Task NestingVersusLiteralColonKey_Throws()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddYaml("a:\n  b: 1\n\"a:b\": 2");

        var collision = await Collision.TryBuildAsync(builder);

        await Assert.That(collision).IsNotNull();
        await Assert.That(collision!.Key).IsEqualTo("a:b");
        await Assert.That(collision.Message).Contains("a:b");
        await Assert.That(collision.Message).Contains("YAML");
        // Both positions have to be findable from the message: the nested write is reported as its
        // segments (a → b), the quoted key as the single segment it is.
        await Assert.That(collision.ExistingPosition!).Contains("a → b");
        await Assert.That(collision.IncomingPosition).Contains("a:b");
    }

    /// <summary>The same collision one level deeper, so the path — not just the leaf — is reported.</summary>
    [Test]
    public async Task NestedNestingVersusLiteralColonKey_Throws()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddYaml("\"a:b\":\n  c: 1\na:\n  b:\n    c: 2");

        var collision = await Collision.TryBuildAsync(builder);

        await Assert.That(collision).IsNotNull();
        await Assert.That(collision!.Key).IsEqualTo("a:b:c");
    }

    /// <summary>
    /// An array index is a path segment too: with <c>flattenArrays</c> on, sequence item 1 of
    /// <c>a</c> is <c>a:1</c>, which a literal <c>"a:1"</c> key also is.
    /// </summary>
    [Test]
    public async Task SequenceIndexVersusLiteralColonKey_Throws()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddYaml("a:\n  - x\n  - y\n\"a:1\": 9", flattenArrays: true);

        var collision = await Collision.TryBuildAsync(builder);

        await Assert.That(collision).IsNotNull();
        await Assert.That(collision!.Key).IsEqualTo("a:1");
    }

    [Test]
    public async Task ColonInsideAValue_IsUnaffected()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddYaml("Url: \"http://host:80/path\"\nA:\n  B: \"c:d\"");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("Url")).IsEqualTo("http://host:80/path");
        await Assert.That(root.GetValue("A:B")).IsEqualTo("c:d");
    }

    /// <summary>Duplicate keys inside ONE mapping are not a collision — last wins, as before.</summary>
    [Test]
    public async Task DuplicateKeyInTheSameMapping_LastWins()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddYaml("a: 1\na: 2");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("a")).IsEqualTo("2");
    }

    [Test]
    public async Task DuplicateKeyInTheSameNestedMapping_LastWins()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddYaml("a:\n  b: 1\n  b: 2");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("a:b")).IsEqualTo("2");
    }
}
