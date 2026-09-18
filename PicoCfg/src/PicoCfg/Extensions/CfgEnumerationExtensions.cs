namespace PicoCfg.Extensions;

/// <summary>
/// Extension methods for enumerating configuration key-value pairs from <see cref="ICfg"/> views.
/// </summary>
public static class CfgEnumerationExtensions
{
    /// <summary>
    /// Returns all key-value pairs from the configuration view.
    /// Keys are merged in provider order — later providers override earlier ones,
    /// and keys are matched case-insensitively (one entry per key, so enumeration
    /// agrees with lookup).
    /// When the view wraps an <see cref="ICfgSnapshot"/>, all keys from that
    /// snapshot are returned. External <see cref="ICfg"/> implementations that do
    /// not implement <see cref="ICfgSnapshot"/> return an empty dictionary.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GetAll(this ICfg cfg)
    {
        if (cfg is ICfgSnapshot snapshot)
            return snapshot.GetAllValues();

        if (cfg is IInternalCfgRootSnapshotAccessor rootAccessor)
            return rootAccessor.CurrentSnapshot.GetAllValues();

        if (cfg is CfgSection section)
        {
            var prefix = section.Path;
            if (string.IsNullOrEmpty(prefix))
                return section.Parent.GetAll();

            var parentAll = section.Parent.GetAll();
            var filtered = new Dictionary<string, string>(parentAll.Count);
            var searchPrefix = prefix + ":";

            foreach (var kvp in parentAll)
            {
                if (kvp.Key.StartsWith(searchPrefix, StringComparison.OrdinalIgnoreCase))
                    filtered[kvp.Key[searchPrefix.Length..]] = kvp.Value;
            }

            return filtered;
        }

        return new Dictionary<string, string>();
    }
}
