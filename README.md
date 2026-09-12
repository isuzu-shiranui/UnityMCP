# Unity MCP 統合フレームワーク

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
![Unity](https://img.shields.io/badge/Unity-2022.3%E2%80%93Unity6-black.svg)
![.NET](https://img.shields.io/badge/.NET-10-purple.svg)
![GitHub Stars](https://img.shields.io/github/stars/isuzu-shiranui/UnityMCP?style=social)

[English Version](./README.en.md)

Unity Editor を AI エージェントに開放するフレームワークです。人が手で実行しても、スクリプトから呼んでも、同じ経路を通ります。

- MCP クライアントは、Editor 自身が公開する Streamable HTTP エンドポイント `http://127.0.0.1:<port>/mcp` に直接接続します。別プロセスの MCP サーバーはありません。Claude Code、Cursor、Codex、Gemini CLI、VS Code、Claude Desktop で動作を確認しています。
- コマンドラインの `isuzu-unity-cli` からも同じツールを呼べます。配布している実行ファイルはネイティブなので、Node も .NET ランタイムも要りません。
- ツールは C# の static メソッドに `[McpTool]` を付けるだけで定義できます。

はじめて使う方は、図つきの導入ガイド [Unity MCP のはじめかた](https://unity-mcp.shiranui-isuzu.dev/) から始めてください。

## 必要条件

- Unity Editor 2022.3 以降。EditMode テストスイートは Unity 6000.0.35f1 で実行しています
- Git クライアント 2.14.0 以降を PATH に通しておいてください。Unity の Package Manager が git URL のパッケージを取得するのに使います（[Unity のマニュアル](https://docs.unity3d.com/Manual/upm-git.html)）。下の VPM リポジトリから入れる場合は要りません
- `com.unity.nuget.newtonsoft-json` 3.2.1。依存として自動で解決されます

## インストール

Unity の Package Manager で **Add package from git URL** を選び、次の URL を入力します。

```
https://github.com/isuzu-shiranui/UnityMCP.git?path=jp.shiranui-isuzu.unity-mcp
```

VCC（VRChat Creator Companion）と ALCOM では、VPM リポジトリ `https://unity-mcp.shiranui-isuzu.dev/vpm.json` を追加してください。どちらもパッケージを zip でダウンロードするので、この経路に Git は要りません。ワンクリックで追加するリンクと、追加する場所の画面は、導入ガイドの [VCC・ALCOM をお使いの場合](https://unity-mcp.shiranui-isuzu.dev/#vpm-title) にあります。

CLI をインストールします。

```bash
# Windows
irm https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.ps1 | iex

# macOS / Linux
curl -fsSL https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.sh | sh
```

GitHub Releases から実行ファイルを直接ダウンロードして、`SHA256SUMS` で検証することもできます。.NET SDK があれば `dotnet tool install -g IsuzuUnityCli` でも入ります。

続けて、エージェント用のスキルと MCP クライアントを登録します。

```bash
isuzu-unity-cli setup                              # Claude Code / Codex 向けのスキル
isuzu-unity-cli setup --mcp --agent claude-code    # MCP クライアントへの登録
```

`--agent` は `claude-code` / `claude-desktop` / `codex` / `cursor` / `gemini` / `vscode` から選べます。Claude Code はサーバーを Unity プロジェクトのパスの下に登録するので、Claude Code を Unity プロジェクトのフォルダーで起動してください。Editor の Preferences > Unity MCP ページからも登録できます。

クライアントごとの設定、Claude Desktop 向けの拡張機能バンドルと stdio ブリッジは [MCP クライアントの接続](docs/mcp-clients.md) にあります。

## 最初のコマンド

Editor がプロジェクトを開くとサーバーが起動し、descriptor ファイルを公開します。CLI はそれを読むので、ポートやトークンの指定は要りません。

```bash
isuzu-unity-cli projects                  # 起動中の Editor 一覧
isuzu-unity-cli tools                     # 利用可能なツール
isuzu-unity-cli call play_mode_status     # ツールの実行
isuzu-unity-cli verify                    # 再コンパイル → エラー抽出 → コンソールのエラー
```

`verify` は、スクリプトを編集したあとの再コンパイルとエラー収集を 1 回の呼び出しにまとめます。`--test` を付けるとテストも実行します。

Editor が複数起動しているときは `--project <name>` で選びます。プロジェクトのディレクトリ内で実行していれば、自動で選ばれます。全コマンドは [CLI リファレンス](docs/cli.md) にあります。

## ツール

診断（コンソール、`Editor.log`、コンパイル状態、テスト、シーン階層、アセットの読み取り）、オーサリング（GameObject・コンポーネント・アセット・シーン・Prefab・Animator Controller の作成と変更）、描画、Timeline / Recorder、ビルド、C# スニペットの実行、Editor への入力の合成があります。オーサリングのツールは、呼び出し 1 回が Undo 1 操作にまとまります。

Timeline のツールは `com.unity.timeline` があるときだけ、Recorder のツールは `com.unity.recorder` と `com.unity.timeline` の両方があるときだけ、`test_run` と `test_results` は `com.unity.test-framework` があるときだけ現れます。

一覧と注意点は [ツール一覧](docs/tools.md) にあります。MCP の URL に `?group=diagnostics,authoring` のようにグループを付けると、`tools/list` がそのグループだけを返します。

## ツールの追加

Editor 側にメソッドを 1 つ書くだけです。

```csharp
using System.Linq;
using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

internal static class MyTools
{
    [McpTool(
        "asset_find_by_type",
        "Find project assets of a given type. Prefer a narrow type and a small limit.",
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

これだけで MCP クライアントと CLI の両方から呼び出せます。JSON Schema はシグネチャから生成されます。`[McpTool]` に指定できるプロパティは [アーキテクチャ](docs/architecture.md) にあります。

C# を書かずに、JSON ファイルでツールを追加することもできます。[定義ツール](docs/defined-tools.md) を参照してください。

## ドキュメント

- [ツール一覧](docs/tools.md): ツールの表と注意点
- [MCP クライアントの接続](docs/mcp-clients.md): クライアントごとの設定、Claude Desktop ブリッジ、プロトコルの性質
- [CLI リファレンス](docs/cli.md): 全コマンド、プロジェクトの選択、終了コード、マシン上に置くもの
- [定義ツール](docs/defined-tools.md): JSON ファイルで `probe` / `script` / `sequence` ツールを追加する
- [Editor 入力の合成・記録・再生](docs/input-tools.md): `input_pointer` / `input_key` / `input_record` / `input_replay`
- [アーキテクチャ](docs/architecture.md): 経路図、Editor 側のクラス、設定、テスト
- [トラブルシューティング](docs/troubleshooting.md)
- [セキュリティ](docs/security.md)
- [v3 からの移行](docs/migration-v3.md)
- [CHANGELOG](jp.shiranui-isuzu.unity-mcp/CHANGELOG.md)

## セキュリティ

サーバーは `127.0.0.1` にだけバインドします。`OPTIONS` を除く全リクエストに bearer token が必要です。descriptor ファイルとトークンファイルは資格情報として扱ってください。これらを読めるものは、Editor 内でコードを実行できます。プレイヤービルドには、Development Build を含めて一切含まれません。詳細は [セキュリティ](docs/security.md) にあります。

## ライセンス

MIT
