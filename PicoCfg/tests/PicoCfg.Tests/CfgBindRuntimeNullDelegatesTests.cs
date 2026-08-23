using PicoCfg;
using PicoCfg.Abs;

namespace PicoCfg.Tests;

// Generated-code redundancy: nested-only types register with bindInto: null.
// CfgBind.BindInto must surface the missing-registration exception instead of
// throwing a NullReferenceException.
public sealed class CfgBindRuntimeNullDelegatesTests
{
    public sealed class NullBindIntoProbe
    {
        public string? Name { get; set; }
    }

    [Test]
    public async Task BindInto_WithNullBindIntoRegistration_ThrowsMissingRegistration()
    {
        // Simulate the pruned registration shape: constructing bind present,
        // BindInto delegate absent (what the generator emits for nested-only types).
        CfgBindRuntime.Register<NullBindIntoProbe>(
            CfgBindRuntime.ContractVersion,
            bind: static (cfg, section) => new NullBindIntoProbe(),
            tryBind: null,
            bindInto: null
        );

        await using var root = await Cfg.CreateBuilder()
            .Add(new Dictionary<string, string> { ["Name"] = "x" })
            .BuildAsync();

        await Assert
            .That(() => CfgBind.BindInto(root, new NullBindIntoProbe()))
            .Throws<PicoCfgBindRegistrationException>();
    }
}
