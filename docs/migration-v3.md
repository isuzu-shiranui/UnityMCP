# v3 からの移行

v3 の利用者が v4 へ移るときに置き換える名前とコマンドを説明します。[README に戻る](../README.md)

v4 は破壊的変更を含みます。

| v3 | v4 |
|---|---|
| `isuzu-unity-mcp <cmd>` | `isuzu-unity-cli <cmd>` |
| `npm i -g @shiranui_isuzu/unity-mcp` | インストールスクリプト（`install.ps1` / `install.sh`）または `dotnet tool install -g IsuzuUnityCli` |
| `{"command":"node","args":[".../build/index.js"]}` | `{"type":"http","url":"http://127.0.0.1:<port>/mcp","headers":{"Authorization":"Bearer <token>"}}`、または `claude mcp add --transport http` |
| `target` パラメーターで Editor を選ぶ | プロジェクトごとに URL が 1 つ（`target` は廃止） |
| `unity_list_clients` | `isuzu-unity-cli projects` |
| スキル `isuzu-unity-mcp` | スキル `isuzu-unity-cli`（`setup` が古いフォルダーを削除します） |
| Preferences の npm インストーラーウィンドウ | CLI が無い間に出る Preferences の「インストール」ボタン |

## 手順

1. Package Manager でパッケージを 4.0.0 に更新します。
2. `npm uninstall -g @shiranui_isuzu/unity-mcp` で v3 の CLI を削除します。
3. [README](../README.md) の手順で `isuzu-unity-cli` をインストールします。
4. `isuzu-unity-cli setup --mcp` で MCP クライアントを再登録します。node コマンドを指す古いエントリは手で削除してください。
5. curl を使った手順書は `isuzu-unity-cli call` に置き換えるか、登録した MCP クライアント経由にしてください。

## 削除された API

- 公開インターフェース `IMcpCommandHandler` / `IMcpResourceHandler` は削除されました。`[McpTool]` を付けた static メソッドとして書き直してください（[README のツールの追加](../README.md#ツールの追加)）。
- v2 の HTTP ルート `/command`、`/resource`、`/read_logs`、`/execute_code`、`/browse_hierarchy`、`/capture_screenshot`、`/play_mode`、`/inspect`、`/hlsl/errors` は削除されました。
- 設定 `clientInstallationPath` とハンドラー単位の有効・無効は削除されました。`/health` の `handlers[]` / `resources[]` も無くなり、`mcpUrl` / `preferredPort` / `portMismatch` / `toolCount` が追加されています。
- 環境変数 `MCP_DESCRIPTOR_INTERVAL` / `MCP_HEALTH_INTERVAL` / `MCP_RELOAD_RETRY_MAX_MS` / `MCP_PROJECT_API_PORT` と `/proxy` ルートは削除されました。
- MCP プロンプト `code_execute` は削除されました。

変更点の一覧は [CHANGELOG](../jp.shiranui-isuzu.unity-mcp/CHANGELOG.md) を参照してください。
