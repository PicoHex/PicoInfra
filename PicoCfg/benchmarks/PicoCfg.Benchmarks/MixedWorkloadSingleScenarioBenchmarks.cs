[BenchmarkClass(
    Description = "Steady-state lookup: build once (GlobalSetup), measure lookup throughput (single scenario)"
)]
public sealed partial class MixedWorkloadSingleScenarioBenchmarks
{
    private readonly int _n;
    private readonly int _providerCount;
    private readonly int _lookupPassCount;
    private IReadOnlyList<Dictionary<string, string>> _dataSets = null!;
    private string[] _keys = null!;
    private IConfigurationRoot _msConfig = null!;
    private ICfgRoot _picoRoot = null!;

    public MixedWorkloadSingleScenarioBenchmarks(int n, int providerCount, int lookupPassCount)
    {
        _n = n;
        _providerCount = providerCount;
        _lookupPassCount = lookupPassCount;
    }

    // PicoBench 2026.2.6 (PBGEN015) requires a public parameterless constructor
    // on [BenchmarkClass] types even when they are only run via the instance
    // overload. The CLI always constructs this class with explicit scenario
    // values; the defaults below only back the generic Run<T>() entry point.
    public MixedWorkloadSingleScenarioBenchmarks()
        : this(100, 1, 1) { }

    [GlobalSetup]
    public void Setup()
    {
        var dataSets = new List<Dictionary<string, string>>(_providerCount);
        for (var providerIndex = 0; providerIndex < _providerCount; providerIndex++)
        {
            var data = new Dictionary<string, string>(_n);
            for (var i = 0; i < _n; i++)
                data[$"Section:Key{i}"] = $"Provider{providerIndex}:Value{i}";

            dataSets.Add(data);
        }

        _dataSets = dataSets;
        _keys = _dataSets[^1].Keys.ToArray();

        var msBuilder = new ConfigurationBuilder();
        for (var i = 0; i < _dataSets.Count; i++)
        {
            msBuilder.AddInMemoryCollection(
                _dataSets[i]
                    .ToDictionary(static pair => pair.Key, static pair => (string?)pair.Value)
            );
        }

        _msConfig = msBuilder.Build();

        var builder = Cfg.CreateBuilder();
        for (var i = 0; i < _dataSets.Count; i++)
            builder.Add(_dataSets[i]);

        _picoRoot = builder.BuildAsync().AsTask().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _picoRoot.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true)]
    public void MsConfig()
    {
        for (var pass = 0; pass < _lookupPassCount; pass++)
        {
            for (var i = 0; i < _keys.Length; i++)
                _ = _msConfig[_keys[i]];
        }
    }

    [Benchmark]
    public void PicoCfg()
    {
        for (var pass = 0; pass < _lookupPassCount; pass++)
        {
            for (var i = 0; i < _keys.Length; i++)
                _ = _picoRoot.GetValue(_keys[i]);
        }
    }
}
