namespace PicoCfg.Tests;

internal static class TestCfgFactory
{
    private static readonly Func<
        Stream,
        CancellationToken,
        Task<Dictionary<string, string>>
    > DefaultStreamParser = async (stream, ct) =>
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var data = new Dictionary<string, string>();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var idx = line.IndexOf('=');
            if (idx < 0)
                continue;
            data[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        return data;
    };

    public static CfgProviderState CreateProviderState(
        Func<CfgChangeSignal>? changeSignalFactory = null,
        Func<IReadOnlyDictionary<string, string>, int, CfgSnapshot>? snapshotFactory = null
    )
    {
        return new CfgProviderState(
            changeSignalFactory ?? (static () => new CfgChangeSignal()),
            snapshotFactory
                ?? (static (values, fingerprint) => new CfgSnapshot(values, fingerprint))
        );
    }

    public static StreamCfgProvider CreateStreamProvider(
        Func<CancellationToken, ValueTask<Stream>> streamFactory,
        Func<object?>? versionStampFactory = null,
        Func<Stream, CancellationToken, Task<Dictionary<string, string>>>? streamParser = null,
        CfgProviderState? state = null
    )
    {
        return new StreamCfgProvider(
            streamFactory,
            versionStampFactory,
            streamParser: streamParser ?? DefaultStreamParser,
            state ?? CreateProviderState()
        );
    }

    public static StreamCfgSource CreateStreamSource(
        Func<CancellationToken, ValueTask<Stream>> streamFactory,
        Func<object?>? versionStampFactory = null,
        Func<Stream, CancellationToken, Task<Dictionary<string, string>>>? streamParser = null,
        Func<CfgProviderState>? providerStateFactory = null
    )
    {
        return new StreamCfgSource(() =>
            CreateStreamProvider(
                streamFactory,
                versionStampFactory,
                streamParser,
                providerStateFactory?.Invoke() ?? CreateProviderState()
            )
        );
    }

    public static DictionaryCfgProvider CreateDictionaryProvider(
        Func<IEnumerable<KeyValuePair<string, string>>> dataFactory,
        Func<object?>? versionStampFactory = null,
        CfgProviderState? state = null
    )
    {
        return new DictionaryCfgProvider(
            dataFactory,
            versionStampFactory,
            state ?? CreateProviderState()
        );
    }

    public static DictionaryCfgSource CreateDictionarySource(
        IDictionary<string, string> data,
        Func<object?>? versionStampFactory = null,
        Func<CfgProviderState>? providerStateFactory = null
    )
    {
        return CreateDictionarySource(() => data, versionStampFactory, providerStateFactory);
    }

    public static DictionaryCfgSource CreateDictionarySource(
        Func<IEnumerable<KeyValuePair<string, string>>> dataFactory,
        Func<object?>? versionStampFactory = null,
        Func<CfgProviderState>? providerStateFactory = null
    )
    {
        return new DictionaryCfgSource(() =>
            CreateDictionaryProvider(
                dataFactory,
                versionStampFactory,
                providerStateFactory?.Invoke() ?? CreateProviderState()
            )
        );
    }

    public static CfgRoot CreateRoot(
        IEnumerable<ICfgProvider> providers,
        Func<IReadOnlyList<ICfgSnapshot>, ICfgSnapshot>? snapshotComposer = null,
        Func<CfgChangeSignal>? changeSignalFactory = null,
        TimeSpan? reloadDrainTimeout = null,
        TimeSpan? reloadDrainRetryTimeout = null
    )
    {
        return new CfgRoot(
            providers,
            snapshotComposer
                ?? (
                    providerSnapshots =>
                        CfgSnapshotComposer.CreateSnapshot(
                            providerSnapshots,
                            (values, fingerprint) => new CfgSnapshot(values, fingerprint)
                        )
                ),
            changeSignalFactory ?? (static () => new CfgChangeSignal()),
            reloadDrainTimeout,
            reloadDrainRetryTimeout
        );
    }

    public static CfgRoot CreateRoot(params ICfgProvider[] providers)
    {
        return CreateRoot((IEnumerable<ICfgProvider>)providers);
    }
}
