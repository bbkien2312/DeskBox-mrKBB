# DeskBox

> Ghi chú fork (29/08/2026, Fork 9): khi chụp **một cửa sổ đã chọn**, fork dùng Windows Graphics Capture theo HWND để ảnh không bị các cửa sổ khác che. Chụp Desktop/toàn màn hình/kéo vùng tự do vẫn giữ snapshot cũ. Installer x64 đã phát hành tại tag `v1.4.2.1-fork.9`; chi tiết triển khai và kiểm thử được ghi bằng tiếng Việt trong `AGENTS.md` của fork.

**A free, open-source Windows desktop organizer with native-feeling WinUI 3 widgets.**

English | [简体中文](README.zh-CN.md)

[![CI](https://github.com/Tianyu199509/DeskBox/actions/workflows/ci.yml/badge.svg)](https://github.com/Tianyu199509/DeskBox/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/badge/release-1.4.2-2563EB.svg)](https://github.com/Tianyu199509/DeskBox/releases/tag/v1.4.2)
[![Windows 10/11](https://img.shields.io/badge/Windows-10%2F11-0078D4.svg)](#system-requirements)
[![x64 and ARM64](https://img.shields.io/badge/architecture-x64%20%7C%20ARM64-5C2D91.svg)](#download)
[![License: GPL v3](https://img.shields.io/badge/license-GPLv3-blue.svg)](LICENSE)

![DeskBox Windows desktop organizer with file, todo, search, weather, and music widgets](docs/images/brand/readme-hero-1-3-7-dark-en.png)

DeskBox organizes desktop files, maps existing folders, and keeps everyday tools close without replacing Explorer or changing how your files work. Its real-folder-backed widgets make it a modern open-source alternative to tools such as Stardock Fences, while todos, quick notes, search, weather, and music controls remain useful extras rather than the product's core promise.

## Ghi chú cho fork mrKBB

Fork duy trì tại <https://github.com/bbkien2312/DeskBox-mrKBB> bổ sung các chức năng ưu tiên cho Desktop organizer: phân loại tự động theo type/folder, log tổ chức, cache thumbnail trên ổ đĩa, khôi phục nội dung box khi thoát và chụp màn hình nhanh.

- `Ctrl+Alt+S`: mở lớp chụp màn hình đóng băng; click chọn một cửa sổ, kéo chuột chọn vùng tự do, hoặc chọn Desktop/taskbar để chụp toàn màn hình.
- Sau khi vùng chụp đã khóa, nhấn `Ctrl+C` hoặc nút **Sao chép (Ctrl+C)** để đưa ảnh vào Clipboard Windows. Không có hotkey chụp toàn cục mới và không tạo thêm cache ảnh trong RAM.
- Mọi bản fork phát hành phải tăng riêng `forkBuildNumber`, upload installer/checksum lên GitHub Release và cập nhật `release/stable.json`; không dùng số phiên bản upstream để quyết định update.

Bản hiện hành là [1.4.2.1-fork.8](https://github.com/bbkien2312/DeskBox-mrKBB/releases/tag/v1.4.2.1-fork.8). Installer x64 online có SHA-256 `66208975EC78986B8EF1EA39B4DBD8A30572A030DC793F572544E0B50958AAF6`; nó tự tải dependency còn thiếu theo cơ chế setup hiện có.

## Mica and Acrylic on the desktop

DeskBox uses native-feeling Windows materials and keeps ordinary desktop files and folders in place.

| Mica | Acrylic |
| --- | --- |
| ![DeskBox desktop widgets with Mica material in English](docs/images/screenshots/en-us/云母材质.png) | ![DeskBox desktop widgets with Acrylic material in English](docs/images/screenshots/en-us/亚克力材质.png) |

## DeskBox at a glance

| | |
| --- | --- |
| **Platform** | Windows 10/11, x64 and ARM64 |
| **Technology** | C#, WinUI 3, .NET 10, Windows App SDK 2.2 |
| **Storage model** | Local-first; files, notes, tasks, settings, and layouts remain on the PC |
| **Languages** | English, Simplified Chinese, Japanese, German, Brazilian Portuguese, Hindi, Spanish, French, Arabic, Bengali, Russian |
| **License** | GPL-3.0-only |

The six newer language packs prioritize the main file-widget and onboarding flows. A small number of detailed settings still use English while their translations are completed.

## Download

The current stable release is DeskBox 1.4.2, available from [GitHub Releases](https://github.com/Tianyu199509/DeskBox/releases/tag/v1.4.2).

- [DeskBox 1.4.2 for x64](https://github.com/Tianyu199509/DeskBox/releases/download/v1.4.2/DeskBox_Setup_1.4.2_x64.exe), for most Intel and AMD PCs.
- [DeskBox 1.4.2 for ARM64](https://github.com/Tianyu199509/DeskBox/releases/download/v1.4.2/DeskBox_Setup_1.4.2_arm64.exe), for Snapdragon, Surface Pro X, and other Windows on ARM PCs.

The installers are framework-dependent, so they stay smaller and do not bundle a private runtime. Setup checks the matching architecture of .NET 10 Runtime and Windows App Runtime 2.2. An existing compatible runtime is reused; a missing dependency is downloaded and installed during setup.

> Internet access is needed only when setup must download a missing runtime. Windows may request administrator permission for that dependency installation; DeskBox itself installs for the current user by default.

## Features

### File organizer and folder widgets

- Create managed file widgets backed by ordinary folders, or map an existing folder without moving it.
- Use icon or list layouts, title styles, detail and path controls, manual or rule-based sorting, auto stacks, adjustable icon sizes, and compact display density.
- Reorder items directly, move or copy them into a folder item, and create a folder with automatic scrolling and inline naming. Manual order is restored after restart.
- Drag files and shortcuts in or out, copy, cut, paste, rename, delete, reveal in Explorer, and use Shell-compatible shortcut behavior when dragging to the Windows desktop.
- Drop content from Explorer, WeChat, or a browser; remote image and file URLs can be downloaded and imported.
- Preview supported files through a running [QuickLook](https://github.com/QL-Win/QuickLook) instance by pressing Space.

### Widget groups and desktop organization

- Merge file widgets into a group without changing their backing folders, then switch members from the title, mouse wheel, or cyclic Ctrl+Tab shortcut.
- Detach a member or dissolve a group safely; grouped and standalone file widgets share the same views, settings, menus, sorting, drag-and-drop, and QuickLook behavior.
- Preview desktop organization by category before moving anything, and choose whether each category creates a folder or reuses an existing widget.
- Optionally organize new desktop files after downloads, extraction, and same-path replacements reach a stable state.

### Todo and Quick Capture

- Work in responsive Todo and Quick Capture list/detail layouts that switch between single- and dual-pane modes, with an adjustable master pane on wide widgets.
- Track tasks with due dates, reminders, recurrence, color markers, Markdown notes, attachments, filters, and batch actions.
- Save reusable text, links, images, and files in Quick Capture with pinning, paper styles, Markdown editing and preview, removable attachments, and focused editing.
- Keep attachment files linked to their original location or copy them into DeskBox-managed storage.

### Desktop search

- Search files, folders, applications, settings, and DeskBox content from one popup or search widget.
- Combine the Windows index with an optional local USN-based file index.
- Use configurable filters, sortable detail columns, result limits, history, favorites, and a global search hotkey.
- Select multiple rows with Ctrl or Shift, drag a selection rectangle with edge auto-scroll, and apply batch actions to the result set.
- Receive staged incremental results while individual search providers remain isolated from one another if a source fails.
- The popup shell is warmed during idle time so a widget click can show and focus it first, while recommendations, icons, and an idle-unloaded local index recover in the background.
- The resident local index can unload after search has been idle while lightweight file watchers continue tracking changes; disabling Search releases the complete search runtime.

### Weather and music

- View current conditions plus hourly and multi-day forecasts with MSN Weather and automatic Open-Meteo fallback.
- Choose a theme-aware Standard weather skin or the richer condition-based skin, with responsive Day and Week views across widget sizes.
- Control the active Windows media session, playback mode, progress, and system volume from the music widget.
- Use responsive cover, controls, record, and compact layouts with optional album-color ambience.

### Capsule mode and native Windows behavior

- Collapse widgets into smart capsules with click-to-toggle or hover-to-expand behavior.
- Show key information, a short summary, or only an icon and title; hide sensitive Todo and Quick Capture text while collapsed.
- Arrange capsules independently or combine them into a movable, ordered bar.
- Raise or hide all widgets from the tray or a configurable global hotkey, with serialized repeated-toggle handling and recovery across display, DPI, sleep, and Explorer changes.
- Customize Mica/acrylic materials, opacity, borders, DWM corners, animation, title bars, icon size, and text size.

### Updates, backup, and diagnostics

- Check for updates in the app, read long release notes in a dedicated view, retry failed downloads, or continue from the official website.
- Start a visible installer after DeskBox closes; upgrades reuse and lock the existing installation path instead of creating a second copy.
- Back up and restore settings, and export a privacy-filtered diagnostics package for troubleshooting.
- Recover settings from resilient snapshots, flush pending changes during shutdown, and report save failures instead of silently reverting to defaults.

## What's new in 1.4.2

- **More controllable file stacks.** Manual stacks remain available without automatic stacking. Files can be dragged in from Explorer, browsers, and other widgets, then removed or reordered without changing the whole widget's sorting.
- **More predictable capsules.** Choose automatic, downward, or upward expansion. Fixed directions keep the title edge anchored, while hover recovery and F7 restore no longer leave shifted widgets or stale drag masks.
- **Desktop-compatible file launching.** Items opened from file widgets receive the same user environment as desktop launches. QuickLook navigation can continue into an adjacent visible file widget.
- **Reliable Todo and Quick Capture.** Todo checkmarks stay with the intended task. Clipboard images show thumbnails and copy back as images, attachments use scrollable tiles, and Quick Capture search stays in the tab row.
- **Smoother widget groups.** One wheel step changes one member and plays one highlight, rapid input remains responsive, and first-to-last circular navigation stays available.
- **Windows-style music controls.** Play, pause, previous, and next use matching filled transport icons. Cover mode receives a clearer single-line frosted control bar.
- **Clearer first use and editing.** Localized Todo and Quick Capture guides return after a reset, onboarding uses a calmer transition, and Markdown remains readable in light and dark themes.
- **Refined interface details.** Desktop-layer widgets remain behind other apps when clicked. Title-bar add buttons share one widget-creation menu, while empty states, settings spacing, search refreshes, and compact controls are more consistent.

Read the complete [changelog](CHANGELOG.md) or the [1.4.2 release notes](docs/releases/v1.4.2.md).

## Current interface

These screenshots are representative of the current DeskBox settings interface.

### Settings

| General | Appearance |
| --- | --- |
| ![DeskBox General settings in English](docs/images/screenshots/en-us/常规.png) | ![DeskBox Appearance settings in English](docs/images/screenshots/en-us/外观.png) |

| Capsule mode | File widgets |
| --- | --- |
| ![DeskBox Capsule mode settings in English](docs/images/screenshots/en-us/胶囊模式.png) | ![DeskBox File widget settings in English](docs/images/screenshots/en-us/文件格子.png) |

| Feature widgets | Shortcuts & interaction |
| --- | --- |
| ![DeskBox Feature widget settings in English](docs/images/screenshots/en-us/功能格子.png) | ![DeskBox Shortcuts and interaction settings in English](docs/images/screenshots/en-us/快捷与交互.png) |

## Local-first data and privacy

DeskBox does not require an account or cloud synchronization. Widget configuration, todos, quick notes, search history, layouts, and managed files are stored locally.

Some actions intentionally use the network:

- Weather requests use MSN Weather or Open-Meteo.
- Update checks contact the DeskBox update endpoint or GitHub Releases.
- Setup downloads .NET or Windows App Runtime only when the selected architecture is missing.
- A remote URL dragged from a browser is downloaded only when you import it.

Capsule privacy mode hides selected text in the collapsed presentation; it is a presentation control, not file encryption.

## System requirements

- Windows 10 version 21H2 (build 19044) or later; Windows 11 version 22H2 or later for the full visual treatment.
- x64 or ARM64 processor matching the installer.
- .NET 10 Runtime and Windows App Runtime 2.2; setup can install either dependency when missing.

On Windows 10, unsupported materials, rounded corners, and some animations automatically fall back to compatible visuals; file sync, drag-and-drop, and core widget behavior are validated against the compatibility floor.

## Installation, updates, and removal

DeskBox uses an Inno Setup installer and installs for the current user by default. Overwrite installation preserves app settings, widget configuration, and managed storage. Older administrator-level installations under Program Files are migrated to avoid elevated-process drag-and-drop restrictions.

Startup launch is tray-first and silent. If DeskBox is already running, a second startup instance exits instead of opening another settings window.

Uninstall offers explicit choices to keep application data or permanently remove it. Permanent removal clears `%LocalAppData%\DeskBox`, `%LocalAppData%\DeskBox-Recovery`, temporary files, and DeskBox-owned registration data; user files in the managed storage path are always preserved. Silent uninstall keeps application data unless an administrator explicitly supplies `/PURGEUSERDATA`.

## FAQ

### Is DeskBox a Windows desktop replacement?

No. Explorer remains the desktop shell, and files remain normal files and folders. DeskBox adds independently managed widgets above the existing desktop.

### Where does DeskBox store data?

- App settings and widget data: `%LocalAppData%\DeskBox\data`
- New-user managed storage: a fixed non-system drive with enough free space when available, such as `D:\DeskBox\username`; otherwise `%UserProfile%\DeskBox`

Both locations can be backed up from DeskBox settings.

### Which installer should I choose?

Choose x64 for almost all Intel and AMD Windows PCs. Choose ARM64 for native Windows on ARM devices such as Snapdragon PCs. Check **Settings → System → About → System type** if unsure.

### Why can the installer need the internet?

Release installers do not contain the .NET 10 or Windows App Runtime 2.2 payload. Setup first checks the PC and downloads only a missing architecture-specific dependency.

### Does disabling a feature widget remove its data?

No. Disabling a feature closes its UI and releases runtime resources, while its saved configuration remains available for the next time you enable it.

## Build from source

Development requires the .NET 10 SDK and a Windows 11 environment. Visual Studio with the Windows App SDK workload is recommended.

Restore, test, and build the x64 Debug version:

```powershell
dotnet restore .\DeskBox.sln -p:Platform=x64
dotnet test .\DeskBox.sln --configuration Debug --no-restore -p:Platform=x64 -v:minimal
dotnet build .\src\DeskBox\DeskBox.csproj --configuration Debug --no-restore -p:Platform=x64 -v:minimal
```

Create framework-dependent Release outputs:

```powershell
dotnet publish .\src\DeskBox\DeskBox.csproj --configuration Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:WindowsAppSDKSelfContained=false -o .\artifacts\publish\DeskBox\x64 -v:minimal
dotnet publish .\src\DeskBox\DeskBox.csproj --configuration Release -p:Platform=ARM64 -p:RuntimeIdentifier=win-arm64 -p:SelfContained=false -p:WindowsAppSDKSelfContained=false -o .\artifacts\publish\DeskBox\arm64 -v:minimal
```

With Inno Setup 6 or newer installed, compile both installers:

```powershell
ISCC.exe .\installer\DeskBox.iss
ISCC.exe .\installer\DeskBox.arm64.iss
```

Expected outputs:

```text
Output\DeskBox_Setup_1.4.2_x64.exe
Output\DeskBox_Setup_1.4.2_arm64.exe
```

## Project layout

```text
src\DeskBox                 WinUI 3 application
src\DeskBox.Updater         direct-release updater helper
tests\DeskBox.Tests         service and policy tests
installer                   x64/ARM64 Inno Setup scripts
docs\user-guide             product documentation
docs\images                 README and release imagery
docs\releases               release copy and test checklists
```

## Feedback and localization

DeskBox is currently developed and maintained by a solo developer. External pull requests are not being accepted at this stage so the project can keep a consistent architecture and clear copyright boundaries, but bug reports, feature requests, translations, and UI/UX feedback are welcome through [GitHub Issues](https://github.com/Tianyu199509/DeskBox/issues).

Special thanks to [@magisph](https://github.com/magisph) for the Brazilian Portuguese localization.

You can also visit [deskbox.fun](https://deskbox.fun) or use the contact information in the app's About page.

## Author and license

- Developer: Tianyu Zhu
- Repository: <https://github.com/Tianyu199509/DeskBox>
- License: [GPL-3.0-only](LICENSE)

Earlier DeskBox versions already published under the MIT License remain available under that license. The change is not retroactive; see [LICENSE_CHANGE.md](LICENSE_CHANGE.md).
