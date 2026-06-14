# Multi Brave Manager v3

Phiên bản quản lý nhiều Brave + KiotProxy với giao diện dễ dùng hơn. **Không thay thế** `open-multi-brave` (v1).

## Chạy app

Từ thư mục gốc repo:

```
OpenMultiBraveLauncherV3.cmd
```

Hoặc build rồi chạy exe:

```
open-multi-brave-v3\bin\Release\net8.0-windows\OpenMultiBraveLauncherV3.exe
```

## Cấu hình & profile

| Thành phần | Vị trí |
|------------|--------|
| Cài đặt (key, instance, Brave path) | `launcher-settings.json` cạnh exe |
| Profile từng instance | `profiles\<instanceId>\Default\` |
| Extension Brave | `extension\` cạnh exe |

## Khác v1

- Danh sách instance bên trái + chi tiết bên phải
- Tự lưu khi đóng app (không cần nút Save key)
- Profile **ổn định** theo instance (giữ cookie/đăng nhập)
- CDP port **9330+** (v1 dùng 9230+) — có thể chạy song song
- Lần đầu có thể đọc key từ `open-multi-brave/.../proxy-keys.json` (chỉ đọc)
