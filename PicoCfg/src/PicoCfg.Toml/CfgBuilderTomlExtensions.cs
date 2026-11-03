namespace PicoCfg.Toml;

public static class CfgBuilderTomlExtensions
{
    extension(CfgBuilder builder)
    {
        /// <param name="toml">The TOML document to parse.</param>
        /// <param name="flattenArrays">
        /// When <see langword="true"/>, array elements are flattened with numeric index segments —
        /// <c>servers:0:name</c> for an array of tables, <c>ports:0</c> for a value array — matching
        /// the list binding format used by CfgBind. Defaults to <see langword="false"/> (arrays are
        /// skipped), the same contract <c>AddJson</c> has.
        /// </param>
        public CfgBuilder AddToml(string toml, bool flattenArrays = false)
        {
            ArgumentNullException.ThrowIfNull(toml);
            return builder.AddCustomSource(
                new TomlCfgSource(Encoding.UTF8.GetBytes(toml), flattenArrays)
            );
        }

        /// <param name="path">The path to the TOML file.</param>
        /// <param name="flattenArrays">See <see cref="AddToml"/>.</param>
        public CfgBuilder AddTomlFile(string path, bool flattenArrays = false)
        {
            ArgumentNullException.ThrowIfNull(path);
            return builder.AddSource(
                builder.CreateFileWatchingSource(
                    path,
                    (stream, ct) => ParseTomlFileAsync(path, stream, ct, flattenArrays),
                    () => File.GetLastWriteTimeUtc(path)
                )
            );
        }
    }

    private static Task<Dictionary<string, string>> ParseTomlFileAsync(
        string path,
        Stream stream,
        CancellationToken ct,
        bool flattenArrays = false
    )
    {
        ct.ThrowIfCancellationRequested();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(TomlFlattener.Flatten(buffer.ToArray(), flattenArrays, path));
    }
}

internal sealed class TomlCfgSource(byte[] tomlBytes, bool flattenArrays = false) : ICfgSource
{
    public ValueTask<ICfgProvider> OpenAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<ICfgProvider>(new TomlCfgProvider(tomlBytes, flattenArrays));
}

internal sealed class TomlCfgProvider(byte[] tomlBytes, bool flattenArrays = false) : ICfgProvider
{
    public ICfgSnapshot Snapshot { get; } =
        new TomlCfgSnapshot(TomlFlattener.Flatten(tomlBytes, flattenArrays));

    public ValueTask<bool> ReloadAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class TomlCfgSnapshot(Dictionary<string, string> values) : ICfgSnapshot
{
    public bool TryGetValue(string path, out string? value) => values.TryGetValue(path, out value);

    public IReadOnlyDictionary<string, string> GetAllValues() => values;
}
