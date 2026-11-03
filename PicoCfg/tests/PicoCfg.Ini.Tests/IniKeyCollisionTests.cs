namespace PicoCfg.Ini.Tests;

/// <summary>
/// The key-collision contract for the INI backend. INI is the format that has to change a rule
/// rather than only add a guard: a section already nests with <c>'.'</c> (<c>[Parent.Child]</c> →
/// <c>Parent:Child</c>), so <c>':'</c> — PicoCfg's own path separator — is not a legal character in
/// a section name or a key. <c>[A.C]\nk=1</c> and <c>[A:C]\nk=2</c> used to normalise onto the same
/// section and overwrite each other with no diagnostic; <c>[A:C]</c> is now rejected at its own
/// position (naming <c>[A.C]</c> too, when the document contains it), and every document that
/// spells nesting with <c>'.'</c> keeps exactly the keys it has today.
/// </summary>
public class IniKeyCollisionTests
{
    /// <summary>
    /// The measured silent overwrite: both sections normalise to <c>A:C</c>, so <c>k</c> was published
    /// twice and the second row won. Both spellings have to be findable from the error.
    /// </summary>
    [Test]
    public async Task DottedSectionAndColonSection_WithTheSameKey_Throws()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddIni("[A.C]\nk=1\n[A:C]\nk=2");

        var collision = await Collision.TryBuildAsync(builder);

        await Assert.That(collision).IsNotNull();
        await Assert.That(collision!.Key).IsEqualTo("A:C");
        await Assert.That(collision.Message).Contains("A:C");
        await Assert.That(collision.Message).Contains("INI");
        await Assert.That(collision.ExistingPosition).Contains("[A.C]");
        await Assert.That(collision.IncomingPosition).Contains("[A:C]");
    }

    /// <summary>
    /// The rule this backend chose, pinned: <c>':'</c> is reserved, so <c>[A:C]</c> is not a section
    /// name even when nothing else in the document uses <c>[A.C]</c>. Without this the document has
    /// no way to say whether <c>A:C</c> is the section <c>[A.C]</c> or a section literally called
    /// <c>A:C</c> — which is what made the <c>[A.C]</c> + <c>[A:C]</c> pair possible in the first
    /// place.
    /// </summary>
    [Test]
    public async Task ColonInASectionName_IsRejectedOnItsOwn()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddIni("[A:C]\nk=1");

        var collision = await Collision.TryBuildAsync(builder);

        await Assert.That(collision).IsNotNull();
        await Assert.That(collision!.Key).IsEqualTo("A:C");
        await Assert.That(collision.Message).Contains("section name");
        await Assert.That(collision.Message).Contains("[A:C]");
        // Nothing else produced the key: only the one rejected position exists.
        await Assert.That(collision.ExistingPosition).IsNull();
        await Assert.That(collision.IncomingPosition).Contains("[A:C]");
    }

    /// <summary>
    /// Two keys that do not collide are still ambiguous: <c>[A.C]</c> and <c>[A:C]</c> merge into one
    /// section, so a lookup cannot say which section a sibling came from. The header is what is
    /// rejected, and it is read before the rows.
    /// </summary>
    [Test]
    public async Task DottedSectionAndColonSection_WithDifferentKeys_Throw()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddIni("[A.C]\nk=1\n[A:C]\nj=2");

        var collision = await Collision.TryBuildAsync(builder);

        await Assert.That(collision).IsNotNull();
        await Assert.That(collision!.Key).IsEqualTo("A:C");
        await Assert.That(collision.ExistingPosition).Contains("[A.C]");
        await Assert.That(collision.IncomingPosition).Contains("[A:C]");
    }

    /// <summary>
    /// The same rule on the key half: <c>[A]\nB:C=1</c> addresses <c>A:B:C</c>, which
    /// <c>[A.B]\nC=1</c> already addresses, so the row is rejected and both positions are reported.
    /// </summary>
    [Test]
    public async Task ColonInsideAKey_AndDottedSection_Throw()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddIni("[A.B]\nC=2\n[A]\nB:C=1");

        var collision = await Collision.TryBuildAsync(builder);

        await Assert.That(collision).IsNotNull();
        await Assert.That(collision!.Key).IsEqualTo("A:B:C");
        await Assert.That(collision.Message).Contains("key");
        await Assert.That(collision.ExistingPosition).Contains("A.B → C");
        await Assert.That(collision.IncomingPosition).Contains("B:C");
    }

    /// <summary>A key carrying ':' is not a key name, with or without a dotted twin.</summary>
    [Test]
    public async Task ColonInsideAKey_IsRejectedOnItsOwn()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddIni("[A]\nB:C=1");

        var collision = await Collision.TryBuildAsync(builder);

        await Assert.That(collision).IsNotNull();
        await Assert.That(collision!.Key).IsEqualTo("A:B:C");
        await Assert.That(collision.ExistingPosition).IsNull();
        await Assert.That(collision.IncomingPosition).Contains("B:C");
    }

    /// <summary>
    /// The migration guarantee: '.' still nests exactly as it did, so no existing document changes
    /// a key.
    /// </summary>
    [Test]
    public async Task DottedSection_StillNests()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddIni("[Parent]\nKey=value1\n[Parent.Child]\nKey=value2");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("Parent:Key")).IsEqualTo("value1");
        await Assert.That(root.GetValue("Parent:Child:Key")).IsEqualTo("value2");
    }

    /// <summary>
    /// A file source names the file in the message — the two positions have to be findable, and for
    /// a watcher-driven source the line number alone is not enough to know which document to open.
    /// </summary>
    [Test]
    public async Task AddIniFile_Collision_NamesTheFile()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"picocfg-ini-collision-{Guid.NewGuid():N}.ini"
        );
        await File.WriteAllTextAsync(path, "[A.C]\nk=1\n[A:C]\nk=2");

        try
        {
            var builder = Cfg.CreateBuilder();
            builder.AddIniFile(path);

            var collision = await Collision.TryBuildAsync(builder);

            await Assert.That(collision).IsNotNull();
            await Assert.That(collision!.Message).Contains(path);
            await Assert.That(collision.ExistingPosition).Contains("[A.C]");
            await Assert.That(collision.IncomingPosition).Contains("[A:C]");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ColonInsideAValue_IsUnaffected()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddIni("[App]\nUrl=http://host:80/path\nConn=Server=x;Db=y");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("App:Url")).IsEqualTo("http://host:80/path");
        await Assert.That(root.GetValue("App:Conn")).IsEqualTo("Server=x;Db=y");
    }

    /// <summary>Duplicate keys inside ONE section are not a collision — last wins, as before.</summary>
    [Test]
    public async Task DuplicateKeyInTheSameSection_LastWins()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddIni("[App]\nKey=first\nKey=second");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("App:Key")).IsEqualTo("second");
    }

    /// <summary>
    /// A section header may legitimately be repeated (INI has no "define once" rule); the rows are
    /// still the same mapping, so last wins instead of throwing.
    /// </summary>
    [Test]
    public async Task RepeatedSectionHeader_LastWins()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddIni("[A]\nk=1\n[B]\nx=0\n[A]\nk=2");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("A:k")).IsEqualTo("2");
        await Assert.That(root.GetValue("B:x")).IsEqualTo("0");
    }
}
