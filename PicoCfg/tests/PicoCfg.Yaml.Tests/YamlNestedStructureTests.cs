namespace PicoCfg.Yaml.Tests;

/// <summary>
/// The shapes a YAML document really has, read through PicoYaml's reader instead of the
/// line splitter this backend used to carry. Every case here was SILENTLY DROPPED before
/// (a `- item` line has no ':' so the splitter skipped it, and a block scalar's body lines
/// vanished the same way) — see the PicoSerDe capability matrix: YAML round-trips nested
/// sequences, object sequences and block scalars, so a reader-based flattener can carry them.
/// </summary>
public class YamlNestedStructureTests
{
    [Test]
    public async Task NestedSequence_IsIndexed_When_FlattenArrays_Is_On()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddYaml("Servers:\n  - a\n  - b\nHost: localhost", flattenArrays: true);

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("Host")).IsEqualTo("localhost");
        await Assert.That(root.GetValue("Servers:0")).IsEqualTo("a");
        await Assert.That(root.GetValue("Servers:1")).IsEqualTo("b");
    }

    [Test]
    public async Task NestedSequence_IsSkipped_When_FlattenArrays_Is_Off()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddYaml("Servers:\n  - a\n  - b\nHost: localhost");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("Host")).IsEqualTo("localhost");
        await Assert.That(root.GetValue("Servers:0")).IsNull();
        await Assert.That(root.GetValue("Servers")).IsNull();
    }

    /// <summary>
    /// A sequence of mappings, as far as the tokenizer carries it. PicoYaml hands the FIRST inline
    /// member of a `- name: x` item over as one raw string and the rest of the mapping as a nested
    /// object, so <c>Items:0</c> is <c>"name: x"</c> and the continuation lands under
    /// <c>Items:0:port</c>. Splitting that string here would put a hand-written YAML parser back in
    /// this file — the reader is the thing that has to learn to emit a PropertyName for it.
    /// </summary>
    [Test]
    public async Task SequenceOfMappings_KeepsTheRowsTheReaderTokenizes()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddYaml("Items:\n  - name: x\n    port: 1", flattenArrays: true);

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("Items:0")).IsEqualTo("name: x");
        await Assert.That(root.GetValue("Items:0:port")).IsEqualTo("1");
    }

    /// <summary>
    /// The documented contract is "sequences are skipped", not "sequence items are skipped": the
    /// reader hands a <c>- name: x</c> item's first member over as a fused scalar and the rest of
    /// the mapping as a continuation object, and that object's members used to be emitted whatever
    /// <c>flattenArrays</c> said, so <c>Items:0:port</c> leaked with the flag off.
    /// </summary>
    [Test]
    public async Task SequenceOfMappings_IsSkippedEntirely_When_FlattenArrays_Is_Off()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddYaml("Items:\n  - name: x\n    port: 1\nHost: localhost");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("Host")).IsEqualTo("localhost");
        await Assert.That(root.GetValue("Items:0")).IsNull();
        await Assert.That(root.GetValue("Items:0:name")).IsNull();
        await Assert.That(root.GetValue("Items:0:port")).IsNull();

        // Not only the keys named above: nothing at all may be published under Items.
        var leaked = root.GetAll()
            .Keys.Where(static key => key.StartsWith("Items", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(string.Join(", ", leaked)).IsEqualTo("");
    }

    [Test]
    public async Task BlockScalar_KeepsItsBody()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddYaml("Script: |\n  line one\n  line two\nAfter: yes");

        var root = await builder.BuildAsync();

        var script = root.GetValue("Script");
        await Assert.That(script).IsNotNull();
        await Assert.That(script!).Contains("line one");
        await Assert.That(script!).Contains("line two");
        await Assert.That(root.GetValue("After")).IsEqualTo("yes");
    }

    [Test]
    public async Task FourSpaceIndent_NestsTheSameAsTwo()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddYaml("A:\n    B: inner\nC: outer");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("A:B")).IsEqualTo("inner");
        await Assert.That(root.GetValue("C")).IsEqualTo("outer");
    }

    [Test]
    public async Task ScalarsKeepTheirTypeShape_AsStrings()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddYaml("Port: 5432\nFlag: true\nNone:");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("Port")).IsEqualTo("5432");
        await Assert.That(root.GetValue("Flag")).IsEqualTo("true");
        await Assert.That(root.GetValue("None")).IsNull();
    }

    /// <summary>
    /// PicoSerDe 2026.12.1 made the YAML reader push exactly what it opens and pop exactly what it
    /// closes; before that it emitted one trailing <c>ObjectEnd</c> past the last close, and this
    /// backend carried a compensation branch for it. The branch is gone (the guard that remains is
    /// only a bound), so this pins the property the compensation used to hide: a nested document
    /// closes every container it opens, and a key written after the nested block still flattens.
    /// </summary>
    [Test]
    public async Task NestedDocument_ClosesEveryContainerItOpens_AndTheFollowingKeySurvives()
    {
        var builder = Cfg.CreateBuilder();
        builder.AddYaml("A:\n  B:\n    C: deep\nAfter: kept");

        var root = await builder.BuildAsync();

        await Assert.That(root.GetValue("A:B:C")).IsEqualTo("deep");
        await Assert
            .That(root.GetValue("After"))
            .IsEqualTo("kept")
            .Because("a container that closed one token too many would swallow the key after it");
    }
}
