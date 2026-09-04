# アーキテクチャ

CLI と MCP クライアントが Editor へ届く経路、Editor 側の主要クラス、設定、テストの実行方法を説明します。[README に戻る](../README.md)

## 全体像

```
AI エージェント                     MCP クライアント              Claude Desktop
        │                                  │                          │
        ▼                                  │ Streamable HTTP          │ stdio
  isuzu-unity-cli (CLI)                    │                          ▼
        │                                  │                    mcp-stdio ブリッジ
        │  descriptor を読む               │                          │
        │  <ポート + トークン>             │                          │
        ▼                                  ▼                          │
  Unity Editor  :27200-27999  (HttpListener, 127.0.0.1 のみ)  ◀───────┘
        │
        ├── GET  /tools              属性から生成したツールカタログ
        ├── POST /tools/<name>       ツール実行
        ├── POST /mcp                MCP over Streamable HTTP
        ├── GET  /health             状態・キュー深さ・実行中ジョブ数
        ├── GET  /jobs, /jobs/<id>   長時間処理の追跡
        └── POST /jobs/<id>/cancel   未開始のジョブを中止
```

- ツール定義は Editor 側の 1 箇所だけです。C# の static メソッドに `[McpTool]` を付けると、シグネチャから JSON Schema が生成されます。生成されたツールは CLI と MCP の両方に配信されます。
- MCP は Editor 自身が配信します。別プロセスの MCP サーバーはありません。
- `MainThread = false` を宣言したツールと `/health` / `/jobs` / `/tools` はワーカースレッドで応答します。Editor がメインスレッドで固まっている最中でも状態を確認できます。
- 数秒で終わらない呼び出しは job ID を返します。タイムアウトを返しつつ裏で実行を続けることはありません。`job_status` ツールが結果の取得を担当します。
- ポートはプロジェクトのパスから 27200〜27999 の範囲に決まるので、Editor を再起動しても URL が変わりません。
- 全リクエストに bearer token が必要です。

## Editor 側 (C#)

`ToolCatalog` が `[McpTool]` の付いた static メソッドをリフレクションで収集し、シグネチャから JSON Schema を作ります。`ToolInvoker` が JSON 引数を型付きパラメーターに束縛し、`confirm` / `dry_run` ゲートと Undo グルーピングを適用します。

MCP エンドポイントも CLI の `/tools/<name>` も、同じ `ToolCatalog` と `ToolInvoker` を通ります。

`McpMainThreadDispatcher` が、ワーカースレッドから Editor メインスレッドへ処理を渡します。キューからの取り出しだけをロックし、実行はロックの外で行います。そのため、遅い処理が他のリクエストの受付を止めません。開始前のジョブは確実に中止できます。

フォーカスの無い Editor は、メインループを約 100 ms 間隔でしか回しません。サーバーは要求が待っている間だけ Editor を起こすので、通常は数 ms で処理されます。`/health` の `loopWaker` が `on-demand` / `always` / `unavailable` のどれかを示します。

## 設定（Preferences > Unity MCP）

この設定は Unity の Preferences フォルダーに保存され、その PC の Unity すべてで共有します。プロジェクトごとの設定ではありません。`httpPort` に正の値を入れると、他のプロジェクトも同じポートを試すことになります。既定値は、その PC でまだ一度も保存していない場合の値です。


| 設定 | 既定値 | 意味 |
|---|---|---|
| `httpPort` | 0 | 0 ならプロジェクトのパスから決まるポートを使う。正の値を指定するとそのポートに固定される（変更後は MCP クライアントの再登録が必要） |
| `autoStartOnLaunch` | true | Editor 起動時にサーバーを開始 |
| `syncWaitMs` | 3000 | この時間を超えた処理は job ID を返します |
| `detailedLogs` | false | 各リクエストと起動・停止の各段階を Unity のコンソールに書きます。これらは `console_read_logs` の結果にも混ざるので、既定では出しません。警告とエラーは、この設定に関わらず出ます |
| `keepEditorAwake` | false | フォーカスの無い Editor は約 100 ms 間隔でしか回りません。サーバーは要求が待っている間だけ Editor を起こします。これを有効にすると、セッション中ずっと起こし続けます（待機中の CPU を使います） |
| `uiLanguage` | 0 | Preferences ページの言語です。0 は Editor の言語に合わせ、1 は English、2 は 日本語 になります。ツールの説明文と CLI の出力は英語のままです |

## テスト

```bash
# CLI
cd isuzu-unity-cli && dotnet test

# Unity (ヘッドレス)
Unity.exe -batchmode -nographics -projectPath <project> \
  -runTests -testPlatform EditMode -testResults results.xml
```

起動中の Editor に対しては CLI や MCP クライアントからも走らせられます。

```bash
isuzu-unity-cli call test_run --mode edit --assembly MyGame.Tests
isuzu-unity-cli call test_results          # 進行中でも読める
isuzu-unity-cli call test_results --include_passed true --limit 200
```

テスト実行中はメインスレッドが塞がるので、他のツールは応答しません。`test_results` は `MainThread = false` なので、その最中でも件数と失敗内容を返します。実行を開始した `test_run` はすぐ戻り、結果を待ちません。

Unity 側のテストを走らせるには、プロジェクトの `Packages/manifest.json` に `"testables": ["jp.shiranui-isuzu.unity-mcp"]` が必要です。

EditMode スイートは Unity 2022.3.22f1 / 6000.0.35f1 / 6000.5.10f1 で検証しています。
