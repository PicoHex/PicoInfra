namespace PicoCfg.Yaml.Tests;

internal static class Collision
{
    /// <summary>
    /// Builds the root and returns the collision the build threw, or <see langword="null"/> when the
    /// document flattened without one (which is what makes the assertion read as a red test instead
    /// of an unexplained exception).
    /// </summary>
    public static async Task<CfgKeyCollisionException?> TryBuildAsync(CfgBuilder builder)
    {
        try
        {
            await using var root = await builder.BuildAsync();
            return null;
        }
        catch (CfgKeyCollisionException ex)
        {
            return ex;
        }
    }
}
