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

    /// <summary>
    /// A key defined twice in one table is not a collision we resolve - it is a document TOML 1.0
    /// forbids, so the reader refuses it before the flattening ever sees two values. This test used to
    /// assert "last wins", which was this repo codifying the reader's leniency as a contract; the
    /// clause is TOML 1.0 section Keys (defining a key multiple times is invalid), and tomllib refuses
    /// the same document.
    /// </summary>
    [Test]
    public async Task DuplicateKeyInTheSameTable_IsRefusedByTheReader()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddToml("[t]\nk = 1\nk = 2");

        await Assert
            .That(async () => await builder.BuildAsync())
            .Throws<FormatException>()
            .Because("TOML 1.0 section Keys forbids defining a key twice in the same table");
    }
}
