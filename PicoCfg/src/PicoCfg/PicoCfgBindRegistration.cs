namespace PicoCfg;

internal sealed class PicoCfgBindRegistration<T>(
    int contractVersion,
    Func<ICfg, string?, T>? bind,
    PicoCfgGeneratedTryBindDelegate<T>? tryBind,
    PicoCfgGeneratedBindIntoDelegate<T>? bindInto
)
{
    public int ContractVersion { get; } = contractVersion;
    public Func<ICfg, string?, T>? Bind { get; } = bind;
    public PicoCfgGeneratedTryBindDelegate<T>? TryBind { get; } = tryBind;
    public PicoCfgGeneratedBindIntoDelegate<T>? BindInto { get; } = bindInto;
}
