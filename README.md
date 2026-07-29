# Unity MCP 統合フレームワーク

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
![Version](https://img.shields.io/badge/version-3.2.0-brightgreen)
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

- Unity Editor 2022.3 以降（Unity 6.0 / 6.3 / 6.5 の EditMode で検証。6.5 の EntityId 移行にも対応）
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

### Editor が公開するもの（57個）

**診断（見る）**

| ツール | 冪等性 | 用途 |
|---|---|---|
| `console_read_logs` | safe | コンソールのエントリを読む |
| `console_get_count` | safe | エラー / 警告 / ログの件数 |
| `console_clear` | unsafe | コンソールをクリア |
| `editor_log_tail` | safe | `Editor.log` を直接読む（**Editor が固まっていても動く**） |
| `compile_status` | safe | コンパイル中か、直前のコンパイルが成功したか |
| `compile_request` | unsafe | 再コンパイルを要求 |
| `test_run` | unsafe | EditMode / PlayMode テストの実行を開始 |
| `test_results` | safe | 実行中・直近のテスト結果（**実行中でも読める**） |
| `scene_browse_hierarchy` | safe | シーン階層の走査。**`path` を返す**ので編集系にそのまま渡せる |
| `scene_list` | safe | 開いているシーンとビルド設定のシーン |
| `inspect_read` | safe | シリアライズプロパティの読み取り |
| `inspect_list` | safe | シリアライズプロパティの一覧 |
| `play_mode_status` | safe | 再生中 / 一時停止中 / コンパイル中 |
| `project_assemblies` | safe | ロード済みアセンブリ一覧 |
| `project_packages` | safe | UPM パッケージ一覧 |
| `capture_screenshot` | safe | Game / Scene ビューや Editor パネルの画像。`save_path` でファイル出力 |

**オーサリング（作る・変える）** — すべて Undo 1操作にまとまります。

| ツール | 冪等性 | 用途 |
|---|---|---|
| `gameobject_create` | unsafe | GameObject / プリミティブの生成 |
| `gameobject_delete` | unsafe | 削除（Undo で戻せる） |
| `gameobject_duplicate` | unsafe | 複製 |
| `gameobject_reparent` | unsafe | 親子付け。ワールド位置は既定で維持 |
| `gameobject_set_transform` | unsafe | 位置 / 回転 / スケール。**指定した軸だけ**変える |
| `gameobject_set_active` | unsafe | 有効・無効の切り替え |
| `gameobject_add_component` | unsafe | コンポーネント追加 |
| `gameobject_remove_component` | unsafe | コンポーネント削除 |
| `inspect_write` | unsafe | シリアライズプロパティの書き込み |
| `asset_find` | safe | 型 / 名前 / フォルダ / ラベルでアセット検索 |
| `asset_info` | safe | 型・GUID・importer・依存の詳細 |
| `asset_create_folder` | unsafe | フォルダ作成（親も作る、冪等） |
| `asset_move` | unsafe | 移動・リネーム（GUID を維持） |
| `asset_delete` | unsafe | 削除。**OS のゴミ箱行きなので戻せる** |
| `asset_reimport` | unsafe | 再インポート |
| `scene_open` | unsafe | シーンを開く（未保存があれば拒否） |
| `scene_save` | unsafe | 保存 / 別名保存 |
| `scene_create` | unsafe | 新規シーン |
| `prefab_create` | unsafe | シーンオブジェクトを Prefab 化 |
| `prefab_instantiate` | unsafe | Prefab をシーンに配置 |
| `prefab_apply` | unsafe | インスタンスのオーバーライドを Prefab へ適用 |
| `menu_execute` | unsafe | メニュー項目の実行 |
| `play_mode_play` | unsafe | 再生開始 |
| `play_mode_stop` | unsafe | 停止 |
| `play_mode_pause` | unsafe | 一時停止 |
| `play_mode_unpause` | unsafe | 一時停止解除 |
| `play_mode_step` | unsafe | 1フレーム進める |

**描画・シェーダーのデバッグ**

| ツール | 冪等性 | 用途 |
|---|---|---|
| `render_compare` | safe | 2枚のキャプチャの差を**数値で**返す（差分画素数・平均/最大デルタ・矩形・グリッド） |
| `render_pipeline_info` | safe | 実効 RP、色空間、Graphics API、品質レベル。**Quality 側の RP 上書き**も併記 |
| `render_camera_info` | safe | カメラと view / projection / **GPU projection** 行列 |
| `shader_errors` | safe | シェーダーのコンパイルエラー（**黙って magenta になるので聞かないと分からない**） |
| `shader_info` | safe | パス数、プロパティ、キーワード空間、render queue |
| `material_read` | safe | マテリアルの**現在値**・有効キーワード・render queue |
| `material_set` | unsafe | プロパティ / キーワード / render queue を変更 |

**Timeline（動画制作・ライブ）** — `com.unity.timeline` がある時だけ現れます。

| ツール | 冪等性 | 用途 |
|---|---|---|
| `timeline_inspect` | safe | トラック / クリップ / バインディングと director の時刻。**ControlTrack を辿って子 Timeline を再帰展開**する（ライブの多層構造向け） |
| `timeline_evaluate` | unsafe | director を時刻 / フレームに評価（Play mode 不要）。`capture_screenshot` と組んで1コマ検証 |
| `timeline_edit_clip` | unsafe | 1クリップの start / duration / 表示名 / ease / blend / 速度。**要求値でなく実効値を返し**、効かなかった引数は理由付きで `ignored` に出す |
| `timeline_shift_clips` | unsafe | **リップル編集**。指定時刻以降をまとめてずらす。0秒を割る場合は**1つも動かさず**拒否 |
| `timeline_set_track` | unsafe | mute / lock / リネーム / バインディング。トラックの型に必要なコンポーネントを**自前で解決**（Animation なら Animator） |
| `timeline_delete` | unsafe | トラック / クリップの削除。グループは配下ごと。Undo 可能なので確認を求めない |
| `timeline_create` | unsafe | Timeline アセットの新規作成（+ director 付与）。**トラック追加の前提となる唯一の入口** |
| `timeline_create_track` | unsafe | トラック追加（activation / animation / audio / control / group / playable / signal）。グループへのネストとバインド同時指定可 |
| `timeline_create_clip` | unsafe | クリップ追加。`control_source` で**ControlTrack のネストを一発で構成**、`animation_clip` で AnimationClip を指定 |

編集系は**書き込んだ後に読み直した実効値**を返します。Timeline の setter は
クリップ型が対応していない値（Activation クリップの速度など）を**エラーなく捨てる**ため、
要求値をそのまま返すと「設定したつもり」が残るからです。
また作成系は、対象の Timeline が**まだアセットでなければ着手前に拒否**します
（Timeline はその状態だとトラックをメモリ上にしか作らず、後から永続化する公開 API が無いため）。

**Recorder（書き出し）** — `com.unity.recorder` と `com.unity.timeline` が揃っている時だけ現れます。

| ツール | 冪等性 | 用途 |
|---|---|---|
| `recorder_add_track` | unsafe | Timeline に Recorder トラックを追加し、**director を再生するだけで録画**にする。mp4 / webm / mov と png / jpeg / exr、入力は game view / カメラ / RenderTexture、解像度指定可 |
| `recorder_list` | safe | その Timeline が**何をどこへ書き出すか**（形式・出力先・有効/無効） |

Recorder を直接叩かず Timeline のトラックとして持つのは、**フレームレートが Timeline 側から入る**ため
（録画とタイムラインがズレない）で、かつ Recorder API のバージョン差の影響を受けにくいからです。
`output_path` を省くと `Assets` と同階層の `Recording` フォルダに Timeline 名で書き出します。

**内部状態・GPU**

| ツール | 冪等性 | 用途 |
|---|---|---|
| `reflect_read` | safe | 型とメンバーパスで**private を含む live な状態**を読む |
| `reflect_find_type` | safe | ロード済み型の検索 |
| `gpu_readback` | safe | バッファ / テクスチャを読み戻し、**中身でなく統計**（range / mean / zeroCount / histogram）を返す |
| `execute_code` | unsafe | C# スニペットのコンパイル・実行（**専用ツールで届かないときの最後の手段**） |

**ビルド**

| ツール | 冪等性 | 用途 |
|---|---|---|
| `build_settings` | safe | 実効ビルドターゲット、ビルドに入るシーン、モジュールの有無 |
| `build_player` | unsafe | プレイヤービルド。コールドは job、増分はインラインで返る |
| `build_switch_target` | unsafe | ビルドターゲット切替（再インポートを伴う） |

Editor パネルのキャプチャ（`inspector` / `hierarchy` / `project` / `console` / `window:<title>`）は Windows 限定です。`game` と `scene` は全プラットフォームで動きます。

`test_run` / `test_results` は `com.unity.test-framework` が入っているときだけ現れます（Unity の既定パッケージなので通常は入っています）。専用のアセンブリに分けて `UNITY_INCLUDE_TESTS` で制約しているため、無い環境ではこの2つが一覧に出ないだけで、パッケージ全体は変わらず動きます。

Unity Hub の操作（Editor やモジュールのインストール）は**あえて持ちません**。Hub 自身に CLI があるので、未インストールのビルドターゲットを指定したときに実行すべきコマンドを返します。

### 知っておくと事故らないこと

- **編集系ツールが受け取る `path` は `scene_browse_hierarchy` が返すものです。** 非アクティブなオブジェクトも解決でき、兄弟に同名がいるときだけ `/Canvas/Button[1]/Text` と添字が付きます。
- **Play Mode 中のシーン編集は、成功したように見えて終了時に破棄されます。** その状況では応答に `playModeWarning` が付きます。アセットの変更は残るので、そちらには付きません。
- **削除は確認を求めず、戻せるようにしてあります。** アセットは OS のゴミ箱へ、GameObject は Undo 経由です。ただし**未保存シーンへの上書きは拒否します**（これだけは Undo でも戻せないため）。
- **`execute_code` は Undo に乗りません。** オーサリングは専用ツールを使ってください。

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
