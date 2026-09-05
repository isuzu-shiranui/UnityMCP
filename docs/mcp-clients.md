# MCP クライアントの接続

MCP クライアントごとの設定と、Editor が公開する MCP エンドポイントのプロトコル上の性質を説明します。[README に戻る](../README.md)

Editor が `http://127.0.0.1:<port>/mcp` を Streamable HTTP で公開しています。別プロセスの MCP サーバーは不要です。ポートとトークンは Editor の descriptor に書かれています。

`isuzu-unity-cli doctor` は「Running Editors」の行に実際の URL を表示します。ただし、トークンは表示しません。トークンが要るときは、Editor の Preferences > Unity MCP ページを開いてください。Connection の Bearer トークンの行にある「Copy」でクリップボードにコピーできます。

## 自動登録

```bash
isuzu-unity-cli setup --mcp --agent claude-code --scope project
```

`--agent` は `claude-code` / `claude-desktop` / `codex` / `cursor` / `gemini` / `vscode` から選べます。`claude-desktop` は Windows と macOS だけです。Linux には Claude Desktop が無いためです。

`--scope user|project` は Claude Code 向けです。project スコープは `.mcp.json` に `${UNITY_MCP_TOKEN}` を書き込みます。生のトークンは書きません。

`--no-skill` はスキルの導入をスキップします。`--project <name>` で対象プロジェクトを指定します。Editor が起動している必要があります。

CLI が PATH に無い間、Editor の Preferences > Unity MCP ページに「インストール」ボタンが出ます。このボタンはターミナルを開いて、インストールスクリプトを実行します。CLI 自体を入れるためのボタンです。クライアントの登録までは行いません。CLI が見つかると、この行は見つかった旨の表示に変わります。

## クライアントごとの設定

以下は貼り付け先と形を示すものです。実際の値が入ったものが要るときは、Editor の Preferences > Unity MCP ページを開いてください。クライアントを選び、「Show」で確認するか「Copy」でコピーします。

ページに並ぶ設定は 6 つです。Claude Code のコマンド、Cursor と Claude Code の `.mcp.json`、Codex の `config.toml` があります。さらに Gemini CLI の `settings.json`、VS Code の `.vscode/mcp.json`、Claude Desktop の stdio ブリッジがあります。このうち 4 つは、URL とトークンが埋まった状態で出ます。

VS Code のものには、トークンそのものではなく入力を促す参照が入ります。`.vscode/mcp.json` をリポジトリに入れることが多いためです。Claude Desktop のものはトークンを使いません。CLI のパスとプロジェクト名が入ります。

Claude Code:

```bash
claude mcp add --transport http isuzu-unity http://127.0.0.1:<port>/mcp --header "Authorization: Bearer <token>"
```

Cursor（`~/.cursor/mcp.json`）:

```json
{
  "mcpServers": {
    "isuzu-unity": {
      "url": "http://127.0.0.1:<port>/mcp",
      "headers": { "Authorization": "Bearer <token>" }
    }
  }
}
```

Codex（`~/.codex/config.toml`）:

```toml
[mcp_servers.isuzu-unity]
url = "http://127.0.0.1:<port>/mcp"
http_headers = { Authorization = "Bearer <token>" }
```

Gemini CLI（`~/.gemini/settings.json`）は `url` の代わりに `httpUrl` を使います。

VS Code（`.vscode/mcp.json`、ルートキーは `servers`）:

```json
{
  "servers": {
    "isuzu-unity": {
      "type": "http",
      "url": "http://127.0.0.1:<port>/mcp",
      "headers": { "Authorization": "Bearer ${input:isuzu-unity-token}" }
    }
  },
  "inputs": [
    {
      "id": "isuzu-unity-token",
      "type": "promptString",
      "description": "Unity MCP bearer token",
      "password": true
    }
  ]
}
```

## Claude Desktop

Claude Desktop 向けに、拡張機能バンドル `isuzu-unity-cli.mcpb` があります。[Releases](https://github.com/isuzu-shiranui/UnityMCP/releases) からダウンロードしてください。ダウンロードしたファイルをダブルクリックします。

Claude Desktop のウィンドウにドラッグしても同じです。Settings > Extensions > Advanced settings > Install Extension から選んでも同じです。

導入時にプロジェクト名を聞かれます。Unity を 1 つだけ開いているなら、空欄のままで構いません。複数開いているときは、Unity Editor のタイトルバーに表示されている名前を入力します。

Player Settings の Product Name でも通ります。名前は `isuzu-unity-cli projects` でも確認できます。使うときは、パッケージを導入した Unity Editor を開いておいてください。

バンドルには Windows・macOS(Apple Silicon と Intel)・Linux の実行ファイルが入っています。追加のランタイムは不要です。マニフェストが宣言している対応プラットフォームも `win32` / `darwin` / `linux` の 3 つです。次の点に注意してください。

- バンドルは自己署名です。Claude Desktop は導入時に、未署名の拡張機能である旨の警告をログに書きます。動作には影響しません。
- Cowork はローカルの拡張機能を使えません。
- Microsoft Store 版の Claude Desktop には既知の問題があります。.mcpb を開いたときのプレビューが閉じてしまいます。その場合は .mcpb を zip として展開してください。展開したフォルダーを Settings > Extensions > Advanced settings > Install Unpacked Extension で指定します。

バンドルを使わずに手で設定する場合は、`claude_desktop_config.json` に stdio ブリッジを書きます。Claude Desktop はローカルの HTTP エンドポイントに直接つなげないためです。

```json
{
  "mcpServers": {
    "isuzu-unity": {
      "command": "<isuzu-unity-cli のパス>",
      "args": ["mcp-stdio", "--project", "<プロジェクト名>"]
    }
  }
}
```

## ChatGPT

ChatGPT の通常のチャットが受け付ける MCP サーバーは、インターネット上で HTTPS を公開しているものだけです。ローカルの Editor には直接つなげません。

Codex を使う場合は、stdio ブリッジを 1 行で登録できます。

```bash
codex mcp add isuzu-unity -- isuzu-unity-cli mcp-stdio --project <プロジェクト名>
```

Codex は Streamable HTTP にも直接つなげます。`isuzu-unity-cli setup --mcp --agent codex` で上の Codex の設定を書き込む方法でも構いません。

通常のチャットからローカルの Editor を使う経路もあります。公式にサポートされているのは、OpenAI の [Secure MCP Tunnel](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels) です。[openai/tunnel-client](https://github.com/openai/tunnel-client) を `--mcp-command` で stdio ブリッジに向けて起動します。stdio ブリッジのコマンドは `isuzu-unity-cli mcp-stdio --project <プロジェクト名>` です。

そのあと、OpenAI Platform 側でトンネルをワークスペースに関連付けます。ngrok や cloudflared のような第三者のトンネルもあります。認証を付けない限り、この方法で Editor のエンドポイントを公開することはお勧めしません。このエンドポイントは Unity 内で C# を実行できます。

## プロトコル上の性質

- エンドポイントはステートレスです。セッション ID を持ちません。
- プロトコルのバージョンは 2025-11-25 / 2025-06-18 / 2025-03-26 に対応しています。
- `tools/list` にはアノテーションが付きます。`Idempotency` が `Safe` のツールには `readOnlyHint` と `idempotentHint` が付きます。破壊的なツールには `destructiveHint` が付きます。
- `tools/call` はテキストと `structuredContent` の両方を返します。ツール自体のエラーは、トランスポートのエラーにはなりません。`isError` の結果として、モデルに読める形で返ります。
- GET と DELETE は 405 を返します。`Origin` が別ドメインなら 403 を返します。
- `tools/list_changed` は送られません。パッケージの追加・削除や定義ツールの変更で、ツールが増減することがあります。その場合はクライアントを再接続してください。
- MCP の URL には `?group=diagnostics,authoring` のようにグループを付けられます。付けると `tools/list` がそのグループだけを返します。呼び出し自体は絞り込みの影響を受けません。
