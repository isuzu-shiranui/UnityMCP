using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor.Settings
{
    /// <summary>Which language the Preferences page draws itself in.</summary>
    public enum McpUiLanguage
    {
        /// <summary>Follow the Editor's own language.</summary>
        Auto = 0,
        English = 1,
        Japanese = 2,
        Vietnamese = 3,
    }

    /// <summary>
    /// The Preferences page's user-visible strings.
    /// </summary>
    /// <remarks>
    /// Only this page is translated. Tool and argument descriptions, the text a tool returns and
    /// the CLI's output stay in English: a model reads them to decide what to call, and the
    /// documentation checks in CI compare them against the code.
    /// <para>
    /// Entries are keyed by their English text, so a string with no entry draws in English
    /// instead of drawing a key name.
    /// </para>
    /// </remarks>
    public static class McpEditorText
    {
        /// <summary>
        /// The Editor writes its language here as a <see cref="SystemLanguage"/> name. Reading the
        /// preference rather than LocalizationDatabase keeps this on public API; that type is
        /// internal to the Editor assembly.
        /// </summary>
        private const string EditorLocaleKey = "Editor.kEditorLocale";

        private static readonly Dictionary<string, string> Japanese = new Dictionary<string, string>
        {
            // Setup
            ["Setup"] = "セットアップ",
            ["Listening on port {0}"] = "ポート {0} で待ち受けています",
            ["Server stopped"] = "サーバーは停止しています",
            ["Server not initialized"] = "サーバーが初期化されていません",
            ["Start"] = "開始",
            ["Stop"] = "停止",
            ["Initialize"] = "初期化",
            ["isuzu-unity-cli found"] = "isuzu-unity-cli が見つかりました",
            ["isuzu-unity-cli not found on PATH"] = "isuzu-unity-cli が PATH にありません",
            ["Install"] = "インストール",
            ["Register"] = "登録",
            ["Registered in {0} client configuration(s)"] = "{0} 個のクライアント設定に登録済み",
            ["No MCP client points at this Editor"] = "この Editor を指している MCP クライアントがありません",
            ["setup --mcp finished without saying anything."] = "setup --mcp は何も出力せずに終了しました。",
            ["Could not run {0}: {1}"] = "{0} を実行できませんでした: {1}",
            ["Refresh"] = "再確認",
            ["Copy command"] = "コマンドをコピー",
            ["Register an MCP client with the configuration below, or run isuzu-unity-cli setup --mcp."] =
                "下の設定で MCP クライアントを登録してください。isuzu-unity-cli setup --mcp でも登録できます。",
            ["Command-line agents need the CLI. MCP clients reach the endpoint below without it."] =
                "コマンドラインのエージェントには CLI が要ります。MCP クライアントは下のエンドポイントに直接つながります。",
            ["Port {0} was busy, so this Editor is on {1}. Clients configured for the usual URL cannot reach it. Close whatever holds the port, or pin an HTTP Port in Settings and register the clients again."] =
                "ポート {0} が使用中だったので、この Editor は {1} で待ち受けています。通常の URL で登録したクライアントからは届きません。ポートを使っているものを閉じるか、設定で HTTP Port を固定して登録し直してください。",
            ["{0} is out and this package is {1}. Run 'isuzu-unity-cli update' to install it and bring this project's package up with it."] =
                "{0} が公開されています。このパッケージは {1} です。'isuzu-unity-cli update' を実行すると、CLI とこのプロジェクトのパッケージを揃えて更新します。",

            // Connection
            ["Connection"] = "接続",
            ["MCP URL"] = "MCP URL",
            ["Bearer token"] = "Bearer トークン",
            ["Configuration for"] = "クライアント設定",
            ["Copy"] = "コピー",
            ["Regenerate"] = "再発行",
            ["Show"] = "表示",
            ["Hide"] = "隠す",
            ["Start the server to see the connection details."] = "接続情報を見るにはサーバーを開始してください。",
            ["The descriptor and token files under {0} are credentials. Anything that can read them can run code in this Editor."] =
                "{0} の descriptor ファイルとトークンファイルは資格情報です。これらを読めるものは、この Editor 内でコードを実行できます。",
            ["Regenerate token"] = "トークンを再発行",
            ["Every MCP client registered with the current token stops working until it is registered again with isuzu-unity-cli doctor --fix. Continue?"] =
                "今のトークンで登録済みの MCP クライアントは、isuzu-unity-cli doctor --fix で登録し直すまで動かなくなります。続けますか？",
            ["Cancel"] = "キャンセル",

            // Settings
            ["Settings"] = "設定",
            ["HTTP Port"] = "HTTP ポート",
            ["0 derives a stable port from the project path, which is what MCP client configuration relies on. Set a positive port only to resolve a collision; clients then have to be registered again."] =
                "0 にすると、プロジェクトのパスから決まる固定のポートを使います。MCP クライアントの設定はこれを前提にしています。正の値はポートの衝突を解決するときだけ指定してください。指定するとクライアントの登録をやり直す必要があります。",
            ["A port must be 0, or between 1024 and 65535. The server cannot bind {0}."] =
                "ポートは 0 か、1024 から 65535 の間である必要があります。{0} にはバインドできません。",
            ["Auto-start on launch"] = "起動時に自動で開始",
            ["Start the server when the Editor opens this project."] = "Editor がこのプロジェクトを開いたときにサーバーを開始します。",
            ["Sync wait (ms)"] = "同期待ち時間 (ms)",
            ["How long a request waits for its main-thread work before the server answers with a job id instead. The server does not go below 250 ms."] =
                "リクエストがメインスレッドの処理を待つ時間です。これを超えるとサーバーは job id を返します。250 ms 未満にはなりません。",
            ["The server uses 250 ms, which is its floor."] = "サーバーは下限の 250 ms を使います。",
            ["Detailed logs"] = "詳細ログ",
            ["Write every request and each start and stop step to the Console. Those lines come back to the agent through console_read_logs. Warnings and errors are written either way."] =
                "各リクエストと起動・停止の各段階を Console に書きます。これらは console_read_logs を通じてエージェントにも返ります。警告とエラーは、この設定に関わらず書かれます。",
            ["Keep Editor awake"] = "Editor を起こし続ける",
            ["Without focus the Editor runs its main loop about every 100 ms, so calls that need it wait. The server wakes the loop while requests are queued. Turning this on keeps it awake for the whole session instead, at the cost of idle CPU."] =
                "フォーカスが無いと Editor はメインループを約 100 ms 間隔でしか回さないので、それを使う呼び出しは待たされます。サーバーはリクエストが待っている間だけ Editor を起こします。これを有効にすると、セッション中ずっと起こし続けます。待機中の CPU を使います。",
            ["Language"] = "言語",
            ["The language of this page. Tool descriptions and CLI output stay in English."] =
                "このページの言語です。ツールの説明文と CLI の出力は英語のままです。",
            ["Follow the Editor"] = "Editor に合わせる",
            ["These settings live in Unity's preferences folder and are shared by every project on this machine."] =
                "この設定は Unity の Preferences フォルダーに保存され、この PC の全プロジェクトで共有されます。",

            // Help
            ["Help"] = "ヘルプ",
            ["Getting started"] = "はじめかた",
            ["Documentation"] = "ドキュメント",
            ["Troubleshooting"] = "トラブルシューティング",
        };

        private static readonly Dictionary<string, string> Vietnamese = new Dictionary<string, string>
        {
            // Setup
            ["Setup"] = "Thiết lập",
            ["Listening on port {0}"] = "Đang lắng nghe trên cổng {0}",
            ["Server stopped"] = "Máy chủ đã dừng",
            ["Server not initialized"] = "Máy chủ chưa được khởi tạo",
            ["Start"] = "Khởi chạy",
            ["Stop"] = "Dừng",
            ["Initialize"] = "Khởi tạo",
            ["isuzu-unity-cli found"] = "Đã tìm thấy isuzu-unity-cli",
            ["isuzu-unity-cli not found on PATH"] = "Không tìm thấy isuzu-unity-cli trong PATH",
            ["Install"] = "Cài đặt",
            ["Register"] = "Đăng ký",
            ["Registered in {0} client configuration(s)"] = "Đã đăng ký trong {0} cấu hình ứng dụng khách",
            ["No MCP client points at this Editor"] = "Chưa có ứng dụng khách MCP nào trỏ tới Editor này",
            ["setup --mcp finished without saying anything."] = "setup --mcp đã kết thúc mà không xuất thông báo nào.",
            ["Could not run {0}: {1}"] = "Không thể chạy {0}: {1}",
            ["Refresh"] = "Kiểm tra lại",
            ["Copy command"] = "Sao chép lệnh",
            ["Register an MCP client with the configuration below, or run isuzu-unity-cli setup --mcp."] =
                "Đăng ký ứng dụng khách MCP bằng cấu hình bên dưới, hoặc chạy isuzu-unity-cli setup --mcp.",
            ["Command-line agents need the CLI. MCP clients reach the endpoint below without it."] =
                "Tác nhân AI dùng dòng lệnh cần có CLI. Ứng dụng khách MCP kết nối trực tiếp đến điểm cuối bên dưới mà không cần CLI.",
            ["Port {0} was busy, so this Editor is on {1}. Clients configured for the usual URL cannot reach it. Close whatever holds the port, or pin an HTTP Port in Settings and register the clients again."] =
                "Cổng {0} đang được sử dụng, nên Editor này dùng cổng {1}. Ứng dụng khách được cấu hình với URL thông thường sẽ không kết nối được. Hãy đóng ứng dụng đang chiếm cổng, hoặc đặt Cổng HTTP cố định trong Cài đặt rồi đăng ký lại các ứng dụng khách.",

            // Connection
            ["Connection"] = "Kết nối",
            ["MCP URL"] = "URL MCP",
            ["Bearer token"] = "Token Bearer",
            ["Configuration for"] = "Cấu hình cho",
            ["Copy"] = "Sao chép",
            ["Regenerate"] = "Tạo lại",
            ["Show"] = "Hiện",
            ["Hide"] = "Ẩn",
            ["Start the server to see the connection details."] = "Khởi chạy máy chủ để xem thông tin kết nối.",
            ["The descriptor and token files under {0} are credentials. Anything that can read them can run code in this Editor."] =
                "Các tệp mô tả kết nối và tệp token trong {0} chứa thông tin xác thực. Bất kỳ ai hoặc chương trình nào đọc được chúng đều có thể chạy mã trong Editor này.",
            ["Regenerate token"] = "Tạo lại token",
            ["Every MCP client registered with the current token stops working until it is registered again with isuzu-unity-cli doctor --fix. Continue?"] =
                "Mọi ứng dụng khách MCP đã đăng ký bằng token hiện tại sẽ ngừng hoạt động cho đến khi được đăng ký lại bằng isuzu-unity-cli doctor --fix. Tiếp tục?",
            ["Cancel"] = "Hủy",

            // Settings
            ["Settings"] = "Cài đặt",
            ["HTTP Port"] = "Cổng HTTP",
            ["0 derives a stable port from the project path, which is what MCP client configuration relies on. Set a positive port only to resolve a collision; clients then have to be registered again."] =
                "Giá trị 0 xác định cổng cố định từ đường dẫn dự án; cấu hình ứng dụng khách MCP dựa vào cổng này. Chỉ đặt số cổng dương để giải quyết xung đột; khi đó cần đăng ký lại các ứng dụng khách.",
            ["A port must be 0, or between 1024 and 65535. The server cannot bind {0}."] =
                "Cổng phải là 0 hoặc nằm trong khoảng 1024 đến 65535. Máy chủ không thể lắng nghe trên cổng {0}.",
            ["Auto-start on launch"] = "Tự khởi chạy",
            ["Start the server when the Editor opens this project."] = "Khởi chạy máy chủ khi Editor mở dự án này.",
            ["Sync wait (ms)"] = "Chờ đồng bộ (ms)",
            ["How long a request waits for its main-thread work before the server answers with a job id instead. The server does not go below 250 ms."] =
                "Thời gian yêu cầu chờ xử lý trên luồng chính trước khi máy chủ trả về mã tác vụ (job id). Thời gian chờ tối thiểu là 250 ms.",
            ["The server uses 250 ms, which is its floor."] = "Máy chủ dùng thời gian chờ tối thiểu là 250 ms.",
            ["Detailed logs"] = "Nhật ký chi tiết",
            ["Write every request and each start and stop step to the Console. Those lines come back to the agent through console_read_logs. Warnings and errors are written either way."] =
                "Ghi từng yêu cầu và từng bước khởi chạy, dừng máy chủ vào Console. Tác nhân AI đọc được các dòng này qua console_read_logs. Cảnh báo và lỗi luôn được ghi dù bật hay tắt tùy chọn này.",
            ["Keep Editor awake"] = "Giữ Editor hoạt động",
            ["Without focus the Editor runs its main loop about every 100 ms, so calls that need it wait. The server wakes the loop while requests are queued. Turning this on keeps it awake for the whole session instead, at the cost of idle CPU."] =
                "Khi không có tiêu điểm, Editor chạy vòng lặp chính khoảng mỗi 100 ms, nên các lệnh cần vòng lặp này phải chờ. Máy chủ đánh thức vòng lặp khi có yêu cầu trong hàng đợi. Bật tùy chọn này để giữ vòng lặp hoạt động suốt phiên, nhưng sẽ tốn CPU cả khi không có yêu cầu.",
            ["Language"] = "Ngôn ngữ",
            ["The language of this page. Tool descriptions and CLI output stay in English."] =
                "Ngôn ngữ của trang này. Mô tả công cụ và đầu ra CLI vẫn dùng tiếng Anh.",
            ["Follow the Editor"] = "Theo ngôn ngữ Editor",
            ["These settings live in Unity's preferences folder and are shared by every project on this machine."] =
                "Các cài đặt này được lưu trong thư mục tùy chọn của Unity và dùng chung cho mọi dự án trên máy này.",

            // Help
            ["Help"] = "Trợ giúp",
            ["Getting started"] = "Bắt đầu sử dụng",
            ["Documentation"] = "Tài liệu",
            ["Troubleshooting"] = "Khắc phục sự cố",
        };

        /// <summary>
        /// The translated entries. Public so the test can check that each translation carries the
        /// same {0}-style placeholders as its key; a translation with an index the key does not
        /// have throws FormatException when the page draws it.
        /// </summary>
        public static IReadOnlyDictionary<string, string> JapaneseEntries => Japanese;

        /// <summary>The Vietnamese entries, exposed for the same translation checks.</summary>
        public static IReadOnlyDictionary<string, string> VietnameseEntries => Vietnamese;

        /// <summary>Translates <paramref name="english"/> for the language the page is drawn in.</summary>
        public static string Tr(string english)
        {
            switch (Resolve())
            {
                case SystemLanguage.Japanese:
                    return Japanese.TryGetValue(english, out var japanese) ? japanese : english;
                case SystemLanguage.Vietnamese:
                    return Vietnamese.TryGetValue(english, out var vietnamese) ? vietnamese : english;
                default:
                    return english;
            }
        }

        /// <summary>A label and its tooltip, both translated.</summary>
        public static GUIContent Content(string label, string tooltip)
        {
            return new GUIContent(Tr(label), Tr(tooltip));
        }

        /// <summary>The language the page draws itself in.</summary>
        public static SystemLanguage Resolve()
        {
            switch ((McpUiLanguage)McpSettings.instance.uiLanguage)
            {
                case McpUiLanguage.English:
                    return SystemLanguage.English;
                case McpUiLanguage.Japanese:
                    return SystemLanguage.Japanese;
                case McpUiLanguage.Vietnamese:
                    return SystemLanguage.Vietnamese;
                default:
                    return EditorLanguage();
            }
        }

        /// <summary>
        /// The preference is unset until the language is changed once, so the OS language stands
        /// in for it. An unrecognised value falls back the same way rather than throwing.
        /// </summary>
        private static SystemLanguage EditorLanguage()
        {
            var saved = EditorPrefs.GetString(EditorLocaleKey, string.Empty);

            if (!string.IsNullOrEmpty(saved) && System.Enum.TryParse<SystemLanguage>(saved, out var language))
            {
                return language;
            }

            return Application.systemLanguage;
        }
    }
}
