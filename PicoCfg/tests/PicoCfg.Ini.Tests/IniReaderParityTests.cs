namespace PicoCfg.Ini.Tests;

/// <summary>
/// The INI backend now reads through <c>PicoIni</c> like the other three formats instead of
/// carrying its own line splitter. INI has no nesting, so the contract is unchanged on purpose —
/// these pin the behavior that used to come from hand parsing (comments, '=' inside a value,
/// empty value) so the rewrite cannot drift.
/// </summary>
public class IniReaderParityTests
{
    [Test]
    public async Task Comments_AreIgnored()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddIni("; a comment\n[App]\n# another\nName=test");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("App:Name")).IsEqualTo("test");
    }

    [Test]
    public async Task EqualsInsideAValue_KeepsTheWholeValue()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddIni("[App]\nConn=Server=x;Db=y");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("App:Conn")).IsEqualTo("Server=x;Db=y");
    }

    [Test]
    public async Task EmptyValue_StaysAnEmptyString()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddIni("[App]\nEmpty=");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("App:Empty")).IsEqualTo("");
    }

    [Test]
    public async Task KeyWithNoSection_HasNoPrefix()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddIni("Top=1\n[App]\nName=test");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("Top")).IsEqualTo("1");
        await Assert.That(root.GetValue("App:Name")).IsEqualTo("test");
    }
}
