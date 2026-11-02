namespace PicoCfg.Yaml;

public static class CfgBuilderYamlExtensions
{
    extension(CfgBuilder builder)
    {
        /// <param name="yaml">The YAML document to parse.</param>
        /// <param name="flattenArrays">
        /// When <see langword="true"/>, sequence elements are flattened with numeric index
        /// segments (<c>Servers:0</c>, <c>Items:0</c>), matching the list binding format used by
        /// CfgBind. Defaults to <see langword="false"/> (sequences are skipped), the same contract
        /// <c>AddJson</c> has.
        /// </param>
        public CfgBuilder AddYaml(string yaml, bool flattenArrays = false)
        {
            ArgumentNullException.ThrowIfNull(yaml);
            return builder.AddCustomSource(
                new YamlCfgSource(Encoding.UTF8.GetBytes(yaml), flattenArrays)
            );
        }

        /// <param name="path">The path to the YAML file.</param>
        /// <param name="flattenArrays">See <see cref="AddYaml"/>.</param>
        public CfgBuilder AddYamlFile(string path, bool flattenArrays = false)
        {
            ArgumentNullException.ThrowIfNull(path);
            return builder.AddSource(
                builder.CreateFileWatchingSource(
                    path,
                    (stream, ct) => ParseYamlFileAsync(stream, ct, flattenArrays),
                    () => File.GetLastWriteTimeUtc(path)
                )
            );
        }
    }

    private static Task<Dictionary<string, string>> ParseYamlFileAsync(
        Stream stream,
        CancellationToken ct,
        bool flattenArrays = false
    )
    {
        ct.ThrowIfCancellationRequested();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(YamlFlattener.Flatten(buffer.ToArray(), flattenArrays));
    }
}

internal sealed class YamlCfgSource(byte[] yamlBytes, bool flattenArrays = false) : ICfgSource
{
    public ValueTask<ICfgProvider> OpenAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<ICfgProvider>(new YamlCfgProvider(yamlBytes, flattenArrays));
}

internal sealed class YamlCfgProvider(byte[] yamlBytes, bool flattenArrays = false) : ICfgProvider
{
    public ICfgSnapshot Snapshot { get; } =
        new YamlCfgSnapshot(YamlFlattener.Flatten(yamlBytes, flattenArrays));

    public ValueTask<bool> ReloadAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class YamlCfgSnapshot(Dictionary<string, string> values) : ICfgSnapshot
{
    public bool TryGetValue(string path, out string? value) => values.TryGetValue(path, out value);

    public IReadOnlyDictionary<string, string> GetAllValues() => values;
}
