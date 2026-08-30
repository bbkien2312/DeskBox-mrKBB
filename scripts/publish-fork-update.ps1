<#
.SYNOPSIS
    Tạo gói cập nhật fork theo một hợp đồng chung cho fork1, fork2, fork3...

.DESCRIPTION
    Script này dùng cùng một quy trình cho mọi fork build:
    1. Build/publish DeskBox và biên dịch installer.
    2. Tạo file SHA256 và stable.json.
    3. Có thể tạo GitHub Release, upload installer và commit manifest.

    stable.json chỉ được commit sau khi GitHub Release đã có asset. Nhờ đó
    nút Update không quảng bá một URL installer chưa tồn tại.
#>
[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [ValidateSet("x86", "x64", "ARM64")]
    [string[]]$Platform = @("x64", "ARM64", "x86"),

    [int]$ForkBuildNumber = 0,

    [string]$ForkVersion = "",

    [string]$ForkCommit = "",

    [string]$Repository = "bbkien2312/DeskBox-mrKBB",

    [string]$ReleaseTag = "",

    [string]$InstallerPath = "",

    [string[]]$AdditionalAssetPath = @(),

    [string]$VietnameseReleaseNotes = "",

    [string]$EnglishReleaseNotes = "",

    [switch]$BuildOfflinePrerequisites,

    [switch]$SkipBuild,

    [switch]$PublishGitHubRelease,

    [switch]$CommitAndPushManifest
)

$ErrorActionPreference = "Stop"

function Get-ProjectProperty {
    param(
        [System.Xml.XmlDocument]$Project,
        [string]$Name,
        [string]$Fallback
    )

    $node = $Project.SelectSingleNode("//*[local-name()='$Name']")
    if ($null -ne $node -and -not [string]::IsNullOrWhiteSpace($node.InnerText)) {
        return $node.InnerText.Trim()
    }

    return $Fallback
}

function Invoke-NativeChecked {
    param(
        [string]$FilePath,
        [string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Lệnh '$FilePath' thất bại với mã $LASTEXITCODE."
    }
}

function Get-GitCommit {
    param(
        [string]$RepositoryRoot
    )

    $commit = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
        return "unknown"
    }

    return $commit
}

function Get-ReleaseAssetName {
    param(
        [string]$Version,
        [string]$Architecture
    )

    $suffix = Get-ArchitectureSuffix -Architecture $Architecture
    return "DeskBox_Setup_${Version}_${suffix}.exe"
}

function Get-ArchitectureSuffix {
    param([string]$Architecture)

    switch ($Architecture) {
        "x86" { return "x86" }
        "x64" { return "x64" }
        "ARM64" { return "arm64" }
        default { throw "Kiến trúc không hợp lệ: $Architecture" }
    }
}

function Get-ReleaseApiHeaders {
    $token = [Environment]::GetEnvironmentVariable("GITHUB_TOKEN")
    if ([string]::IsNullOrWhiteSpace($token)) {
        $token = [Environment]::GetEnvironmentVariable("GH_TOKEN")
    }

    if ([string]::IsNullOrWhiteSpace($token)) {
        throw "Thiếu GITHUB_TOKEN hoặc GH_TOKEN. Chỉ dùng -PublishGitHubRelease khi đã cấp token có quyền tạo Release."
    }

    return @{
        Authorization = "Bearer $token"
        Accept = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
        "User-Agent" = "DeskBox-Fork-Release"
    }
}

function Publish-GitHubRelease {
    param(
        [string]$Repo,
        [string]$Tag,
        [string[]]$AssetPaths,
        [string]$ReleaseName,
        [hashtable]$Headers
    )

    $apiBase = "https://api.github.com/repos/$Repo"
    $releaseBody = @{
        tag_name = $Tag
        name = $ReleaseName
        body = "DeskBox fork update $ReleaseName. Installer được build từ commit hiện tại của fork."
        draft = $false
        prerelease = $false
    } | ConvertTo-Json
    $releaseBodyBytes = [System.Text.Encoding]::UTF8.GetBytes($releaseBody)

    try {
        $release = Invoke-RestMethod -Method Get -Uri "$apiBase/releases/tags/$Tag" -Headers $Headers
    }
    catch {
        if ($_.Exception.Response.StatusCode.value__ -ne 404) {
            throw
        }

        $release = Invoke-RestMethod -Method Post -Uri "$apiBase/releases" -Headers $Headers -ContentType "application/json; charset=utf-8" -Body $releaseBodyBytes
    }

    foreach ($assetPathToUpload in $AssetPaths) {
        $assetName = [System.IO.Path]::GetFileName($assetPathToUpload)
        $existing = @($release.assets | Where-Object { $_.name -eq $assetName })
        foreach ($asset in $existing) {
            Invoke-RestMethod -Method Delete -Uri $asset.url -Headers $Headers | Out-Null
        }

        $uploadUri = $release.upload_url -replace '\{\?name,label\}$', ""
        $uploadUri = "${uploadUri}?name=$([Uri]::EscapeDataString($assetName))"
        Invoke-RestMethod -Method Post -Uri $uploadUri -Headers $Headers -ContentType "application/octet-stream" -InFile $assetPathToUpload | Out-Null
    }

    return $release
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectPath = Join-Path $repoRoot "src\DeskBox\DeskBox.csproj"
$project = New-Object System.Xml.XmlDocument
$project.Load($projectPath)

$upstreamVersion = Get-ProjectProperty -Project $project -Name "DeskBoxUpstreamVersion" -Fallback "unknown"
if ([string]::IsNullOrWhiteSpace($ForkVersion)) {
    $ForkVersion = Get-ProjectProperty -Project $project -Name "DeskBoxForkVersion" -Fallback "1.4.2.1"
}

if ($ForkBuildNumber -le 0) {
    $ForkBuildNumber = [int](Get-ProjectProperty -Project $project -Name "DeskBoxForkBuildNumber" -Fallback "1")
}

$protocolVersion = [int](Get-ProjectProperty -Project $project -Name "DeskBoxUpdaterProtocolVersion" -Fallback "1")
if ([string]::IsNullOrWhiteSpace($ForkCommit)) {
    $forkCommit = Get-GitCommit -RepositoryRoot $repoRoot
}
else {
    $forkCommit = $ForkCommit.Trim()
}
$displayVersion = "$ForkVersion-fork.$ForkBuildNumber"
$tag = if ([string]::IsNullOrWhiteSpace($ReleaseTag)) { "v$displayVersion" } else { $ReleaseTag.Trim() }
$outputRoot = Join-Path $repoRoot "Output"
$installerRoot = Join-Path $repoRoot "installer"
$requestedPlatforms = @($Platform | Select-Object -Unique)
if ($requestedPlatforms.Count -eq 0) {
    throw "At least one Platform is required."
}

if (-not [string]::IsNullOrWhiteSpace($InstallerPath) -and $requestedPlatforms.Count -ne 1) {
    throw "-InstallerPath is only valid for one Platform; multi-architecture releases use standard installer names."
}

$innoCandidates = @(
    (Join-Path $repoRoot ".tools\inno\ISCC.exe"),
    (Join-Path (Split-Path $repoRoot -Parent) ".tools\inno\ISCC.exe")
)
$iscc = $innoCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $SkipBuild -and [string]::IsNullOrWhiteSpace($iscc)) {
    throw "Inno Setup compiler was not found. Checked: $($innoCandidates -join '; ')"
}

$installerRecords = @()
foreach ($targetPlatform in $requestedPlatforms) {
    $architectureSuffix = Get-ArchitectureSuffix -Architecture $targetPlatform
    $assetName = Get-ReleaseAssetName -Version $ForkVersion -Architecture $targetPlatform
    $publishRoot = Join-Path $repoRoot "artifacts\publish\DeskBox\$architectureSuffix"
    $installerScript = switch ($targetPlatform) {
        "x86" { Join-Path $installerRoot "DeskBox.x86.iss" }
        "ARM64" { Join-Path $installerRoot "DeskBox.arm64.iss" }
        default { Join-Path $installerRoot "DeskBox.iss" }
    }

    if (-not $SkipBuild) {
        Invoke-NativeChecked -FilePath "dotnet" -ArgumentList @(
            "publish", $projectPath,
            "--configuration", $Configuration,
            "-p:Platform=$targetPlatform",
            "-p:RuntimeIdentifier=win-$architectureSuffix",
            "-p:SelfContained=false",
            "-p:WindowsAppSDKSelfContained=false",
            "-p:DeskBoxBuildNumber=$ForkBuildNumber",
            "-p:DeskBoxForkCommit=$forkCommit",
            "-o", $publishRoot,
            "-v:minimal")
        Invoke-NativeChecked -FilePath $iscc -ArgumentList @($installerScript)
    }

    $resolvedInstallerPath = if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
        Join-Path $outputRoot $assetName
    }
    elseif ([System.IO.Path]::IsPathRooted($InstallerPath)) {
        $InstallerPath
    }
    else {
        Join-Path $repoRoot $InstallerPath
    }

    $resolvedInstallerPath = [System.IO.Path]::GetFullPath($resolvedInstallerPath)
    if (-not (Test-Path $resolvedInstallerPath -PathType Leaf)) {
        throw "Installer ${architectureSuffix} was not found: $resolvedInstallerPath"
    }

    $actualName = [System.IO.Path]::GetFileName($resolvedInstallerPath)
    if ($actualName -ne $assetName) {
        throw "Installer $architectureSuffix must be named '$assetName'; current name is '$actualName'."
    }

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = ([BitConverter]::ToString($sha256.ComputeHash([System.IO.File]::ReadAllBytes($resolvedInstallerPath)))).Replace("-", "")
    }
    finally {
        $sha256.Dispose()
    }

    $size = (Get-Item $resolvedInstallerPath).Length
    $shaPath = "$resolvedInstallerPath.sha256"
    "$hash  $assetName" | Set-Content -Path $shaPath -Encoding ASCII
    $installerRecords += [PSCustomObject]@{
        Platform = $targetPlatform
        Suffix = $architectureSuffix
        AssetName = $assetName
        Path = $resolvedInstallerPath
        ShaPath = $shaPath
        Sha256 = $hash
        Size = $size
    }
}

if ($BuildOfflinePrerequisites) {
    if ($requestedPlatforms -notcontains "x64") {
        throw "Offline prerequisites currently support x64 only; include Platform x64."
    }

    $preparePrerequisitesScript = Join-Path $repoRoot "scripts\prepare-offline-prerequisites.ps1"
    Invoke-NativeChecked -FilePath "powershell.exe" -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", $preparePrerequisitesScript,
        "-Platform", "x64")
    $offlineInstallerScript = Join-Path $installerRoot "DeskBox.Prerequisites.iss"
    Invoke-NativeChecked -FilePath $iscc -ArgumentList @($offlineInstallerScript)
    $AdditionalAssetPath += (Join-Path $outputRoot "DeskBox_Prerequisites_${ForkVersion}_x64.exe")
}

$releaseAssetPaths = @()
foreach ($installerRecord in $installerRecords) {
    $releaseAssetPaths += @($installerRecord.Path, $installerRecord.ShaPath)
}
foreach ($additionalPath in $AdditionalAssetPath) {
    $resolvedAdditionalPath = if ([System.IO.Path]::IsPathRooted($additionalPath)) {
        [System.IO.Path]::GetFullPath($additionalPath)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $repoRoot $additionalPath))
    }

    if (-not (Test-Path $resolvedAdditionalPath -PathType Leaf)) {
        throw "Không tìm thấy release asset bổ sung: $resolvedAdditionalPath"
    }

    $additionalName = [System.IO.Path]::GetFileName($resolvedAdditionalPath)
    $additionalSha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $additionalHash = ([BitConverter]::ToString($additionalSha.ComputeHash([System.IO.File]::ReadAllBytes($resolvedAdditionalPath)))).Replace("-", "")
    }
    finally {
        $additionalSha.Dispose()
    }

    $additionalShaPath = "$resolvedAdditionalPath.sha256"
    "$additionalHash  $additionalName" | Set-Content -Path $additionalShaPath -Encoding ASCII
    $releaseAssetPaths += @($resolvedAdditionalPath, $additionalShaPath)
}

# Windows PowerShell 5 có thể đọc file script UTF-8 không BOM theo code page
# hệ thống. Dựng câu tiếng Việt từ HTML entities để manifest không bị lỗi dấu.
$viSummary = [System.Net.WebUtility]::HtmlDecode(
    "DeskBox $displayVersion &#x0111;&#x00E3; s&#x1EB5;n s&#x00E0;ng c&#x1EAD;p nh&#x1EAD;t.")
$viReleaseNotes = [System.Net.WebUtility]::HtmlDecode(
    "S&#x1EED;a lu&#x1ED3;ng ch&#x1EE5;p Desktop/to&#x00E0;n m&#x00E0;n h&#x00EC;nh v&#x00E0; ph&#x00E1;t h&#x00E0;nh installer theo ki&#x1EBF;n tr&#x00FA;c x86, x64, ARM64.")

if (-not [string]::IsNullOrWhiteSpace($VietnameseReleaseNotes)) {
    $viReleaseNotes = $VietnameseReleaseNotes.Trim()
}

if ([string]::IsNullOrWhiteSpace($EnglishReleaseNotes)) {
    $EnglishReleaseNotes = "Fixes frozen desktop capture before its overlay is shown and publishes architecture-aware x86, x64, and ARM64 installers."
}

$primaryInstaller = $installerRecords | Where-Object { $_.Suffix -eq "x64" } | Select-Object -First 1
if ($null -eq $primaryInstaller) {
    $primaryInstaller = $installerRecords | Select-Object -First 1
}
$arm64Installer = $installerRecords | Where-Object { $_.Suffix -eq "arm64" } | Select-Object -First 1
$installerManifest = [ordered]@{}
foreach ($installerRecord in $installerRecords) {
    $installerManifest[$installerRecord.Suffix] = [ordered]@{
        downloadUrl = "https://github.com/$Repository/releases/download/$tag/$([Uri]::EscapeDataString($installerRecord.AssetName))"
        sha256 = $installerRecord.Sha256
        size = $installerRecord.Size
    }
}

$downloadUrl = "https://github.com/$Repository/releases/download/$tag/$([Uri]::EscapeDataString($primaryInstaller.AssetName))"
$manifest = [ordered]@{
    schemaVersion = 1
    updaterProtocolVersion = $protocolVersion
    channel = "stable"
    version = $ForkVersion
    forkVersion = $ForkVersion
    forkDisplayVersion = $displayVersion
    forkBuildNumber = $ForkBuildNumber
    upstreamVersion = $upstreamVersion
    forkCommit = $forkCommit
    buildNumber = $displayVersion
    releaseDate = [DateTimeOffset]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
    minimumSupportedVersion = "1.4.2.1"
    mandatory = $false
    downloadUrl = $downloadUrl
    installers = $installerManifest
    manualDownloadUrl = "https://github.com/$Repository/releases/latest"
    mirrorUrl = "https://github.com/$Repository/releases/latest"
    sha256 = $primaryInstaller.Sha256
    size = $primaryInstaller.Size
    minimumWindowsBuild = 19044
    releaseNotesUrl = "https://github.com/$Repository/releases/tag/$tag"
    summary = [ordered]@{
        "vi-VN" = $viSummary
        "en-US" = "DeskBox $displayVersion is ready to update."
    }
    releaseNotes = [ordered]@{
        "vi-VN" = $viReleaseNotes
        "en-US" = $EnglishReleaseNotes.Trim()
    }
}

if ($null -ne $arm64Installer) {
    $manifest.arm64DownloadUrl = "https://github.com/$Repository/releases/download/$tag/$([Uri]::EscapeDataString($arm64Installer.AssetName))"
    $manifest.arm64Sha256 = $arm64Installer.Sha256
    $manifest.arm64Size = $arm64Installer.Size
}

$artifactManifestRoot = Join-Path $repoRoot "artifacts\release\$displayVersion"
New-Item -ItemType Directory -Path $artifactManifestRoot -Force | Out-Null
$manifestPath = Join-Path $artifactManifestRoot "stable.json"
$manifestJson = $manifest | ConvertTo-Json -Depth 8
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, $utf8NoBom)

if ($PublishGitHubRelease) {
    $headers = Get-ReleaseApiHeaders
    $release = Publish-GitHubRelease `
        -Repo $Repository `
        -Tag $tag `
        -AssetPaths $releaseAssetPaths `
        -ReleaseName $displayVersion `
        -Headers $headers

    $manifestPathForRepo = Join-Path $repoRoot "release\stable.json"
    New-Item -ItemType Directory -Path (Split-Path $manifestPathForRepo -Parent) -Force | Out-Null
    [System.IO.File]::WriteAllText($manifestPathForRepo, $manifestJson, $utf8NoBom)

    if ($CommitAndPushManifest) {
        Push-Location $repoRoot
        try {
            git add release/stable.json
            git commit -m "release: publish $displayVersion update manifest"
            if ($LASTEXITCODE -ne 0) {
                throw "Không thể commit manifest."
            }

            git push fork main
            if ($LASTEXITCODE -ne 0) {
                throw "Không thể push manifest lên fork/main."
            }
        }
        finally {
            Pop-Location
        }
    }
}

Write-Host "Đã tạo metadata cập nhật:" -ForegroundColor Green
Write-Host "  Version:       $displayVersion"
Write-Host "  Protocol:      $protocolVersion"
Write-Host "  Release tag:   $tag"
foreach ($installerRecord in $installerRecords) {
    Write-Host "  Installer $($installerRecord.Suffix): $($installerRecord.Path)"
    Write-Host "  SHA256 $($installerRecord.Suffix):    $($installerRecord.Sha256)"
}
Write-Host "  Manifest:      $manifestPath"
if ($PublishGitHubRelease) {
    Write-Host "  GitHub Release: đã upload $tag"
    if ($CommitAndPushManifest) {
        Write-Host "  stable.json:   đã commit và push"
    }
}
else {
    Write-Host "  GitHub Release: chưa upload (dùng -PublishGitHubRelease khi đã cấp token)" -ForegroundColor Yellow
}
