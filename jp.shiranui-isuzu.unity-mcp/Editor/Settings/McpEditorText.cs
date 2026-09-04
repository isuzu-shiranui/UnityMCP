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
            ["Refresh"] = "再確認",
            ["Copy command"] = "コマンドをコピー",
            ["Register an MCP client with the configuration below, or run isuzu-unity-cli setup --mcp."] =
                "下の設定で MCP クライアントを登録してください。isuzu-unity-cli setup --mcp でも登録できます。",
            ["Command-line agents need the CLI. MCP clients reach the endpoint below without it."] =
                "コマンドラインのエージェントには CLI が要ります。MCP クライアントは下のエンドポイントに直接つながります。",
            ["Port {0} was busy, so this Editor is on {1}. Clients configured for the usual URL cannot reach it. Close whatever holds the port, or pin an HTTP Port in Settings and register the clients again."] =
                "ポート {0} が使用中だったので、この Editor は {1} で待ち受けています。通常の URL で登録したクライアントからは届きません。ポートを使っているものを閉じるか、設定で HTTP Port を固定して登録し直してください。",

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

        /// <summary>
        /// The translated entries. Public so the test can check that each translation carries the
        /// same {0}-style placeholders as its key; a translation with an index the key does not
        /// have throws FormatException when the page draws it.
        /// </summary>
        public static IReadOnlyDictionary<string, string> JapaneseEntries => Japanese;

        /// <summary>Translates <paramref name="english"/> for the language the page is drawn in.</summary>
        public static string Tr(string english)
        {
            if (Resolve() != SystemLanguage.Japanese)
            {
                return english;
            }

            return Japanese.TryGetValue(english, out var translated) ? translated : english;
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
