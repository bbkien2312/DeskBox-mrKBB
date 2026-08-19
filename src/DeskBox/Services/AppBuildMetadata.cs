using System.Reflection;

namespace DeskBox.Services;

/// <summary>
/// Build identity for the fork channel. The upstream version and commit are
/// intentionally kept separate from the fork version so an upstream release
/// cannot mask a fork update.
/// </summary>
public static class AppBuildMetadata
{
    private static readonly Assembly s_assembly = typeof(AppBuildMetadata).Assembly;

    public static string UpstreamVersion => Get("DeskBox.UpstreamVersion", "1.4.2");
    public static string ForkVersion => Get("DeskBox.ForkVersion", "1.4.2.1");
    public static string ForkDisplayVersion => Get("DeskBox.ForkDisplayVersion", $"{ForkVersion} (Fork)");
    public static string BuildNumber => Get("DeskBox.BuildNumber", "dev");
    public static string UpstreamCommit => Get("DeskBox.UpstreamCommit", "unknown");
    public static string ForkCommit => Get("DeskBox.ForkCommit", "unknown");

    public static string DisplaySummary =>
        $"{ForkDisplayVersion} · nền {UpstreamVersion} · build {BuildNumber}";

    private static string Get(string key, string fallback)
    {
        string? value = s_assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute =>
                string.Equals(attribute.Key, key, StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
