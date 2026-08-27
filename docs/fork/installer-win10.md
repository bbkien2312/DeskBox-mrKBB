# Bộ cài Windows 10/11 của DeskBox fork

## Phạm vi hỗ trợ

- Windows 10 21H2 (build 19044) hoặc mới hơn, x64.
- Windows 11 x64.
- Không hỗ trợ Windows 32-bit hoặc Windows 7 bằng bộ cài WinUI hiện hành.

## Hai gói phát hành

1. `DeskBox_Setup_1.4.2.1_x64.exe`: gói online nhỏ, là asset duy nhất mà updater fork tải. Setup tự kiểm tra và chỉ tải dependency còn thiếu.
2. `DeskBox_Prerequisites_1.4.2.1_x64.exe`: gói rescue offline. Nó cài Visual C++ 2015-2022 x64, .NET 10 Runtime x64 và Windows App Runtime 2.2 x64; sau đó người dùng chạy lại setup chính.

Binary runtime vendor nằm tại `artifacts\prerequisites\x64` khi build và luôn bị Git bỏ qua. Chúng chỉ được upload làm GitHub Release asset, không commit vào source. Dung lượng gói prerequisites xấp xỉ 158 MiB; setup chính giữ khoảng 24–25 MiB.

## Chẩn đoán lỗi cài đặt

- Setup bật `SetupLogging=yes`; Inno Setup ghi log mặc định trong thư mục Temp.
- Khi cần gửi log dễ tìm, chạy:

  ```bat
  DeskBox_Setup_1.4.2.1_x64.exe /LOG="%USERPROFILE%\Desktop\DeskBox-Setup.log"
  ```

- Log phải cho biết: build Windows, kiến trúc, trạng thái .NET, Windows App Runtime, VC++ và mã thoát từng installer.
- Nếu Windows App Runtime vừa cài system-wide nhưng process Setup không nhìn thấy registration của user ngay, setup yêu cầu restart thay vì báo lỗi vĩnh viễn. Sau restart chạy lại DeskBox setup.

## Build và release

```powershell
./scripts/publish-fork-update.ps1 -Platform x64 -ForkBuildNumber 7 -BuildOfflinePrerequisites
```

Lệnh trên build installer online, tải prerequisite vào `artifacts`, build rescue offline và tạo SHA-256 cho cả các asset. Khi publish GitHub Release, truyền `-PublishGitHubRelease`; manifest `release/stable.json` chỉ trỏ đến setup online để tránh updater tự tải asset lớn.
