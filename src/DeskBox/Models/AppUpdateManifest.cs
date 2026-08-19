namespace DeskBox.Models;

public sealed class AppUpdateManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Channel { get; set; } = "stable";
    public string Version { get; set; } = string.Empty;
    /// <summary>
    /// Fork release identity. Version remains the comparable numeric value
    /// used by older clients; these fields explain which upstream base and
    /// fork commit produced the installer.
    /// </summary>
    public string ForkVersion { get; set; } = string.Empty;
    public string ForkDisplayVersion { get; set; } = string.Empty;
    public int ForkBuildNumber { get; set; }
    public string UpstreamVersion { get; set; } = string.Empty;
    public string UpstreamCommit { get; set; } = string.Empty;
    public string ForkCommit { get; set; } = string.Empty;
    public string BuildNumber { get; set; } = string.Empty;
    public string ReleaseDate { get; set; } = string.Empty;
    public string MinimumSupportedVersion { get; set; } = string.Empty;
    public bool Mandatory { get; set; }
    public string DownloadUrl { get; set; } = string.Empty;
    /// <summary>
    /// Optional architecture-specific installer metadata. Older manifests can
    /// continue to use the primary fields for a single architecture.
    /// </summary>
    public string Arm64DownloadUrl { get; set; } = string.Empty;
    public string ManualDownloadUrl { get; set; } = string.Empty;
    public string MirrorUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string Arm64Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public long Arm64Size { get; set; }
    public string ReleaseNotesUrl { get; set; } = string.Empty;
    public Dictionary<string, string> Summary { get; set; } = [];
    /// <summary>
    /// Full release notes in Markdown, keyed by locale. This is optional so
    /// older manifests remain compatible with clients that only understand
    /// the short summary fields.
    /// </summary>
    public Dictionary<string, string> ReleaseNotes { get; set; } = [];

    public string GetLocalizedSummary(string cultureName)
    {
        if (Summary.Count == 0)
        {
            return string.Empty;
        }

        if (Summary.TryGetValue(cultureName, out string? exact) &&
            !string.IsNullOrWhiteSpace(exact))
        {
            return exact;
        }

        string language = cultureName.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? cultureName;
        var languageMatch = Summary.FirstOrDefault(pair =>
            pair.Key.StartsWith(language, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(pair.Value));

        if (!string.IsNullOrWhiteSpace(languageMatch.Value))
        {
            return languageMatch.Value;
        }

        // Non-Chinese locales should never inherit a Chinese-only summary when
        // the server omits their exact translation. Prefer the English
        // fallback, then use any available value for compatibility with older
        // manifests that predate the English entry.
        if (Summary.TryGetValue("en-US", out string? english) &&
            !string.IsNullOrWhiteSpace(english))
        {
            return english;
        }

        return Summary.Values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    /// <summary>
    /// Returns the exact or language-level release note for a requested
    /// locale. This method is used when the user explicitly switches the
    /// language in the release-notes window.
    /// </summary>
    public string GetReleaseNotesForLocale(string cultureName)
    {
        if (ReleaseNotes.Count == 0 || string.IsNullOrWhiteSpace(cultureName))
        {
            return string.Empty;
        }

        if (ReleaseNotes.TryGetValue(cultureName, out string? exact) &&
            !string.IsNullOrWhiteSpace(exact))
        {
            return exact;
        }

        string language = cultureName.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? cultureName;
        var languageMatch = ReleaseNotes.FirstOrDefault(pair =>
            pair.Key.StartsWith(language, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(pair.Value));
        return languageMatch.Value ?? string.Empty;
    }

    /// <summary>
    /// Returns the best available release note for the requested locale.
    /// Exact and language-level translations are preferred, followed by
    /// Chinese for Chinese users and English for every other locale.
    /// </summary>
    public string GetLocalizedReleaseNotes(string cultureName)
    {
        string localized = GetReleaseNotesForLocale(cultureName);
        if (!string.IsNullOrWhiteSpace(localized))
        {
            return localized;
        }

        bool isChinese = cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        if (isChinese)
        {
            string chinese = GetReleaseNotesForLocale("zh-CN");
            if (!string.IsNullOrWhiteSpace(chinese))
            {
                return chinese;
            }
        }

        return GetReleaseNotesForLocale("en-US");
    }

    public bool HasReleaseNotes => ReleaseNotes.Values.Any(value => !string.IsNullOrWhiteSpace(value));

    public bool HasReleaseNotesOrUrl => HasReleaseNotes || IsSafeReleaseNotesUrl(ReleaseNotesUrl);

    public static bool IsSafeReleaseNotesUrl(string? url)
    {
        return IsSafeWebUrl(url);
    }

    public static bool IsSafeWebUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
            uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }
}

public enum AppUpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    InvalidManifest,
    NotFound,
    Failed
}

public sealed class AppUpdateCheckResult
{
    public AppUpdateCheckResult(
        AppUpdateCheckStatus status,
        string currentVersion,
        AppUpdateManifest? manifest = null,
        string? errorMessage = null)
    {
        Status = status;
        CurrentVersion = currentVersion;
        Manifest = manifest;
        ErrorMessage = errorMessage;
    }

    public AppUpdateCheckStatus Status { get; }
    public string CurrentVersion { get; }
    public AppUpdateManifest? Manifest { get; }
    public string? ErrorMessage { get; }
    public bool IsUpdateAvailable => Status == AppUpdateCheckStatus.UpdateAvailable && Manifest is not null;
}

public sealed class AppUpdateDownloadProgress
{
    public AppUpdateDownloadProgress(long bytesReceived, long? totalBytes)
    {
        BytesReceived = bytesReceived;
        TotalBytes = totalBytes;
    }

    public long BytesReceived { get; }
    public long? TotalBytes { get; }
    public double Percent =>
        TotalBytes is > 0
            ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0, 100)
            : 0;
}

public enum AppUpdateDownloadFailureKind
{
    None,
    InvalidManifest,
    HashMissing,
    HashMismatch,
    Network,
    FileSystem,
    Cancelled
}

public sealed class AppUpdateDownloadResult
{
    private AppUpdateDownloadResult(
        bool success,
        string? filePath,
        AppUpdateDownloadFailureKind failureKind,
        string? errorMessage)
    {
        Success = success;
        FilePath = filePath;
        FailureKind = failureKind;
        ErrorMessage = errorMessage;
    }

    public bool Success { get; }
    public string? FilePath { get; }
    public AppUpdateDownloadFailureKind FailureKind { get; }
    public string? ErrorMessage { get; }

    public static AppUpdateDownloadResult Completed(string filePath) =>
        new(true, filePath, AppUpdateDownloadFailureKind.None, null);

    public static AppUpdateDownloadResult Failed(AppUpdateDownloadFailureKind failureKind, string? errorMessage = null) =>
        new(false, null, failureKind, errorMessage);
}

public sealed class AppUpdateInstallResult
{
    private AppUpdateInstallResult(bool success, string? errorMessage)
    {
        Success = success;
        ErrorMessage = errorMessage;
    }

    public bool Success { get; }
    public string? ErrorMessage { get; }

    public static AppUpdateInstallResult Started() => new(true, null);

    public static AppUpdateInstallResult Failed(string errorMessage) => new(false, errorMessage);
}
