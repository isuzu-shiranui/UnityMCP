# Unity MCP 統合フレームワーク

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
![Version](https://img.shields.io/badge/version-3.1.0-brightgreen)
![Unity](https://img.shields.io/badge/Unity-2022.3%E2%80%93Unity6-black.svg)
![.NET](https://img.shields.io/badge/.NET-C%23_9.0-purple.svg)
![GitHub Stars](https://img.shields.io/github/stars/isuzu-shiranui/UnityMCP?style=social)

[English Version](./README.en.md)

Unity Editor を Model Context Protocol (MCP) 経由で AI エージェントに、CLI 経由で人間とスクリプトに開放するフレームワークです。

## v3 の特徴

- **ツール定義は Editor 側の1箇所だけ** — C# の static メソッドに `[McpTool]` を付けると、シグネチャから JSON Schema が生成され `GET /tools` で配信されます。TypeScript 側にツール定義はありません。
- **CLI が MCP から独立** — `isuzu-unity-mcp` コマンドは Editor が公開する descriptor ファイルを読んで直接接続します。MCP クライアントを起動しておく必要はありません。
- **メインスレッドが詰まっても応答する** — `MainThread = false` を宣言したツールと `/health` `/jobs` `/tools` はワーカースレッドで応答します。Editor が「Hold on」で固まっている最中こそ状態を知りたいので。
- **遅い処理はジョブになる** — 数秒で終わらない呼び出しは job ID を返します。タイムアウトを返しつつ裏で実行を続ける、ということはありません。
- **認証必須** — 全リクエストに bearer token が要ります。ローカルバインドはアクセス制御ではありません。

## 必要条件

- Unity Editor 2022.3 以降（Unity 6 で検証）
- Node.js 18 以降
- `com.unity.nuget.newtonsoft-json` 3.2.1（依存として自動解決されます）

## はじめに

### インストール

Unity の Package Manager で **Add package from git URL**:

```
https://github.com/isuzu-shiranui/UnityMCP.git?path=jp.shiranui-isuzu.unity-mcp
```

MCP サーバ / CLI:

```bash
cd unity-mcp-ts
npm install
npm run build
npm link          # isuzu-unity-mcp コマンドを使う場合

isuzu-unity-mcp setup   # MCP クライアントへの登録と Claude Code スキルの導入
```

`setup` は既に存在する MCP クライアント設定だけを更新します（使っていないクライアントの設定ファイルを新規に作ったりはしません）。設定ファイル内の他のサーバやキーはそのまま残ります。

```bash
isuzu-unity-mcp doctor      # 何がどこに入っているか、古いものが残っていないか
isuzu-unity-mcp uninstall   # 何を消すかを一覧表示（--yes で実行）
```

### 動作確認

Editor がプロジェクトを開くとサーバが起動し、descriptor ファイルを公開します。

```bash
isuzu-unity-mcp projects   # 起動中の Editor 一覧
isuzu-unity-mcp health     # サーバの状態
isuzu-unity-mcp tools      # 利用可能なツール
```

### Claude Desktop / Claude Code との連携

```json
{
  "mcpServers": {
    "isuzu-unity-mcp": {
      "command": "node",
      "args": ["/absolute/path/to/unity-mcp-ts/build/index.js"]
    }
  }
}
```

Editor を先に起動しておく必要はありません。起動していない間、MCP サーバは前回のツールカタログをキャッシュから返し、Editor が現れたら `tools/list_changed` を送ります。

### CLI

```bash
isuzu-unity-mcp call play_mode_status
isuzu-unity-mcp call console_read_logs --type error --limit 20
isuzu-unity-mcp call scene_browse_hierarchy --json '{"name":"Player","limit":5}'

# C# スニペットはファイルから渡すのが安全（base64 で送られます）
isuzu-unity-mcp call execute_code --file snippet.cs

# Editor が複数起動しているとき
isuzu-unity-mcp call play_mode_status --project MyGame
```

**プロジェクト内で実行していれば `--project` は不要です。** Editor が複数起動していても、カレントディレクトリがどれか1つのプロジェクト配下にあれば、そのプロジェクトが選ばれます。

```bash
cd "H:/Unity Projects/MyGame/Assets/Scripts"
isuzu-unity-mcp call play_mode_status
# [using MyGame — the project this directory belongs to]
```

`isuzu-unity-mcp projects` の `containsWorkingDirectory` が、いまここから届く Editor を示します。どのプロジェクトの外でもない場所から曖昧なまま実行した場合は、推測せずに候補を挙げて止まります。同じ判定は MCP サーバ側でも働くので、Claude Code をプロジェクトで開いていれば `target` の指定は要りません。

エラーは stderr に出て終了コードが非ゼロになるので、そのままスクリプトに組み込めます。

> **`--file` を使う理由**: C# のスニペットをシェルと JSON エンコーダの両方に通すと、文字列リテラル中のバックスラッシュが失われます。結果は「呼び出し側に見えない生成ソース中のコンパイルエラー」になり、原因の特定が非常に困難です。ファイルから読めばそのどちらも経由しません。

## アーキテクチャ

```
MCP クライアント (Claude)                  ターミナル / スクリプト
        │ stdio                                    │
        ▼                                          │
  MCP サーバ (build/index.js)              isuzu-unity-mcp (CLI)
        │                                          │
        │  descriptor を読む ──────────────────────┤
        │  <ポート + トークン>                      │
        ▼                                          ▼
  Unity Editor  :27182-27199  (HttpListener, 127.0.0.1 のみ)
        │
        ├── GET  /tools              属性から生成したツールカタログ
        ├── POST /tools/<name>       ツール実行
        ├── GET  /health             状態・キュー深さ・実行中ジョブ数
        ├── GET  /jobs, /jobs/<id>   長時間処理の追跡
        └── POST /jobs/<id>/cancel   未開始のジョブを中止
```

### Editor 側 (C#)

`ToolCatalog` が `[McpTool]` の付いた static メソッドをリフレクションで収集し、シグネチャから JSON Schema を作ります。`ToolInvoker` が JSON 引数を型付きパラメータに束縛し、`confirm` / `dry_run` ゲートと Undo グルーピングを適用します。

`McpMainThreadDispatcher` がワーカースレッドから Editor メインスレッドへ処理を渡します。キューからの取り出しだけロックし実行はロック外で行うので、遅い処理が他のリクエストの受付を止めません。開始前のジョブは確実に中止できます。

### MCP サーバ側 (TypeScript)

`ToolCatalogClient` が `/tools` を取得し、`ToolRouter` がそれを `tools/list` / `tools/call` として配ります。低レベルのリクエストハンドラを使っているので、**Editor が生成した JSON Schema がそのままクライアントに届きます**。

## 組み込みツール

### Editor が公開するもの（23個）

| ツール | 冪等性 | 用途 |
|---|---|---|
| `execute_code` | unsafe | C# スニペットのコンパイル・実行 |
| `compile_status` | safe | コンパイル中か、直前のコンパイルが成功したか |
| `compile_request` | unsafe | 再コンパイルを要求 |
| `test_run` | unsafe | EditMode / PlayMode テストの実行を開始 |
| `test_results` | safe | 実行中・直近のテスト結果（**実行中でも読める**） |
| `console_read_logs` | safe | コンソールのエントリを読む |
| `console_get_count` | safe | エラー / 警告 / ログの件数 |
| `console_clear` | unsafe | コンソールをクリア |
| `editor_log_tail` | safe | `Editor.log` を直接読む（**Editor が固まっていても動く**） |
| `scene_browse_hierarchy` | safe | シーン階層の走査 |
| `inspect_read` / `inspect_list` | safe | シリアライズプロパティの読み取り・一覧 |
| `inspect_write` | unsafe | シリアライズプロパティの書き込み（Undo 1操作にまとまる） |
| `play_mode_status` | safe | 再生中 / 一時停止中 / コンパイル中 |
| `play_mode_play` / `_stop` / `_pause` / `_unpause` / `_step` | unsafe | Play mode 制御 |
| `capture_screenshot` | safe | Game / Scene ビューや Editor パネルの画像 |
| `menu_execute` | unsafe | メニュー項目の実行 |
| `project_assemblies` | safe | ロード済みアセンブリ一覧 |
| `project_packages` | safe | UPM パッケージ一覧 |

Editor パネルのキャプチャ（`inspector` / `hierarchy` / `project` / `console` / `window:<title>`）は Windows 限定です。`game` と `scene` は全プラットフォームで動きます。

`test_run` / `test_results` は `com.unity.test-framework` が入っているときだけ現れます（Unity の既定パッケージなので通常は入っています）。専用のアセンブリに分けて `UNITY_INCLUDE_TESTS` で制約しているため、無い環境ではこの2つが一覧に出ないだけで、パッケージ全体は変わらず動きます。

### MCP サーバが提供するもの（3個）

`unity_list_clients` / `unity_set_active_client` / `unity_get_active_client` — 複数 Editor の選択。どの Editor 単体にも答えられない問いなので、ここに残っています。

### プロンプト

`code_execute` — `execute_code` に渡す C# の書き方。

## ツールの追加

**Editor 側にメソッドを1つ書くだけです。** TypeScript 側の作業はありません。

```csharp
using System.Linq;
using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

internal static class MyTools
{
    [McpTool(
        "asset_find_by_type",
        "Find project assets of a given type. Prefer a narrow type and a small limit: " +
        "a full asset list is large and rarely relevant to one question.",
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

これだけで `/tools` に現れ、MCP クライアントと CLI の両方から呼べます。JSON Schema はシグネチャから生成されるので、書く場所は1箇所です。

`[McpTool]` の属性:

| プロパティ | 既定値 | 意味 |
|---|---|---|
| `Idempotency` | `Unsafe` | 接続失敗時に自動リトライしてよいか。読み取り専用なら `Safe` |
| `MainThread` | `true` | Editor メインスレッドが必要か。`false` なら Editor が固まっていても応答できる（Unity API を触らないツール限定） |
| `Destructive` | `false` | `true` なら `confirm: true` が無いと実行せず、`dry_run` に対応 |
| `UndoGroup` | `null` | 設定すると呼び出し1回が Undo 1操作にまとまる |

ツール名は `^[a-z][a-z0-9_]{0,63}$`（MCP のツール名にドットは使えません）。

**説明文はモデルがそのツールを選ぶ唯一の手がかりです。** 何をするかだけでなく、どういうときに使うかを書いてください。

## 設定

### Unity Editor (Preferences → Unity MCP)

| 設定 | 既定値 | 意味 |
|---|---|---|
| `httpPort` | 27182 | 開始ポート。使用中なら 27199 まで自動で繰り上がります |
| `autoStartOnLaunch` | true | Editor 起動時にサーバを開始 |
| `syncWaitMs` | 3000 | この時間を超えた処理は job ID を返します |
| `detailedLogs` | true | リクエストログ |

### MCP サーバ環境変数

| 変数 | 既定値 | 意味 |
|---|---|---|
| `MCP_DESCRIPTOR_INTERVAL` | 2000 | descriptor ディレクトリの走査間隔 (ms) |
| `MCP_HEALTH_INTERVAL` | 10000 | `/health` ポーリング間隔 (ms) |
| `MCP_RELOAD_RETRY_MAX_MS` | 15000 | ドメインリロード中のリトライ上限 (ms) |
| `MCP_PROJECT_API_PORT` | 27180 | ProjectApi のポート |

## テスト

```bash
# TypeScript
cd unity-mcp-ts && npm test

# Unity (ヘッドレス)
Unity.exe -batchmode -nographics -projectPath <project> \
  -runTests -testPlatform EditMode -testResults results.xml
```

起動中の Editor に対しては MCP / CLI からも走らせられます。

```bash
isuzu-unity-mcp call test_run --mode edit --assembly MyGame.Tests
isuzu-unity-mcp call test_results          # 進行中でも読める
isuzu-unity-mcp call test_results --include_passed true --limit 200
```

**テスト実行中はメインスレッドが塞がるので、他のツールは応答しません。** `test_results` は `MainThread = false` なので、その最中でも件数と失敗内容を返します。実行を開始した `test_run` はすぐ戻り、結果は待ちません。

Unity 側のテストを走らせるには、プロジェクトの `Packages/manifest.json` に
`"testables": ["jp.shiranui-isuzu.unity-mcp"]` が必要です。

## トラブルシューティング

**`isuzu-unity-mcp projects` が何も返さない** — Editor がプロジェクトを開いていて、サーバが起動しているか確認してください。descriptor は `%LOCALAPPDATA%\UnityMCP\instances\`（macOS / Linux では `~/.local/share` または `~/Library/Application Support` 配下）に作られます。

**401 が返る** — トークンは descriptor ファイルにあります。CLI と MCP サーバは自動で読みますが、curl で直接叩く場合は `Authorization: Bearer <token>` が要ります。`isuzu-unity-mcp` 経由にするか、MCP サーバの `/proxy` を使えばトークンを意識せずに済みます。

**ログが無いはずがないのに空** — `console_read_logs` は Editor コンソールの現在の内容を返します。取りこぼしが疑われるときは `editor_log_tail` でログファイルを直接読んでください。こちらは Editor がビジーでも動きます。

**スクリプトを編集したのに反映されない** — `AssetDatabase.Refresh()` は必ずしも再コンパイルを起こしません。`compile_request` を使い、`compile_status` で `succeeded` を確認してください。コンパイルに失敗すると Editor は直前のアセンブリのまま `isCompiling` を false に戻すので、「静か」＝「成功」ではありません。

**呼び出しが job ID を返した** — `syncWaitMs`（既定3秒）を超えた処理はジョブになります。`isuzu-unity-mcp jobs <id>` で結果を取ってください。**同じ呼び出しをやり直さないでください。** 処理はまだ動いています。

## セキュリティ

- サーバは `127.0.0.1` のみにバインドし、**全リクエストに bearer token を要求します**。
- CORS ヘッダは送りません。v2 は `Access-Control-Allow-Origin: *` を返しており、ユーザーが開いている任意の Web ページが `/execute_code` に POST して Editor 内で任意の C# を実行できました。
- **descriptor ファイルは資格情報として扱ってください。** 読める者は Editor 内でコードを実行できます。
- `execute_code` と `menu_execute` は Editor の全権限で動きます。信頼できないコードを流さないでください。

### ビルドには入りません

このパッケージが任意の C# をコンパイル・実行する HTTP サーバである以上、ビルドに混入すれば遠隔コード実行の穴になります。そうならないことは**2重に保証**されています。

1. アセンブリ定義が `"includePlatforms": ["Editor"]` — プレイヤービルドにコンパイルされません
2. すべてのソースと DLL が `Editor/` 配下 — Unity は importer 設定に関わらずビルドから除外します

**Development Build も含めて、プレイヤーには一切入りません。** ランタイム側のアセンブリ自体が存在しないためです。将来ランタイム機能を足す場合は `DEVELOPMENT_BUILD` で明示的にゲートしてください。

これは規約に頼った約束ではなく、CI が両方を毎回検査します。`Runtime/` にスクリプトを1つ置く、あるいは asmdef の `includePlatforms` を空にする、いずれもビルドが落ちることを確認済みです。

## マシン上に置くもの

状態はすべて1つのルート配下にまとまっているので、消すときは1箇所で済みます。

| パス | 中身 |
|---|---|
| `%LOCALAPPDATA%\UnityMCP\instances\` | 起動中 Editor の descriptor（ポートとトークン）。終了時に自分で削除し、起動時に pid 死亡分を掃除 |
| `%LOCALAPPDATA%\UnityMCP\cache\` | ツールカタログのキャッシュ |
| `~/.claude/skills/isuzu-unity-mcp/` | Claude Code / Codex スキル（`setup` で導入） |
| MCP クライアント設定の `isuzu-unity-mcp` エントリ | `setup` で追加 |

macOS / Linux では `%LOCALAPPDATA%` の位置が `~/.local/share` または `~/Library/Application Support` になります。`isuzu-unity-mcp doctor` が実際の場所を表示します。

```bash
isuzu-unity-mcp uninstall         # 消す対象を一覧表示するだけ
isuzu-unity-mcp uninstall --yes   # 実行
```

`uninstall` は MCP クライアント設定から `isuzu-unity-mcp` エントリだけを取り除き、他のサーバや設定には触れません。Editor が起動中だと descriptor をすぐ再作成してしまうので、その場合は実行を拒否して先に閉じるよう促します。Unity パッケージ本体の削除は Package Manager から行ってください。

## v2 からの移行

破壊的変更です。

| v2 | v3 |
|---|---|
| `/command` の `console.getLogs` 等 | ツール `console_read_logs` 等 |
| `unity_listClients` | `unity_list_clients` |
| `/inspect`（`mode` 引数） | `inspect_read` / `inspect_list` / `inspect_write` |
| `/play_mode`（`action` 引数） | `play_mode_status` / `_play` / `_stop` / … |
| MCP resource `unity://assemblies` | ツール `project_assemblies` |
| `unity_connectToProject` | `unity_set_active_client` |
| UDP ブロードキャストによる発見 | descriptor ファイル |
| 認証なし | bearer token 必須 |
| TypeScript でハンドラを書く | C# に `[McpTool]` を書く |

curl ベースの手順書は `isuzu-unity-mcp call` に置き換えるか、MCP サーバの `/proxy/<project>/...` 経由にしてください。後者はトークンを自動で付与します。

## ライセンス

MIT
