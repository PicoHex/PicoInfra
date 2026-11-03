namespace PicoCfg.Ini;

public static class CfgBuilderIniExtensions
{
    extension(CfgBuilder builder)
    {
        public CfgBuilder AddIni(string ini)
        {
            ArgumentNullException.ThrowIfNull(ini);
            return builder.AddCustomSource(new IniCfgSource(Encoding.UTF8.GetBytes(ini)));
        }

        public CfgBuilder AddIniFile(string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            return builder.AddSource(
                builder.CreateFileWatchingSource(
                    path,
                    (stream, ct) => ParseIniFileAsync(path, stream, ct),
                    () => File.GetLastWriteTimeUtc(path)
                )
            );
        }
    }

    private static Task<Dictionary<string, string>> ParseIniFileAsync(
        string path,
        Stream stream,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(IniFlattener.Flatten(buffer.ToArray(), path));
    }
}

internal sealed class IniCfgSource(byte[] iniBytes) : ICfgSource
{
    public ValueTask<ICfgProvider> OpenAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<ICfgProvider>(new IniCfgProvider(iniBytes));
}

internal sealed class IniCfgProvider(byte[] iniBytes) : ICfgProvider
{
    public ICfgSnapshot Snapshot { get; } = new IniCfgSnapshot(IniFlattener.Flatten(iniBytes));

    public ValueTask<bool> ReloadAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class IniCfgSnapshot(Dictionary<string, string> values) : ICfgSnapshot
{
    public bool TryGetValue(string path, out string? value) => values.TryGetValue(path, out value);

    public IReadOnlyDictionary<string, string> GetAllValues() => values;
}
