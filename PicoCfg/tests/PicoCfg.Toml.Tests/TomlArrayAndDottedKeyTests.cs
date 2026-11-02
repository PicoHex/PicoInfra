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
