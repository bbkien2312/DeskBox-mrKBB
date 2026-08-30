; DeskBox x86 installer. The shared setup script retains the same AppId and
; migration safeguards; only native payload and prerequisite architecture vary.
#define MyAppRuntimeArchitecture "x86"
#define MyAppOutputArchitectureSuffix "x86"
#define MyAppArchitecturesAllowed "x86compatible"
#define MyAppUse64BitInstallMode 0
#define MyAppReleaseDir "..\artifacts\publish\DeskBox\x86"
#define DeskBoxDependencyArchitecture "x86"
#define DeskBoxDependencyPackageArchitecture "X86"
#define DeskBoxDependencyVcRegistryRoot HKLM
#define DeskBoxDependencyRequires64Bit 0
#define DeskBoxDependencyDotNetRuntimeUrl "https://aka.ms/dotnet/10.0/dotnet-runtime-win-x86.exe"
#define DeskBoxDependencyDotNetRuntimeFallbackUrl "https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.11/dotnet-runtime-10.0.11-win-x86.exe"
#define DeskBoxDependencyDotNetRuntimeInstallerName "dotnet-runtime-10-win-x86.exe"
#define DeskBoxDependencyWindowsAppRuntimeUrl "https://download.microsoft.com/download/5e0f2e92-f3ef-4023-97f0-bd57018a478c/WindowsAppRuntimeInstall-x86.exe"
#define DeskBoxDependencyWindowsAppRuntimeFallbackUrl "https://aka.ms/windowsappsdk/2.2/2.2.0/windowsappruntimeinstall-x86.exe"
#define DeskBoxDependencyWindowsAppRuntimeInstallerName "WindowsAppRuntimeInstall-x86.exe"
#define DeskBoxDependencyVcRedistUrl "https://aka.ms/vs/17/release/vc_redist.x86.exe"
#define DeskBoxDependencyVcRedistFallbackUrl "https://aka.ms/vs/17/release/vc_redist.x86.exe"
#define DeskBoxDependencyVcRedistInstallerName "vc_redist.x86.exe"
#include "DeskBox.iss"
