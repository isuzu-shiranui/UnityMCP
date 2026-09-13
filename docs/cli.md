# CLI リファレンス

`isuzu-unity-cli` の全コマンドと、プロジェクトの選択規則、終了コード、マシン上に置くファイルを説明します。[README に戻る](../README.md)

CLI は Editor が公開する descriptor ファイルを読みます。そのため、ポートの探索やトークンの取り扱いは不要です。

## コマンド一覧

```bash
isuzu-unity-cli projects                 # 起動中の Editor 一覧
isuzu-unity-cli health                   # サーバーの状態・キュー深さ・実行中ジョブ数
isuzu-unity-cli tools                    # 利用可能なツールと引数名
isuzu-unity-cli tools --group <name>     # グループで絞り込み（カンマ区切りで複数可）
isuzu-unity-cli tools <tool>             # 1 つのツールの説明と引数だけ
isuzu-unity-cli call <tool> [...]        # ツールの実行
isuzu-unity-cli verify [...]             # 再コンパイル・テスト・結果の要約を 1 回で
isuzu-unity-cli jobs [id]                # ジョブの一覧、または指定 ID の状態
isuzu-unity-cli jobs <id> --wait         # 終わるまで待って、最後の結果だけを表示
isuzu-unity-cli setup [--mcp] [...]      # スキルの導入と MCP エンドポイントの登録
isuzu-unity-cli doctor [--fix]           # 何がどこに入っているかの診断と修復
isuzu-unity-cli update [--dry-run]       # CLI とパッケージを揃えて更新
isuzu-unity-cli upgrade [--release vX]   # CLI の更新
isuzu-unity-cli uninstall [--yes]        # 消す対象の一覧表示と削除
isuzu-unity-cli mcp-stdio --project <n>  # Claude Desktop 向け stdio ブリッジ
isuzu-unity-cli mcp-stdio --group <g>    # クライアントに渡すツールをグループで絞る
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

値の型は自動で決まります。`--limit 20` は数値として送られます。`--active_only true` は真偽値として送られます。同じオプションを 2 回以上書くと配列になります（`--paths one --paths two`）。引用符が通らないシェルで配列を渡すのはこの書き方です。

JSON は端末では整形して出力し、パイプやリダイレクトのときは詰めて出力します。端末でも詰めたいときは `--compact` を付けてください。

C# のスニペットは `--file` で渡してください。シェルと JSON エンコーダの両方に通すと、文字列リテラル中のバックスラッシュが失われます。その結果、呼び出し側からは見えない生成ソースでコンパイルエラーが起きます。`--file` はスニペットを base64 で送るので、どちらも経由しません。

## プロジェクトの選択

プロジェクト内で実行していれば `--project` は不要です。Editor が複数起動していても同じです。カレントディレクトリがどれか 1 つのプロジェクト配下にあれば、そのプロジェクトが選ばれます。

```bash
cd "/work/UnityProjects/MyGame/Assets/Scripts"
isuzu-unity-cli call play_mode_status
# このディレクトリが属する MyGame の Editor に送られます
```

`isuzu-unity-cli projects` の `containsWorkingDirectory` は、カレントディレクトリから選ばれる Editor を示します。

どのプロジェクトにも属さない場所から実行すると、起動中の Editor が 1 つならそれを選びます。複数あるときは推測せず、候補を表示して停止します（終了コード 3）。

Unity プロジェクトのフォルダーの中から実行したのに、そのプロジェクトを開いている Editor が無いときも停止します（終了コード 3）。ほかに開いている Editor には送りません。そのプロジェクトを開くか、`--project` で別のプロジェクトを指定してください。

`--project` は次の順に探します。

1. 製品名（Player Settings の Product Name）の完全一致。大文字と小文字は区別しません。
2. フォルダー名（Editor のタイトルバーに出る名前）の完全一致。大文字と小文字は区別しません。
3. 製品名かフォルダー名の部分一致。候補が 1 つに絞れるときだけ選びます。`mcp-stdio`、`setup --mcp`、`update --project` はこの段を使いません。選んだプロジェクトに接続を固定したり、そのプロジェクトのファイルを書き換えたりするためです。

`/` か `\` を含む値と、`.`、`..` はパスとして扱います。相対パスは作業ディレクトリを基準に解決し、そのパスのプロジェクトだけを選びます。名前との一致は試しません。パスは大文字と小文字を区別して比べます。Windows のドライブ文字だけは区別しません。

一度選択すると、`verify`、`jobs --wait`、`mcp-stdio` は同じプロジェクトパスにだけ再接続します。ポート、トークン、製品名の変更には追従しますが、別のプロジェクトへ自動的に切り替わることはありません。同じパスの descriptor が複数あるときは、それぞれのトークンで `/health` に問い合わせ、応答した 1 つを使います。応答が 1 つに決まらないときと、そのパスを開いている Editor が無いときは、理由と切り替え方を表示して停止します。別のプロジェクトを使うには、コマンドを新しく実行してください。`mcp-stdio` の場合は MCP サーバーを再起動します。

`mcp-stdio` は Editor を開く前に起動でき、最初にプロジェクトを選択した時点で接続先を固定します。絶対プロジェクトパスを持たない descriptor も初回の接続には使えますが、接続失敗後に安全に再検出することはできません。

次のときは、同じプロジェクトでも再接続を断ります。いずれもパスの書き方が変わるためです。

- ジャンクション、`subst`、8.3 形式の短い名前など、別の書き方のパスで開き直したとき
- ドライブ文字以外の大文字と小文字を変えて開き直したとき
- プロジェクトを移動またはコピーしたとき

## 終了コード

| コード | 意味 |
|---|---|
| 0 | 成功 |
| 1 | エラー（`verify` ではコンパイルエラー、テスト失敗、または判定不能のテスト） |
| 2 | 引数の誤り。そのコマンドが持たないオプション、値の無いオプション、`call` にツール名が無い場合に返ります。`verify` の `--timeout` に正の数でない値、`--logs` に 0 以上の整数でない値を渡した場合も同じです |
| 3 | Editor が見つからない、候補が複数あって決められない、選んだプロジェクトに再接続できない、または Editor がトークンを拒否し続けた |
| 4 | `verify` または `jobs --wait` の `--timeout` 超過 |
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

`--test` では、完了した実行に失敗または判定不能（inconclusive）のテストがあれば終了コード 1 を返します。スキップだけでは失敗になりません。Editor が返す詳細を制限していても、件数は実行全体の集計です。`--raw` の出力では、`tests.inconclusive` が判定不能の件数、`tests.truncated` が詳細の一部省略を示します。`tests.failures` には、Editor が返した詳細のうち、成功でもスキップでもないものだけが含まれます。

トークンが拒否されたときも descriptor を読み直します。トークンが変わっていなければ 15 秒のあいだ読み直しを続け、それでも拒否されれば終了コード 3 で停止します。`--raw` のときは、それまでに実行した手順の要約を `ok: false` として出力します。

コンソールのエラー件数は表示します。ただし古いエラーが残っていることがあるので、成否には含めません。

## jobs

`syncWaitMs`（既定 3 秒）を超えた処理は job ID を返します。

```json
{"state":"running","jobId":"execute_code-3","poll":"/jobs/execute_code-3"}
```

`isuzu-unity-cli jobs` はジョブの一覧を表示します。`isuzu-unity-cli jobs <id>` は指定した ID の状態と結果を表示します。

job ID が返ったときは、同じ呼び出しをやり直さないでください。処理はまだ動いています。やり直すと 2 回実行されます。

`--wait` を付けると、終わるまで一定間隔で問い合わせて、最後の答えだけを表示します。待っている間に Editor を止めているもの（コンパイル中、ダイアログが出ている、メインスレッドが戻ってこない）は標準エラーへ出します。終了コードは、ジョブが完了したとき 0、失敗または取り消しのとき 1、オプションの誤りで 2、Editor が見つからないか複数あって決められないとき 3、待ちを打ち切ったとき 4 です。

`--timeout <秒>`（既定 300）で待ちを打ち切ります。打ち切ってもジョブ自体は Editor の中で動き続けます。

## tools --group

`isuzu-unity-cli tools --group <name>[,<name>]` はツール一覧をグループで絞り込みます。グループは `diagnostics` / `authoring` / `rendering` / `timeline` / `build` / `code` / `input` です。ツール名を 1 つ渡すと、そのツールの説明と引数だけを表示します。一覧全体を読むより小さく済みます。

## setup

```bash
isuzu-unity-cli setup                                            # Claude Code / Codex 向けスキルを導入
isuzu-unity-cli setup --mcp --agent claude-code --scope project  # MCP エンドポイントも登録
```

`--mcp` は Editor が起動している必要があります。URL とトークンは Editor の descriptor から読みます。フラグの詳細は [MCP クライアントの接続](mcp-clients.md) を参照してください。v3 のスキルフォルダーが残っていれば削除します。

`--project` は完全一致とパスだけで探し、部分一致は使いません。見つからないときは、その理由を表示します。

## update

CLI と Unity パッケージは 1 つのバージョンとして公開され、リリースは両者が一致しないと止まります。それでも手元では簡単にずれます。`upgrade` が入れ替えるのは CLI だけで、パッケージの更新は別の場所で行うためです。

```bash
isuzu-unity-cli update --dry-run   # 何が変わるかだけ表示
isuzu-unity-cli update             # CLI と、起動中の Editor のパッケージを揃える
isuzu-unity-cli update --project X # そのプロジェクトのパッケージだけ
```

パッケージの導入経路 6 通りのうち 4 通りはここからは更新できません。その場合は、動かす手順を名指しで示します。

| 導入経路 | `update` の動作 |
|---|---|
| `Packages/manifest.json` の git URL | 依存のタグを書き換える |
| レジストリのバージョン指定 | バージョンを書き換える |
| `file:` の作業コピー | 断ります。前に進めるのは git の仕事です |
| `Packages/` 直下の実体 | 断ります。Unity はそちらを読んで manifest を無視するので、manifest を書き換えても何も読まれない行が変わるだけです |
| VCC / ALCOM | 断ります。`vpm-manifest.json` で管理されているので、そちらで更新してください |

書き換えたあと、Unity は Editor にフォーカスが戻った時点で解決します。待たずに反映するには `package_resolve` を呼びます。

CLI を winget か `dotnet tool` で入れた場合、`update` と `upgrade` は CLI を置き換えません。その道具の更新コマンドを表示し、`--release` は受け付けません。CLI を更新するまでは、各プロジェクトのパッケージを CLI と同じバージョンまでしか上げません。すでにそれより新しいバージョンのプロジェクトは、そのままにします。

新しいリリースがあるときは、他のすべてのコマンドが標準エラーに 1 行だけ出します。この行は `doctor` か `update` が最後に調べた結果を読んでいるだけで、ネットワークには触れません。`call` 1 回が約 20 ms で終わるのに対し、GitHub への問い合わせは 5 秒のタイムアウトを持つためです。

## doctor / upgrade / uninstall

```bash
isuzu-unity-cli doctor          # 何がどこに入っているか、古いものが残っていないか
isuzu-unity-cli doctor --fix    # 直せるものは直す（古くなったスキル、ポートが変わった登録など）
isuzu-unity-cli upgrade         # CLI だけを最新版に更新（--release でバージョン指定）
isuzu-unity-cli uninstall       # 消す対象を一覧表示するだけ
isuzu-unity-cli uninstall --yes # 実行
```

`doctor --fix` が直すのは、トークンで起動中の Editor と結び付く登録の URL と、Claude Code の登録のうちプロジェクトのパスで結び付くものです。URL だけが一致してトークンが違う登録は、どのプロジェクトの登録か分からないので書き換えず、`setup --mcp` を案内します。

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
