# トラブルシューティング

症状ごとに、確認する場所と対処を説明します。[README に戻る](../README.md)

## Package Manager が `No 'git' executable was found` と出して止まるとき

Unity の Package Manager は、git URL のパッケージを取得するのに Git クライアントを呼び出します。PATH に Git が無いとこのエラーで止まります。[git-scm.com](https://git-scm.com/downloads) から入れて Unity を再起動してください。

Git を使わずに入れる経路もあります。VCC（VRChat Creator Companion）と ALCOM はパッケージを zip でダウンロードするので、Git を呼び出しません。

```
https://unity-mcp.shiranui-isuzu.dev/vpm.json
```

この URL を貼り付ける場所は、VCC では Settings ページの Packages タブにある Add Repository です。ALCOM では「パッケージ&テンプレート」の「VPMリポジトリ」ページにある「VPMリポジトリを追加」です。ワンクリックで追加するリンクは [導入ガイド](https://unity-mcp.shiranui-isuzu.dev/#vpm-title) にあります。

追加すると、プロジェクトのパッケージ一覧に Unity MCP が並びます。リポジトリは公開しているすべての版を載せているので、古い版を指定して入れることもできます。

## `isuzu-unity-cli projects` が何も返さないとき

Editor がプロジェクトを開いていて、サーバーが起動しているか確認してください。

descriptor は Windows では `%LOCALAPPDATA%\UnityMCP\instances\` に作られます。macOS と Linux では `~/.local/share` または `~/Library/Application Support` の配下です。

## 401 が返るとき

トークンは `%LOCALAPPDATA%\UnityMCP\tokens\` 配下のファイルにあります。CLI は自動で読みます。curl で直接呼ぶ場合は `Authorization: Bearer <token>` ヘッダーが必要です。

トークンを再生成した場合は、`isuzu-unity-cli doctor --fix` で MCP クライアントの設定を再登録してください。

## ポートが変わったように見えるとき

ポートはプロジェクトのパスから決まるので、通常は再起動しても変わりません。ただし、その値がすでに使用中だと Editor はスキャンして別のポートを選びます。

そのときは `/health` と descriptor の `portMismatch`、そして Preferences の警告で知らせます。MCP クライアントの登録を実際のポートに合わせて更新してください。

## ツールが見当たらないとき

パッケージの追加・削除でツールが増減しても、`tools/list_changed` は送られません。MCP クライアントを再接続してください。

## ログがあるはずなのに空のとき

`console_read_logs` は Editor コンソールの現在の内容を返します。取りこぼしが疑われるときは `editor_log_tail` でログファイルを直接読んでください。こちらは Editor がビジーでも動きます。

## スクリプトを編集したのに反映されないとき

`AssetDatabase.Refresh()` は必ずしも再コンパイルを起こしません。`compile_request` を使い、`compile_status` で `succeeded` を確認してください。

コンパイルに失敗すると、Editor は直前のアセンブリのまま `isCompiling` を false に戻します。エラーが表示されないことは、成功を意味しません。`isuzu-unity-cli verify` はこの一連の確認を 1 回で行います。

## 呼び出しが job ID を返したとき

`syncWaitMs`（既定 3 秒）を超えた処理はジョブになります。`isuzu-unity-cli jobs <id>` または `job_status` ツールで結果を取ってください。同じ呼び出しをやり直さないでください。処理はまだ動いています。

## 定義ツールが一覧に出ないとき

`definitions_list` の `tools` と `errors` を確認してください。`kind` や必須フィールドが誤っている、属性ツールと名前が衝突している、といった理由がここに出ます。

ただし、読み込むのはプロジェクト固有ディレクトリと共有ディレクトリの 2 つだけです。それ以外の場所に置いたファイルは読まれず、`tools` にも `errors` にも出ません。2 つのディレクトリのフルパスは、`/health` の `definitionsDir` と `sharedDefinitionsDir` で確認できます。

## 再生してもカメラが動かないとき

対象は、表示されている Scene View である必要があります。フォーカスはツール側が送信前に自分で移すので、あらかじめ選んでおく必要はありません。移せなかったときだけ `window_not_active` になり、同じ DockArea でタブの裏にあるのがその代表的な原因です。

座標はピクセルでなく point です。スクリーンショットの画素から座標を作る場合は、結果に含まれる `pixelsPerPoint` で割ってください。

## Claude Desktop から接続できないとき

Claude Desktop は、ローカルの HTTP エンドポイントに直接つなげません。`mcp-stdio --project <name>` を指定したブリッジ経由で接続してください。設定例は [MCP クライアントの接続](mcp-clients.md) にあります。

## Editor が Windows で、エージェントが WSL2 の中にあるとき

Editor は Windows 側の `127.0.0.1` にしか束縛せず、descriptor も Windows のプロファイル配下に書きます。`UNITY_MCP_STATE_DIR` と `UNITY_MCP_HOST` の設定手順は [CLI リファレンス](cli.md) を参照してください。

## `test_run` が `scene_dirty` で拒否されるとき、または始まったまま結果が来ないとき

EditMode の実行前に、Test Runner は開いているシーンを閉じます。未保存の変更があると Editor が保存確認のダイアログを出します。そのダイアログが閉じられるまでメインスレッドが止まり、メインスレッドを使う全ツールが応答しなくなります。

`scene_save` で保存するか、変更を破棄してから実行してください。ダイアログで止まった実行は、次の項目の手順で `editor_dialog_press` から Cancel を押すか、Editor 側で Cancel を押します。次は `force: true` を付けて開始してください。

## ツールが running のまま戻らないとき

Editor がモーダルダイアログを表示していると、メインスレッドはそのダイアログのメッセージループの中で止まります。保存確認、パッケージのインポート確認、Asset Store のダイアログ、`EditorUtility.DisplayDialog` などが該当します。

HTTP サーバーはワーカースレッドで動き続けるので、呼び出し自体は受け付けられます。しかしメインスレッドを使う処理はジョブになり、誰かがダイアログに答えるまで `running` のままです。処理が止まっているのではなく、待たされています。

この状態は 3 か所から分かります。`/health` の `mainThread` に `stalledMs` が入ります。待っている処理があるのに、メインスレッドが回っていない時間です。表示中のダイアログは同じ場所の `dialog` に、題名・本文・ボタンとして入ります。ジョブを返す応答と `job_status` の結果は、ダイアログを検出すると `message` にその題名・本文・ボタンを含む一文を追加します。ダイアログが見えないのにメインスレッドが 5 秒以上回っていないときは、その旨を追加します。`isuzu-unity-cli call` と `verify` はこの一文を標準エラー出力に出します。

対処は、まず `editor_dialog_list` で題名・本文・ボタンを読むことです。それから `editor_dialog_press` にボタンの表示文字列と `confirm: true` を渡して押します。`Don't Save` や `Discard` のようなボタンは未保存の作業を捨てます。迷ったときは `Cancel` を押し、原因（未保存のシーンなど）を直してから元の呼び出しをやり直してください。

押した後は、待っていたジョブが `job_status` で完了に変わります。ダイアログの検出と操作は Windows 限定です。他の OS では `editor_dialog_list` が `supported: false` を返すので、Editor 側でダイアログに答えてください。

## フォーカスの無い Editor で呼び出しが約 100 ms かかるとき

Editor はフォーカスを失うと、メインループを約 100 ms 間隔でしか回しません。サーバーは要求が待っている間 Editor を起こすので、通常は数 ms で処理されます。

`/health` の `loopWaker` が `unavailable` なら、その Unity には内部の起こす手段が無く、この待ち時間が残ります。その状態では、複数フレームにまたがる入力ツールも遅くなります。`input_pointer` の既定のドラッグは、`steps` 30 と `frames_per_step` 1 で 33 フレームです。これだけでも約 3 秒かかり、`syncWaitMs` を超えて job id が返ります。Editor にフォーカスを与えるか、`job_status` で結果を取ってください。

## キャプチャが `window_occluded` で拒否されるとき

画面から読み取るキャプチャは、Editor の手前に別のアプリケーションがあると拒否されます。そのまま撮れば、Editor ではなくそのアプリケーションのウィンドウが画像になり、その画像を受け取ったモデルにも渡るためです。

Editor を手前に出してから撮り直すか、`game` と `scene` を使ってください。手前に出す操作は人が行う必要があります。Windows は、今フォーカスを持っているプロセス以外からの前面化を無視するので、エージェント側からは動かせません。この 2 つは Unity がカメラ経由で描くので、手前に何があっても影響を受けません。画面から読み取るのは `inspector` / `hierarchy` / `project` / `console` と、名前が `_window` で終わるものすべてです。

## ツールが多すぎるとき

MCP の URL に `?group=diagnostics,authoring` のようにグループを付けると、`tools/list` がそのグループだけを返します。グループは `diagnostics` / `authoring` / `rendering` / `timeline` / `build` / `code` / `input` の 7 つです。

CLI では `isuzu-unity-cli tools --group <name>` で同じ絞り込みができます。呼び出し自体は、絞り込みの影響を受けません。
