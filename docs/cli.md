# CLI リファレンス

`isuzu-unity-cli` の全コマンドと、プロジェクトの選択規則、終了コード、マシン上に置くファイルを説明します。[README に戻る](../README.md)

CLI は Editor が公開する descriptor ファイルを読みます。そのため、ポートの探索やトークンの取り扱いは不要です。

## コマンド一覧

```bash
isuzu-unity-cli projects                 # 起動中の Editor 一覧
isuzu-unity-cli health                   # サーバーの状態・キュー深さ・実行中ジョブ数
isuzu-unity-cli tools                    # 利用可能なツールと引数名
isuzu-unity-cli tools --group <name>     # グループで絞り込み（カンマ区切りで複数可）
isuzu-unity-cli call <tool> [...]        # ツールの実行
isuzu-unity-cli verify [...]             # 再コンパイル・テスト・結果の要約を 1 回で
isuzu-unity-cli jobs [id]                # ジョブの一覧、または指定 ID の状態
isuzu-unity-cli setup [--mcp] [...]      # スキルの導入と MCP エンドポイントの登録
isuzu-unity-cli doctor [--fix]           # 何がどこに入っているかの診断と修復
isuzu-unity-cli upgrade [--version vX]   # CLI の更新
isuzu-unity-cli uninstall [--yes]        # 消す対象の一覧表示と削除
isuzu-unity-cli mcp-stdio --project <n>  # Claude Desktop 向け stdio ブリッジ
```

## call

```bash
isuzu-unity-cli call play_mode_status
isuzu-unity-cli call console_read_logs --type error --limit 20
isuzu-unity-cli call scene_browse_hierarchy --json '{"name":"Player","limit":5}'
isuzu-unity-cli call execute_code --file snippet.cs
isuzu-unity-cli call play_mode_status --project MyGame
isuzu-unity-cli call play_mode_status --raw          # 結果だけでなく応答全体を表示
```

値の型は自動で決まります。`--limit 20` は数値として送られます。`--active_only true` は真偽値として送られます。

C# のスニペットは `--file` で渡してください。シェルと JSON エンコーダの両方に通すと、文字列リテラル中のバックスラッシュが失われます。その結果、呼び出し側からは見えない生成ソースでコンパイルエラーが起きます。`--file` はスニペットを base64 で送るので、どちらも経由しません。

## プロジェクトの選択

プロジェクト内で実行していれば `--project` は不要です。Editor が複数起動していても同じです。カレントディレクトリがどれか 1 つのプロジェクト配下にあれば、そのプロジェクトが選ばれます。

```bash
cd "/work/UnityProjects/MyGame/Assets/Scripts"
isuzu-unity-cli call play_mode_status
# このディレクトリが属する MyGame の Editor に送られます
```

`isuzu-unity-cli projects` の `containsWorkingDirectory` は、カレントディレクトリから選ばれる Editor を示します。

どのプロジェクトにも属さない場所から実行すると、候補が複数あることがあります。その場合は推測しません。候補を表示して停止します（終了コード 3）。

`--project` はまず完全一致するプロジェクト名を探します。見つからなければ、一意に絞れる部分一致を探します。

## 終了コード

| コード | 意味 |
|---|---|
| 0 | 成功 |
| 1 | エラー（`verify` ではコンパイルエラーかテスト失敗） |
| 2 | 引数の誤り。`call` にツール名が無い場合に返ります。`verify` の `--timeout` に正の数でない値、`--logs` に 0 以上の整数でない値を渡した場合も同じです |
| 3 | Editor が見つからないか、候補が複数ある |
| 4 | `verify` の `--timeout` 超過 |
| 130 | Ctrl+C による中断 |

エラーは stderr に出力されます。そのため、そのままスクリプトに組み込めます。

## verify

`verify` は、スクリプトを編集したあとの一連の操作を 1 回の呼び出しにまとめます。再コンパイルを要求し、ドメインリロードが終わるのを待ちます。そのあとエラーを集め、テストを実行し、結果を要約します。

```bash
isuzu-unity-cli verify                       # 再コンパイル → エラー抽出 → コンソールのエラー
isuzu-unity-cli verify --test                # 加えて EditMode テストを実行して失敗を列挙
isuzu-unity-cli verify --test --filter Foo   # テストを正規表現で絞る（--assembly / --category も可）
isuzu-unity-cli verify --no-compile --test   # コンパイルを飛ばしてテストだけ
isuzu-unity-cli verify --raw                 # 要約を JSON で
```

コンパイル中は Editor のサーバーが一度停止します。`verify` はその間の接続エラーを想定して待ちます。そのあと descriptor を読み直してから続けます。`--timeout` の既定は 300 秒です。

コンソールのエラー件数は表示します。ただし古いエラーが残っていることがあるので、成否には含めません。

## jobs

`syncWaitMs`（既定 3 秒）を超えた処理は job ID を返します。

```json
{"state":"running","jobId":"execute_code-3","poll":"/jobs/execute_code-3"}
```

`isuzu-unity-cli jobs` はジョブの一覧を表示します。`isuzu-unity-cli jobs <id>` は指定した ID の状態と結果を表示します。

job ID が返ったときは、同じ呼び出しをやり直さないでください。処理はまだ動いています。やり直すと 2 回実行されます。

## tools --group

`isuzu-unity-cli tools --group <name>[,<name>]` はツール一覧をグループで絞り込みます。グループは `diagnostics` / `authoring` / `rendering` / `timeline` / `build` / `code` / `input` です。

## setup

```bash
isuzu-unity-cli setup                                            # Claude Code / Codex 向けスキルを導入
isuzu-unity-cli setup --mcp --agent claude-code --scope project  # MCP エンドポイントも登録
```

`--mcp` は Editor が起動している必要があります。URL とトークンは Editor の descriptor から読みます。フラグの詳細は [MCP クライアントの接続](mcp-clients.md) を参照してください。v3 のスキルフォルダーが残っていれば削除します。

## doctor / upgrade / uninstall

```bash
isuzu-unity-cli doctor          # 何がどこに入っているか、古いものが残っていないか
isuzu-unity-cli doctor --fix    # 直せるものは直す（トークン再生成後の再登録など）
isuzu-unity-cli upgrade         # 最新版に更新（--version でバージョン指定）
isuzu-unity-cli uninstall       # 消す対象を一覧表示するだけ
isuzu-unity-cli uninstall --yes # 実行
```

`uninstall` は MCP クライアント設定から `isuzu-unity` エントリだけを取り除きます。他のサーバーや設定には触れません。

Editor が起動中だと descriptor がすぐ再作成されます。その場合は実行を拒否し、先に Editor を閉じるよう案内します。Unity パッケージ本体の削除は Package Manager から行ってください。

## 所要時間の内訳

`UNITY_MCP_TRACE=1` を設定して実行すると、段階ごとの経過時間を標準エラーへ出力します。経過時間はプロセス開始からのものです。

```
trace runtime-start      14.6 ms
trace main               16.1 ms
trace parsed             16.3 ms
trace resolved           16.7 ms
trace request-built      16.9 ms
trace connected          18.4 ms
trace response           20.2 ms
trace reported           20.4 ms
```

`runtime-start` は、OS が記録したプロセス開始時刻から `Main` に入るまでです。これは実行ファイル自身の起動時間にあたります。

`resolved` までが descriptor の読み取りです。`connected` から `response` までが Editor 側の処理時間です。フォーカスの無い Editor では、この区間が長くなります。詳しくは [トラブルシューティング](troubleshooting.md) の `loopWaker` の項を参照してください。

## WSL2 から Windows の Editor へ

Editor は Windows 側の `127.0.0.1` にだけバインドします。descriptor も Windows のプロファイル配下に書きます。そのため、WSL2 側の CLI からは既定では見えません。

`UNITY_MCP_STATE_DIR=/mnt/c/Users/<you>/AppData/Local/UnityMCP` で descriptor の場所を指定してください。あわせて `UNITY_MCP_HOST` に Windows 側のアドレスを指定します。

Windows 側では追加の設定が必要です。`netsh interface portproxy` で該当ポートを転送してください。WSL2 の mirrored networking を有効にする方法でも構いません。この構成は動作保証の対象外です。

## マシン上に置くもの

状態はすべて 1 つのディレクトリ配下にまとまっています。

| パス | 中身 |
|---|---|
| `%LOCALAPPDATA%\UnityMCP\instances\` | 起動中 Editor の descriptor（ポート・MCP URL・トークンの場所など）。Editor 終了時に削除されます。起動時には、プロセスが終了済みのものを削除します |
| `%LOCALAPPDATA%\UnityMCP\tokens\` | プロジェクトごとの bearer token |
| `%LOCALAPPDATA%\UnityMCP\cache\` | ツールカタログのキャッシュ |
| `%LOCALAPPDATA%\UnityMCP\tools\` | [定義ツール](defined-tools.md)の JSON ファイル |
| `%LOCALAPPDATA%\UnityMCP\recordings\` | [入力ツール](input-tools.md)の記録 |
| CLI 本体 | `dotnet tool install` ならグローバルツールの置き場所に置かれます。インストールスクリプトならユーザーごとの実行ファイル置き場に置かれます |
| `~/.claude/skills/isuzu-unity-cli/` | Claude Code 向けスキル（`setup` で導入）。`CLAUDE_CONFIG_DIR` を設定していれば、その配下に置かれます |
| `~/.codex/skills/isuzu-unity-cli/` | Codex 向けスキル（`setup` で導入）。`CODEX_HOME` を設定していれば、その配下に置かれます |
| MCP クライアント設定の `isuzu-unity` エントリ | `setup --mcp` で追加されます |

macOS / Linux では `%LOCALAPPDATA%` の位置が `~/.local/share` または `~/Library/Application Support` になります。`isuzu-unity-cli doctor` が実際の場所を表示します。
