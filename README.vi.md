# Framework tích hợp Unity MCP

<!-- mcp-name: dev.shiranui-isuzu/unity-mcp -->

[![Giấy phép: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
![Unity](https://img.shields.io/badge/Unity-2022.3%E2%80%93Unity6-black.svg)
![.NET](https://img.shields.io/badge/.NET-10-purple.svg)
![Sao trên GitHub](https://img.shields.io/github/stars/isuzu-shiranui/UnityMCP?style=social)

[日本語](./README.md) | [English](./README.en.md)

Framework này cho phép tác nhân AI thao tác với Unity Editor. Lệnh chạy thủ công và lệnh gọi từ script đều đi qua cùng một đường xử lý.

- Ứng dụng khách MCP kết nối trực tiếp đến điểm cuối Streamable HTTP do chính Editor cung cấp tại `http://127.0.0.1:<port>/mcp`. Không có tiến trình máy chủ MCP riêng. Dự án đã kiểm tra kết nối với Claude Code, Cursor, Codex, Gemini CLI, VS Code và Claude Desktop.
- Công cụ dòng lệnh `isuzu-unity-cli` gọi cùng các công cụ đó. Các tệp thực thi được phát hành là mã máy, không cần Node hay môi trường chạy .NET.
- Mỗi công cụ là một phương thức C# tĩnh có thuộc tính `[McpTool]`.

Nếu đây là lần đầu sử dụng, hãy bắt đầu với [Bắt đầu sử dụng Unity MCP](https://unity-mcp.shiranui-isuzu.dev/vi/), hướng dẫn cài đặt có hình minh họa.

## Yêu cầu

- Unity Editor 2022.3 trở lên. Bộ kiểm thử EditMode của dự án được chạy trên Unity 6000.0.35f1
- Git 2.14.0 trở lên, có trong `PATH`. Package Manager của Unity dùng Git để tải gói từ URL Git ([hướng dẫn Unity, tiếng Anh](https://docs.unity3d.com/Manual/upm-git.html)). Cài qua kho VPM bên dưới thì không cần Git
- `com.unity.nuget.newtonsoft-json` 3.2.1. Gói này được tự động cài đặt theo quan hệ phụ thuộc

## Cài đặt

Trong Package Manager của Unity, chọn **Add package from git URL** rồi nhập:

```
https://github.com/isuzu-shiranui/UnityMCP.git?path=jp.shiranui-isuzu.unity-mcp
```

Với VRChat Creator Companion (VCC) hoặc ALCOM, bạn có thể thêm kho VPM `https://unity-mcp.shiranui-isuzu.dev/vpm.json`. Cả hai tải gói dưới dạng zip nên không cần Git. Liên kết thêm kho bằng một lần nhấp và vị trí nút thêm kho trong từng ứng dụng nằm ở phần [VCC và ALCOM](https://unity-mcp.shiranui-isuzu.dev/vi/#vpm-title) của hướng dẫn bắt đầu.

Để chuyển giao diện Unity MCP sang tiếng Việt, mở Preferences > Unity MCP > Settings và chọn **Tiếng Việt** ở mục Language. Tùy chọn này áp dụng cho trang Preferences của Unity MCP; mô tả công cụ và đầu ra CLI vẫn dùng tiếng Anh.

Cài CLI:

```bash
# Windows
irm https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.ps1 | iex

# macOS / Linux
curl -fsSL https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.sh | sh
```

Bạn cũng có thể tải tệp thực thi từ GitHub Releases và đối chiếu mã băm với `SHA256SUMS`. Nếu đã cài .NET 10 SDK, bạn có thể dùng `dotnet tool install -g IsuzuUnityCli`.

Sau đó cài skill cho tác nhân AI và đăng ký ứng dụng khách MCP:

```bash
isuzu-unity-cli setup                              # skill cho Claude Code và Codex
isuzu-unity-cli setup --mcp --agent claude-code    # đăng ký ứng dụng khách MCP
```

`--agent` chấp nhận `claude-code`, `claude-desktop`, `codex`, `cursor`, `gemini` hoặc `vscode`. Claude Code lưu cấu hình máy chủ theo đường dẫn dự án Unity, vì vậy hãy khởi chạy Claude Code trong thư mục dự án Unity. Bạn cũng có thể đăng ký ứng dụng khách từ Preferences > Unity MCP trong Editor.

Cấu hình từng ứng dụng khách, gói tiện ích mở rộng cho Claude Desktop và cầu nối stdio được mô tả trong [Kết nối ứng dụng khách MCP (tiếng Anh)](docs/en/mcp-clients.md).

## Các lệnh đầu tiên

Máy chủ khởi chạy khi Editor mở dự án và tạo tệp mô tả kết nối (descriptor). CLI đọc tệp này nên bạn không cần nhập cổng hay token.

```bash
isuzu-unity-cli projects                  # các Editor đang chạy
isuzu-unity-cli tools                     # công cụ do Editor này cung cấp
isuzu-unity-cli call play_mode_status     # gọi một công cụ
isuzu-unity-cli verify                    # biên dịch lại, thu thập lỗi, đọc lỗi Console
```

`verify` gộp việc biên dịch lại và thu thập lỗi sau khi sửa script vào một lần gọi. Thêm `--test` để chạy cả kiểm thử.

Khi mở nhiều Editor, chọn dự án bằng `--project <name>`. Nếu chạy lệnh trong thư mục dự án, CLI sẽ tự chọn. Tất cả lệnh được mô tả trong [tài liệu CLI (tiếng Anh)](docs/en/cli.md).

## Công cụ

Các công cụ hỗ trợ chẩn đoán (Console, `Editor.log`, trạng thái biên dịch, kiểm thử, cấu trúc phân cấp scene, đọc asset); tạo và chỉnh sửa GameObject, component, asset, scene, prefab và Animator Controller; kết xuất; Timeline và Recorder; build; chạy đoạn mã C#; và mô phỏng thao tác trong Editor. Mỗi lần gọi công cụ tạo hoặc chỉnh sửa được gộp thành một bước Undo.

Công cụ Timeline chỉ xuất hiện khi có `com.unity.timeline`; công cụ Recorder cần cả `com.unity.recorder` và `com.unity.timeline`; `test_run` và `test_results` cần `com.unity.test-framework`.

Danh sách đầy đủ cùng các lưu ý trước khi chỉnh sửa nằm trong [tài liệu công cụ (tiếng Anh)](docs/en/tools.md). Thêm `?group=diagnostics,authoring` vào URL MCP để `tools/list` chỉ trả về các nhóm đó.

## Thêm công cụ

Viết một phương thức trong Editor.

```csharp
using System.Linq;
using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

internal static class MyTools
{
    [McpTool(
        "asset_find_by_type",
        "Find project assets of a given type. Prefer a narrow type and a small limit.",
        Idempotency = McpIdempotency.Safe)]
    public static string[] FindByType(
        [McpArg("type", "Unity type name, e.g. Material.")] string type,
        [McpArg("limit", "Maximum paths to return.")] int limit = 50)
    {
        return UnityEditor.AssetDatabase.FindAssets($"t:{type}")
            .Take(limit)
            .Select(UnityEditor.AssetDatabase.GUIDToAssetPath)
            .ToArray();
    }
}
```

Như vậy, công cụ đã có thể được gọi từ cả ứng dụng khách MCP lẫn CLI. JSON Schema được tạo từ chữ ký phương thức. Các thuộc tính mà `[McpTool]` chấp nhận được liệt kê trong [Kiến trúc (tiếng Anh)](docs/en/architecture.md).

Bạn cũng có thể thêm công cụ bằng tệp JSON mà không viết C#. Xem [Công cụ định nghĩa bằng JSON (tiếng Anh)](docs/en/defined-tools.md).

## Tài liệu

Các tài liệu kỹ thuật dưới đây hiện dùng tiếng Anh; CHANGELOG dùng tiếng Nhật.

- [Danh sách công cụ](docs/en/tools.md): toàn bộ công cụ và các lưu ý trước khi chỉnh sửa
- [Kết nối ứng dụng khách MCP](docs/en/mcp-clients.md): cấu hình từng ứng dụng khách, cầu nối Claude Desktop, thông tin giao thức
- [Tài liệu CLI](docs/en/cli.md): toàn bộ lệnh, chọn dự án, mã thoát, những tệp được lưu trên máy
- [Công cụ định nghĩa bằng JSON](docs/en/defined-tools.md): công cụ `probe`, `script` và `sequence` từ tệp JSON
- [Mô phỏng, ghi và phát lại thao tác trong Editor](docs/en/input-tools.md): `input_pointer`, `input_key`, `input_record`, `input_replay`
- [Kiến trúc](docs/en/architecture.md): sơ đồ, các lớp phía Editor, cài đặt, kiểm thử
- [Khắc phục sự cố](docs/en/troubleshooting.md)
- [Bảo mật](docs/en/security.md)
- [Chuyển từ v3](docs/en/migration-v3.md)
- [CHANGELOG](jp.shiranui-isuzu.unity-mcp/CHANGELOG.md)

## Bảo mật

Máy chủ chỉ lắng nghe trên `127.0.0.1`, và mọi yêu cầu trừ `OPTIONS` đều cần token Bearer. Hãy bảo vệ tệp mô tả kết nối và tệp token như thông tin xác thực: bất kỳ ai hoặc chương trình nào đọc được chúng đều có thể chạy mã trong Editor. Không có mã của gói trong bản build ứng dụng, kể cả Development Build. Chi tiết nằm trong [Bảo mật (tiếng Anh)](docs/en/security.md).

## Giấy phép

MIT
