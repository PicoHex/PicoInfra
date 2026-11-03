namespace PicoCfg.Toml.Tests;

/// <summary>
/// The shapes the line splitter could not carry. It treated any line starting with '[' as a table
/// header, so <c>[[servers]]</c> became the header <c>[servers]</c> and every row but the last was
/// overwritten under a key that literally contained brackets; inline arrays became the string
/// <c>"[1, 2, 3]"</c>.
/// </summary>
public class TomlArrayAndDottedKeyTests
{
    [Test]
    public async Task ArrayOfTables_IndexesEveryRow_When_FlattenArrays_Is_On()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddToml(
            "[[servers]]\nname = \"alpha\"\n\n[[servers]]\nname = \"beta\"",
            flattenArrays: true
        );

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("servers:0:name")).IsEqualTo("alpha");
        await Assert.That(root.GetValue("servers:1:name")).IsEqualTo("beta");
    }

    [Test]
    public async Task ArrayOfTables_IsSkipped_When_FlattenArrays_Is_Off()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddToml("[[servers]]\nname = \"alpha\"\n\n[[servers]]\nname = \"beta\"");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("servers:0:name")).IsNull();
        await Assert.That(root.GetValue("[servers]:name")).IsNull();
        await Assert.That(root.GetValue("servers:name")).IsNull();
    }

    /// <summary>
    /// <c>[[t]]</c> carries no end token, so the block has to be closed by the next token that
    /// reports a different table. The scope it left behind used to stay at the last element, and
    /// the keys of the table that followed were emitted under it (<c>s:0:b</c>/<c>s:0:c</c>
    /// instead of <c>z:b</c>/<c>z:c</c>).
    /// </summary>
    [Test]
    public async Task TableKeysAfterAnArrayTable_StayUnderTheirOwnTable()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddToml("[[s]]\na = 1\n[z]\nb = 2\nc = 3", flattenArrays: true);

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("s:0:a")).IsEqualTo("1");
        await Assert.That(root.GetValue("z:b")).IsEqualTo("2");
        await Assert.That(root.GetValue("z:c")).IsEqualTo("3");
        await Assert.That(root.GetValue("s:0:b")).IsNull();
        await Assert.That(root.GetValue("s:0:c")).IsNull();
    }

    /// <summary>
    /// A value array inside an <c>[[table]]</c> block arrives as an <c>ArrayStart</c> that still
    /// carries the header's sticky <c>IsArrayTable</c> flag, so it used to be read as the next
    /// element of the array table: the row index advanced and the elements were dropped.
    /// </summary>
    [Test]
    public async Task ValueArrayInsideAnArrayTable_KeepsItsElements()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddToml("[[servers]]\nname = \"a\"\nports = [7, 8]", flattenArrays: true);

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("servers:0:name")).IsEqualTo("a");
        await Assert.That(root.GetValue("servers:0:ports:0")).IsEqualTo("7");
        await Assert.That(root.GetValue("servers:0:ports:1")).IsEqualTo("8");
        await Assert.That(root.GetValue("servers:1:name")).IsNull();
        // Not a scalar either: `ports` used to be published with the PREVIOUS key's value.
        await Assert.That(root.GetValue("servers:0:ports")).IsNull();
    }

    /// <summary>
    /// The same stale ValueSpan, outside an array table: the PropertyName that opens a value array
    /// keeps the previous key's value, so the array's key used to be published as a scalar holding
    /// somebody else's value and its elements were indexed at the document root (<c>0</c>,
    /// <c>1</c>).
    /// </summary>
    [Test]
    public async Task ValueArrayAfterAScalar_IsNotPublishedAsThatScalarsValue()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddToml("host = \"h\"\nports = [1, 2]", flattenArrays: true);

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("host")).IsEqualTo("h");
        await Assert.That(root.GetValue("ports:0")).IsEqualTo("1");
        await Assert.That(root.GetValue("ports:1")).IsEqualTo("2");
        await Assert.That(root.GetValue("ports")).IsNull();
        await Assert.That(root.GetValue("0")).IsNull();
    }

    [Test]
    public async Task ValueArrayInsideAnArrayTable_IsSkipped_When_FlattenArrays_Is_Off()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddToml("[[servers]]\nname = \"a\"\nports = [7, 8]");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("servers:0:ports:0")).IsNull();
        await Assert.That(root.GetValue("servers:0:ports")).IsNull();
    }

    [Test]
    public async Task ValueArray_IsIndexed_And_Never_A_BracketedString()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddToml("ports = [1, 2, 3]\nhost = \"h\"", flattenArrays: true);

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("host")).IsEqualTo("h");
        await Assert.That(root.GetValue("ports:0")).IsEqualTo("1");
        await Assert.That(root.GetValue("ports:2")).IsEqualTo("3");
    }

    [Test]
    public async Task DottedKey_KeepsItsPath()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddToml("a.b = 1\n[t]\nk = \"v\"");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("a:b")).IsEqualTo("1");
        await Assert.That(root.GetValue("t:k")).IsEqualTo("v");
    }

    [Test]
    public async Task DeepTable_KeepsTheWholePath()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddToml("[db]\nport = 5432\n[db.ssl]\non = true");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("db:port")).IsEqualTo("5432");
        await Assert.That(root.GetValue("db:ssl:on")).IsEqualTo("true");
    }
}
