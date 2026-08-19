using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class ForkBuildMetadataTests
{
    [Fact]
    public void BuildMetadata_SeparatesForkAndUpstreamIdentity()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppBuildMetadata.ForkVersion));
        Assert.False(string.IsNullOrWhiteSpace(AppBuildMetadata.UpstreamVersion));
        Assert.False(string.IsNullOrWhiteSpace(AppBuildMetadata.ForkDisplayVersion));
        Assert.Contains("Fork", AppBuildMetadata.ForkDisplayVersion, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UpdateChannel_UsesForkDefaults()
    {
        Assert.Contains("bbkien2312/DeskBox-mrKBB", AppUpdateService.DefaultGitHubLatestReleaseApiUrl, StringComparison.Ordinal);
        Assert.Contains("bbkien2312/DeskBox-mrKBB", AppUpdateService.DefaultManifestUrl, StringComparison.Ordinal);
        Assert.Contains("bbkien2312/DeskBox-mrKBB", AppUpdateService.DefaultManualDownloadUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_StoresBothForkAndUpstreamIdentity()
    {
        var manifest = new AppUpdateManifest
        {
            Version = "1.4.2.2",
            ForkVersion = "1.4.2.2",
            ForkDisplayVersion = "1.4.2-fork.2",
            UpstreamVersion = "1.4.2",
            UpstreamCommit = "upstream-sha",
            ForkCommit = "fork-sha",
            BuildNumber = "20260819.2"
        };

        Assert.Equal("1.4.2.2", AppUpdateService.GetComparableManifestVersion(manifest));
        Assert.Equal("1.4.2", manifest.UpstreamVersion);
        Assert.Equal("fork-sha", manifest.ForkCommit);
    }
}
