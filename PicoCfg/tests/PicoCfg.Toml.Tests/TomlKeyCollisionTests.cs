namespace PicoCfg.Toml.Tests;

/// <summary>
/// The key-collision contract for the TOML backend: <c>TablePath</c> arrives dotted and is
/// normalised to ':' (<c>[db.ssl]</c> → <c>db:ssl</c>), which is genuine TOML nesting and stays.
/// A ':' that a document puts inside a key therefore lands on the same flat key as a nested table,
/// and that may not overwrite silently.
/// </summary>
public class TomlKeyCollisionTests
{
    [Test]
    public async Task ColonInsideAKeyVersusNestedTable_Throws()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddToml("[db]\n\"ssl:on\" = true\n[db.ssl]\non = false");

        var collision = await Collision.TryBuildAsync(builder);

        await Assert.That(collision).IsNotNull();
        await Assert.That(collision!.Key).IsEqualTo("db:ssl:on");
        await Assert.That(collision.Message).Contains("db:ssl:on");
        await Assert.That(collision.Message).Contains("TOML");
        // Both positions have to be findable from the message: the table as it was SPELLED (db.ssl)
        // and the literal ':' key as the segment it is (db → ssl:on).
        await Assert.That(collision.ExistingPosition!).Contains("db → ssl:on");
        await Assert.That(collision.IncomingPosition).Contains("db.ssl");
    }

    /// <summary>Dotted tables keep nesting exactly as before — the rewrite is not going away.</summary>
    [Test]
    public async Task DottedTable_StillNests()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddToml("[db]\nport = 5432\n[db.ssl]\non = true");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("db:port")).IsEqualTo("5432");
        await Assert.That(root.GetValue("db:ssl:on")).IsEqualTo("true");
    }

    [Test]
    public async Task ColonInsideAValue_IsUnaffected()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddToml("url = \"http://host:80/path\"\n[db.ssl]\non = true");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("url")).IsEqualTo("http://host:80/path");
        await Assert.That(root.GetValue("db:ssl:on")).IsEqualTo("true");
    }

    /// <summary>Duplicate keys inside ONE table are not a collision — last wins, as before.</summary>
    [Test]
    public async Task DuplicateKeyInTheSameTable_LastWins()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddToml("[t]\nk = 1\nk = 2");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("t:k")).IsEqualTo("2");
    }
}
