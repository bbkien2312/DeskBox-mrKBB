# DeskBox development workflow

## Handoff bắt buộc — fork `bbkien2312/DeskBox-mrKBB`

Phần này tóm tắt các fix chính của fork ngày 19/08/2026 và phần hoàn thiện Fork 4 ngày 20/08/2026. Commit ngày 18/08 chỉ là baseline source ban đầu. Khi sửa lỗi mới, giữ nguyên các hành vi dưới đây trừ khi người dùng duyệt thay đổi khác.

### Nguồn, phiên bản và phát hành

- Cây `main` của fork là nguồn sự thật. `deskbox_original` chỉ là baseline để đối chiếu; không dùng lại source cũ để ghi đè cây này.
- Phiên bản app hiện tại: `1.4.2.1-fork.4` (`DeskBoxForkBuildNumber=4`). Source đã push đến commit `2aa50d4`.
- Installer Fork 4 đã build tại `Output\DeskBox_Setup_1.4.2.1_x64.exe` và đã publish ở GitHub Release tag `v1.4.2.1-fork.4`. `release/stable.json` online đã trỏ Fork 4, asset, dung lượng và SHA-256 đã được đối chiếu. Fork 3 phải so sánh `forkBuildNumber=3` với manifest `4` để hiện update.
- Luồng updater nằm ở `scripts\publish-fork-update.ps1`, `Services\AppUpdateService.cs`, `Services\AppBuildMetadata.cs` và `Models\AppUpdateManifest.cs`. Giữ protocol version riêng của fork, `forkBuildNumber` tách khỏi version upstream và tên tag `v1.4.2.1-fork.N`.
- Script phát hành phải tạo SHA-256/manifest UTF-8 không BOM. Không giả định PowerShell có `Get-FileHash`; script đã có fallback BCL.

### Desktop organizer — hành vi phải giữ

- `vi-VN` là locale đầy đủ; khi đổi ngôn ngữ phải refresh cả cửa sổ tổ chức. File chính: `Strings\vi-VN.json`, `Services\LocalizationService.cs`, `Views\DesktopOrganizationWindow.xaml.cs`.
- Rule tổ chức nhận file, Folder, shortcut/app, system shortcut, media, archive và document. Folder có hai phần không được gộp nhầm: rule `Folders` chọn box đích, còn watcher tự nhận folder mới trực tiếp trên Desktop.
- Scanner có thể quét Desktop cá nhân, Public Desktop và nội dung nằm trong box khi người dùng bật tùy chọn. File/folder thay đổi sau scan phải bị bỏ qua riêng với log, không làm hủy kế hoạch của các mục khác.
- Watcher phải debounce và chờ file/folder ổn định trước khi move. Các module trọng tâm: `DesktopAutoOrganizationWatcher`, `DesktopOrganizationScanner`, `DesktopOrganizationClassifier`, `DesktopOrganizationPlanner`, `DesktopOrganizationTransaction` và `DesktopOrganizationLogService`.
- Thông báo auto-organize được gộp theo khoảng ngắn, không báo từng file. Giữ `DesktopOrganizationNotificationCoalescer` và log mức `INFO/OK/WARNING/ERROR` có timestamp.
- Với organize cùng volume, `DesktopOrganizationTransaction` gọi transfer plan với `useShellProgress:false` để dùng `File.Move` metadata nhanh. Khác volume vẫn phải rơi về copy/delete có rollback trong `FileService`; không tự giới hạn hoặc bỏ qua chỉ vì khác ổ.
- Box tạo mới bởi Desktop organizer mặc định `WidgetSortMode.Type`; box cũ phải giữ sort mode người dùng đã chọn.

### Tương tác file và vòng đời app

- Trong box: type-ahead theo ký tự đầu tên giống Explorer; double-click theo khoảng Windows mở mục/đổi tên. Các sửa liên quan chủ yếu ở `FileSurfaceContent`, `WidgetViewModel` và `Win32Helper`.
- Shortcut `.lnk` kéo từ box Apps and shortcuts sang Explorer phải dùng payload/OLE native để Explorer quyết định Move/Copy, không tạo bản copy rơi về Desktop. Giữ `FileItemDragPackage`, `NativeFileDragSource` và `VirtualShortcutDragProvider` fallback.
- Khi người dùng chọn thoát hoàn toàn, watcher dừng trước rồi các box managed được trả về Desktop, có undo và journal `managed-storage-restore-journal.json`. Không restore khi shutdown vì update/restart. Lần khởi động sau, tùy chọn resync phải đưa các mục đã trả về lại box theo cấu hình.
- Một EXE DeskBox thứ hai thường tự thoát vì single-instance mutex và signal app đang chạy; không kết luận là crash khi test publish nếu bản cài đặt còn mở.

### Hiệu năng và chụp màn hình

- Thumbnail ảnh dùng cache disk demand-driven tại `%LOCALAPPDATA%\DeskBox\cache\thumbnails`; video tiếp tục dùng Windows thumbnail provider. Không thêm quét cache lúc startup ngoài tạo thư mục/cleanup nhỏ.
- Cache icon, bitmap và thumbnail RAM đã được giảm giới hạn. Số liệu disk thumbnail cache được ghi trong `PerformanceLogger`; đừng tăng cache lại mà không đo RAM thực tế.
- `Ctrl+Alt+S` mở `ScreenshotCaptureWindow`: chụp ảnh màn hình trước khi overlay hiện, rê để chọn cửa sổ, click để khóa, sau đó Sao chép/Lưu. Desktop/taskbar nghĩa là toàn màn hình. Dùng DWM extended frame bounds nếu có, fallback `GetWindowRect`.

### Kiểm thử và môi trường

- Luôn test `x64`; không dùng `AnyCPU`. Lần kiểm thử toàn bộ gần nhất: 1788/1788 passed.
- Khi DeskBox cài đặt đang chạy, đặt `DESKBOX_DEV_DATA_ROOT` tới thư mục test tạm trước khi chạy test Debug để recovery journal không khóa `%LOCALAPPDATA%\DeskBox\data`.
- Nếu terminal gán `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY` hoặc biến `GIT_*_PROXY` tới `127.0.0.1:9`, Git/Git Credential Manager sẽ không push được. Đây là lỗi môi trường terminal, không đổi remote fork và không lưu token vào Git config.

- After changing application code, first stop any running `DeskBox.exe` whose executable path is under this repository, then build the affected project, and start a fresh instance from the current Debug build unless the user explicitly asks not to restart it. Stopping before the build avoids locking the output executable.
- The canonical local development executable is `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`.
- After starting DeskBox, verify that exactly the intended repository build is running and report the executable path.
- Do not launch DeskBox from `Output`, `artifacts`, `.artifacts`, or `src/DeskBox/AppPackages` unless the user explicitly requests testing a packaged or published build.
- Preserve unrelated user changes and release artifacts. Ask before deleting material output directories or installer packages unless the user explicitly authorizes their removal.
- DeskBox is a packaged Windows application. Do not first run its tests with the default `AnyCPU` platform: MSIX packaging rejects a processor-neutral app-host executable. Run the test suite directly with `dotnet test .\tests\DeskBox.Tests\DeskBox.Tests.csproj --no-restore --verbosity:minimal -p:Platform=x64` (add `-p:RuntimeIdentifier=win-x64` when using architecture-specific restored assets).
- For Release publishing, always specify a matching platform and runtime identifier from the start: `-p:Platform=x64 -p:RuntimeIdentifier=win-x64` for x64, or `-p:Platform=ARM64 -p:RuntimeIdentifier=win-arm64` for ARM64. Keep `SelfContained=false` and `WindowsAppSDKSelfContained=false` for the runtime-download installer workflow unless the user requests a self-contained build.
- The explicit architecture rules above apply to tests and Release publishing. Continue using the canonical non-platform Debug output for the normal local restart workflow.
