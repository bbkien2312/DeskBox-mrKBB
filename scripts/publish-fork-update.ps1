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

    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",

    [int]$ForkBuildNumber = 0,

    [string]$ForkVersion = "",

    [string]$Repository = "bbkien2312/DeskBox-mrKBB",

    [string]$ReleaseTag = "",

    [string]$InstallerPath = "",

    [string[]]$AdditionalAssetPath = @(),

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
    $commit = (& git rev-parse HEAD).Trim()
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

    $suffix = if ($Architecture -eq "ARM64") { "arm64" } else { "x64" }
    return "DeskBox_Setup_${Version}_${suffix}.exe"
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
$forkCommit = Get-GitCommit
$displayVersion = "$ForkVersion-fork.$ForkBuildNumber"
$tag = if ([string]::IsNullOrWhiteSpace($ReleaseTag)) { "v$displayVersion" } else { $ReleaseTag.Trim() }
$architectureSuffix = if ($Platform -eq "ARM64") { "arm64" } else { "x64" }
$assetName = Get-ReleaseAssetName -Version $ForkVersion -Architecture $Platform

$publishRoot = Join-Path $repoRoot "artifacts\publish\DeskBox\$($architectureSuffix)"
$outputRoot = Join-Path $repoRoot "Output"
$installerRoot = Join-Path $repoRoot "installer"
$installerScript = if ($Platform -eq "ARM64") {
    Join-Path $installerRoot "DeskBox.arm64.iss"
}
else {
    Join-Path $installerRoot "DeskBox.iss"
}

if (-not $SkipBuild) {
    $dotnet = "dotnet"
    $dotnetArguments = @(
        "publish",
        $projectPath,
        "--configuration", $Configuration,
        "-p:Platform=$Platform",
        "-p:RuntimeIdentifier=win-$($architectureSuffix)",
        "-p:SelfContained=false",
        "-p:WindowsAppSDKSelfContained=false",
        "-p:DeskBoxBuildNumber=$ForkBuildNumber",
        "-p:DeskBoxForkCommit=$forkCommit",
        "-o", $publishRoot,
        "-v:minimal"
    )
    Invoke-NativeChecked -FilePath $dotnet -ArgumentList $dotnetArguments

    $innoCandidates = @(
        (Join-Path $repoRoot ".tools\inno\ISCC.exe"),
        (Join-Path (Split-Path $repoRoot -Parent) ".tools\inno\ISCC.exe")
    )
    $iscc = $innoCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($iscc)) {
        throw "Không tìm thấy Inno Setup compiler. Đã kiểm tra: $($innoCandidates -join '; ')"
    }

    Invoke-NativeChecked -FilePath $iscc -ArgumentList @($installerScript)

    if ($BuildOfflinePrerequisites) {
        if ($Platform -ne "x64") {
            throw "Gói prerequisites offline hiện mới hỗ trợ x64. Không thể build với Platform=$Platform."
        }

        $preparePrerequisitesScript = Join-Path $repoRoot "scripts\prepare-offline-prerequisites.ps1"
        Invoke-NativeChecked -FilePath "powershell.exe" -ArgumentList @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", $preparePrerequisitesScript,
            "-Platform", "x64")

        $offlineInstallerScript = Join-Path $installerRoot "DeskBox.Prerequisites.iss"
        Invoke-NativeChecked -FilePath $iscc -ArgumentList @($offlineInstallerScript)
        $AdditionalAssetPath += (Join-Path $outputRoot "DeskBox_Prerequisites_${ForkVersion}_x64.exe")
    }
}

if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = Join-Path $outputRoot $assetName
}
elseif (-not [System.IO.Path]::IsPathRooted($InstallerPath)) {
    $InstallerPath = Join-Path $repoRoot $InstallerPath
}

$InstallerPath = [System.IO.Path]::GetFullPath($InstallerPath)
if (-not (Test-Path $InstallerPath -PathType Leaf)) {
    throw "Không tìm thấy installer: $InstallerPath"
}

$actualName = [System.IO.Path]::GetFileName($InstallerPath)
if ($actualName -ne $assetName) {
    Write-Warning "Tên installer hiện tại là '$actualName'; manifest sẽ dùng tên asset '$assetName'. Hãy đổi tên hoặc truyền -InstallerPath đúng bản fork."
    $assetName = $actualName
}

$sha256 = [System.Security.Cryptography.SHA256]::Create()
try {
    # Get-FileHash is absent on a few stripped-down PowerShell hosts. Use the
    # BCL directly so the same release script works on those machines too.
    $hash = ([BitConverter]::ToString($sha256.ComputeHash([System.IO.File]::ReadAllBytes($InstallerPath)))).Replace("-", "")
}
finally {
    $sha256.Dispose()
}
$size = (Get-Item $InstallerPath).Length
$shaPath = "$InstallerPath.sha256"
"$hash  $assetName" | Set-Content -Path $shaPath -Encoding ASCII

$releaseAssetPaths = @($InstallerPath, $shaPath)
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
    "C&#x1EA3;i thi&#x1EC7;n c&#x00E0;i &#x0111;&#x1EB7;t Windows 10/11: ki&#x1EC3;m tra r&#x00F5; Windows x64, b&#x1ED5; sung Visual C++ Runtime, log c&#x00E0;i &#x0111;&#x1EB7;t v&#x00E0; g&#x00F3;i prerequisite offline t&#x00E1;ch ri&#x00EA;ng.")

$downloadUrl = "https://github.com/$Repository/releases/download/$tag/$([Uri]::EscapeDataString($assetName))"
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
    manualDownloadUrl = "https://github.com/$Repository/releases/latest"
    mirrorUrl = "https://github.com/$Repository/releases/latest"
    sha256 = $hash
    size = $size
    releaseNotesUrl = "https://github.com/$Repository/releases/tag/$tag"
    summary = [ordered]@{
        "vi-VN" = $viSummary
        "en-US" = "DeskBox $displayVersion is ready to update."
    }
    releaseNotes = [ordered]@{
        "vi-VN" = $viReleaseNotes
        "en-US" = "Improves Windows 10/11 setup with x64 preflight, Visual C++ runtime installation, setup logs, and a separate offline prerequisites package."
    }
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
Write-Host "  Installer:     $InstallerPath"
Write-Host "  SHA256:        $hash"
Write-Host "  SHA file:      $shaPath"
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
