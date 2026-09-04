# セキュリティ

サーバーのアクセス制御、資格情報の置き場所、プレイヤービルドに混入しない保証を説明します。[README に戻る](../README.md)

## アクセス制御

- サーバーは `127.0.0.1` にだけバインドします。`OPTIONS` を除く全リクエストに bearer token が必要です。ローカルアドレスへのバインドだけでは、アクセス制御になりません。`OPTIONS` は CORS のプリフライトで、トークンを見ずに本文の無い 204 を返します。許可を与えるヘッダーを一切返さないので、ブラウザは続く本番のリクエストを送りません
- CORS ヘッダーは送りません。ブラウザで開いた Web ページから、Editor の HTTP サーバーを呼び出すことはできません
- `Origin` ヘッダーを検査するのは MCP エンドポイント `/mcp` だけです。別ドメインの `Origin` を持つ要求には 403 を返します。`/health` や `/tools` などの REST ルートは `Origin` を見ず、bearer token だけでアクセスを制御します
- `execute_code` と `menu_execute` は Editor の全権限で動きます。信頼できないコードを流さないでください
- 定義ツールのディレクトリも、コード実行の入口です。詳しくは次の節を読んでください

### 定義ツールのディレクトリ

共有ディレクトリは `<root>/UnityMCP/tools/shared/` です。Windows では `%LOCALAPPDATA%\UnityMCP\tools\shared\` になります。

ここに置かれた JSON ファイルは、ファイル名を問わず自動で読み込まれます。読み込まれたツールは、すべてのプロジェクトで使えます。

`script` 定義の `file` は、任意の絶対パスの `.cs` を指せます。つまり、このディレクトリに書き込めるプロセスは Editor 内でコードを実行できます。トークンファイルと同じ信頼レベルで扱ってください。

プロジェクト固有ディレクトリ `<root>/UnityMCP/tools/<projectHash>/` も同じ性質を持ちます。影響範囲がそのプロジェクトに限られる点だけが異なります。

## 資格情報

descriptor ファイルとトークンファイルは、資格情報として扱ってください。これらを読めれば、Editor 内でコードを実行できます。

トークンは `%LOCALAPPDATA%\UnityMCP\tokens\` に保存されます。macOS と Linux では `~/.local/share/UnityMCP/tokens/` で、所有者だけが読めるパーミッションが付きます。

Preferences の「Regenerate」でトークンを再発行できます。再発行後は `isuzu-unity-cli doctor --fix` で登録済みクライアントを更新してください。

`setup --mcp --scope project` は `.mcp.json` に `${UNITY_MCP_TOKEN}` を書き込み、生のトークンは書きません。リポジトリに含まれる設定ファイルにトークンが入ることはありません。

## ビルドには入りません

このパッケージは、任意の C# をコンパイルして実行する HTTP サーバーです。ビルドに混入すれば、リモートコード実行の脆弱性になります。混入しないことは 2 重に保証されています。

1. アセンブリ定義が `"includePlatforms": ["Editor"]` です。プレイヤービルドにコンパイルされません。
2. すべてのソースと DLL が `Editor/` 配下にあります。Unity は importer 設定に関わらずビルドから除外します。

**Development Build も含めて、プレイヤーには一切入りません。** ランタイム側のアセンブリ自体が存在しないためです。将来ランタイム機能を足す場合は、`DEVELOPMENT_BUILD` で明示的にゲートしてください。

この 2 点は CI が毎回検査します。`Runtime/` にスクリプトを 1 つ置く、あるいは asmdef の `includePlatforms` を空にすると、CI が失敗します。

## パネルのキャプチャは画面に写っているものを撮ります

`capture_screenshot` のうち、`inspector` / `hierarchy` / `project` / `console` / `game_view_window` / `scene_view_window` / `window:<タイトル>` は、Unity が描いた内容ではなく、画面のその領域を読み取ります。名前に `_window` が付くものは、すべてこちらです。Editor に別のアプリケーションが重なっていれば、その内容が画像に入ります。画像はそのままモデルにも渡ります。

手前にあるのが別のプロセスなら、`window_occluded` で拒否します。ただし、同じ Editor に属する別のウィンドウが対象に重なっている場合は検出できません。Package Manager や Preferences のような浮いたウィンドウが該当します。写るのは Unity 自身の画面ですが、撮りたかったものではありません。

関係のない情報を写したくないときは、Unity がレンダリングする `game` と `scene` を使ってください。あるいは、撮る前に手前のウィンドウを片付けてください。
