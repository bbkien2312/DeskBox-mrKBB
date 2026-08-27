<#
.SYNOPSIS
    Tải các runtime chính thức để dựng gói DeskBox prerequisites offline.

.DESCRIPTION
    File tải được đặt dưới artifacts\prerequisites, bị .gitignore bỏ qua và
    tuyệt đối không commit binary vendor vào source. Gói tạo ra chỉ dùng khi
    máy đích không cài được runtime bằng installer online.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = 'x64',

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$architecture = if ($Platform -eq 'ARM64') { 'arm64' } else { 'x64' }
$targetRoot = Join-Path $repoRoot "artifacts\prerequisites\$architecture"
New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null

function Get-PrerequisiteFile {
    param(
        [string]$Name,
        [string]$Url
    )

    $target = Join-Path $targetRoot $Name
    if ((Test-Path -LiteralPath $target -PathType Leaf) -and -not $Force) {
        if ((Get-Item -LiteralPath $target).Length -gt 0) {
            Write-Host "Dùng lại prerequisite đã tải: $Name"
            return $target
        }

        Write-Warning "Bỏ file prerequisite rỗng/tải dở: $Name"
        Remove-Item -LiteralPath $target -Force
    }

    Write-Host "Đang tải $Name từ $Url"
    Invoke-WebRequest -Uri $Url -OutFile $target -MaximumRedirection 10 -UseBasicParsing
    if ((Get-Item -LiteralPath $target).Length -eq 0) {
        throw "Tải $Name thất bại: file rỗng."
    }

    return $target
}

$files = @(
    @{ Name = "dotnet-runtime-10-win-$architecture.exe"; Url = "https://aka.ms/dotnet/10.0/dotnet-runtime-win-$architecture.exe" },
    @{ Name = "WindowsAppRuntimeInstall-$architecture.exe"; Url = "https://aka.ms/windowsappsdk/2.2/2.2.0/windowsappruntimeinstall-$architecture.exe" },
    @{ Name = "vc_redist.$architecture.exe"; Url = "https://aka.ms/vs/17/release/vc_redist.$architecture.exe" }
)

foreach ($file in $files) {
    $path = Get-PrerequisiteFile -Name $file.Name -Url $file.Url
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    $sizeMiB = [Math]::Round((Get-Item -LiteralPath $path).Length / 1MB, 2)
    Write-Host "  $($file.Name): $sizeMiB MiB, SHA-256 $hash"
}
