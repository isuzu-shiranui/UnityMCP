# Unity MCP 統合フレームワーク

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
![Version](https://img.shields.io/badge/version-4.0.4-brightgreen)
![Unity](https://img.shields.io/badge/Unity-2022.3%E2%80%93Unity6-black.svg)
![.NET](https://img.shields.io/badge/.NET-10-purple.svg)
![GitHub Stars](https://img.shields.io/github/stars/isuzu-shiranui/UnityMCP?style=social)

[English Version](./README.en.md)

はじめて使う方は、図つきの導入ガイド [Unity MCP のはじめかた](https://unity-mcp.shiranui-isuzu.dev/) から始めてください。

Unity Editor を AI エージェントに開放するフレームワークです。人が手で実行しても、スクリプトから呼んでも、同じ経路を通ります。

主な経路はコマンドラインの `isuzu-unity-cli` です。配布している実行ファイルはネイティブなので、Node も .NET ランタイムも要りません。

MCP クライアントは、Editor 自身が公開する Streamable HTTP エンドポイント `http://127.0.0.1:<port>/mcp` に直接つながります。別プロセスの MCP サーバーはありません。

ツールは C# の static メソッドに `[McpTool]` を付けるだけで定義できます。CLI と MCP の両方に配信されます。

ポートはプロジェクトのパスから決まるので、Editor を再起動しても変わりません。ツールを呼ぶには bearer token が必要です。

## 必要条件

- Unity Editor 2022.3 以降。EditMode スイートは 2022.3.22f1 / 6000.0.35f1 / 6000.5.10f1 で検証しています
- Git クライアント 2.14.0 以降を PATH に通しておいてください。Unity の Package Manager が git URL のパッケージを取得するのに使います（[Unity のマニュアル](https://docs.unity3d.com/Manual/upm-git.html)）。下の VPM リポジトリから入れる場合は要りません
- `com.unity.nuget.newtonsoft-json` 3.2.1。依存として自動で解決されます
- CLI に Node.js は不要です。`dotnet tool install` でインストールする場合のみ .NET SDK が必要です

Unity 6.5 以降では、`instanceId` が JSON の数値ではなく文字列で返ります。64 ビットの EntityId は JavaScript の数値では正確に表せないためです。引数の `instance_id` は数値と文字列のどちらでも受け付けます。

## インストール

Unity の Package Manager で **Add package from git URL** を選び、次の URL を入力します。

```
https://github.com/isuzu-shiranui/UnityMCP.git?path=jp.shiranui-isuzu.unity-mcp
```

VCC（VRChat Creator Companion）と ALCOM では、VPM リポジトリからも入れられます。どちらもパッケージを zip でダウンロードするので、この経路に Git は要りません。

```
https://unity-mcp.shiranui-isuzu.dev/vpm.json
```

この URL を貼り付ける場所は、VCC では Settings ページの Packages タブにある Add Repository です。ALCOM では「パッケージ&テンプレート」の「VPMリポジトリ」ページにある「VPMリポジトリを追加」です。追加すると、プロジェクトのパッケージ一覧に Unity MCP が並びます。ワンクリックで追加するリンクは、導入ガイドの [VCC・ALCOM をお使いの場合](https://unity-mcp.shiranui-isuzu.dev/#vpm-title) にあります。

CLI をインストールします。

```bash
# Windows
irm https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.ps1 | iex

# macOS / Linux
curl -fsSL https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.sh | sh

# .NET SDK がある場合
dotnet tool install -g IsuzuUnityCli
```

GitHub Releases から実行ファイルを直接ダウンロードすることもできます。ファイル名は `isuzu-unity-cli-win-x64.exe` / `-osx-arm64` / `-osx-x64` / `-linux-x64` で、`SHA256SUMS` で検証できます。CLI が PATH に無い間は、Editor の Preferences > Unity MCP ページに「インストール」ボタンが出ます。

インストールできたら、Claude Code / Codex 向けのスキルを導入します。

```bash
isuzu-unity-cli setup
```

## 最初のコマンド

Editor がプロジェクトを開くとサーバーが起動し、descriptor ファイルを公開します。CLI はそれを読むので、ポートやトークンの指定は要りません。

```bash
isuzu-unity-cli projects                  # 起動中の Editor 一覧
isuzu-unity-cli health                    # サーバーの状態
isuzu-unity-cli tools                     # 利用可能なツール
isuzu-unity-cli call play_mode_status     # ツールの実行
isuzu-unity-cli verify                    # 再コンパイル → エラー抽出 → コンソールのエラー
```

`verify` は、スクリプトを編集したあとの再コンパイルとエラー収集を 1 回の呼び出しにまとめます。`--test` を付けるとテストも実行します。

Editor が複数起動しているときは `--project <name>` で選びます。プロジェクトのディレクトリ内で実行していれば、自動で選ばれます。全コマンドは [CLI リファレンス](docs/cli.md) にあります。

## MCP クライアントとの連携

Claude Code の場合はこうなります。

```bash
claude mcp add --transport http isuzu-unity http://127.0.0.1:<port>/mcp --header "Authorization: Bearer <token>"
```

ポートは `isuzu-unity-cli doctor` の「Running Editors」に出る URL に含まれています。トークンはそこには出ません。Editor の Preferences > Unity MCP ページを開いてください。Connection の Bearer トークンの行にある「Copy」を押すとコピーできます。

トークンを自分で扱いたくない場合は、CLI に登録を任せられます。

```bash
isuzu-unity-cli setup --mcp --agent claude-code
```

`--agent` は `claude-code` / `claude-desktop` / `codex` / `cursor` / `gemini` / `vscode` から選べます。

Claude Desktop には拡張機能バンドルもあります。[Releases](https://github.com/isuzu-shiranui/UnityMCP/releases) の `isuzu-unity-cli.mcpb` をダブルクリックすると入ります。

クライアントごとの設定、Claude Desktop 向けの stdio ブリッジ、プロトコル上の性質は [MCP クライアントの接続](docs/mcp-clients.md) にあります。

## ツール

Editor は最大で 88 個のツールを公開します。Timeline の 9 個と Recorder の 2 個は、`com.unity.timeline` と `com.unity.recorder` があるときだけ現れます。`test_run` と `test_results` は `com.unity.test-framework` があるときだけです。どれも入っていないプロジェクトが公開するのは 75 個のツールです。

一覧と注意点は [ツール一覧](docs/tools.md) にあります。

| グループ | 内容 |
|---|---|
| 診断 | コンソール、`Editor.log`、コンパイル状態、テスト、シーン階層、シリアライズプロパティとアセットの読み取り、Animator Controller の読み取りと問題の洗い出し、スクリーンショット、ジョブの状態 |
| オーサリング | GameObject・コンポーネント・アセット・シーン・Prefab の作成と変更、Animator Controller のレイヤー・ステート・遷移・パラメーターの編集、メニュー実行、Play Mode の制御。GameObject 系の 8 つと `inspect_write`、`prefab_create`、`prefab_instantiate`、`animator_` の編集用 10 個は Undo 1 操作にまとまります |
| 描画 | パイプライン・カメラ・シェーダー・マテリアルの実効値、GPU バッファとテクスチャの統計、2 枚のキャプチャの数値比較 |
| Timeline / Recorder | トラック・クリップの検査と編集、時刻への評価、Recorder トラックの追加。該当パッケージがあるときだけ現れます |
| ビルド | ビルド設定、プレイヤービルド、ターゲット切替 |
| コード | リフレクションによる内部状態の読み取り、C# スニペットの実行。読み取りはプロパティの getter を呼ぶので、Unity の一部の getter はシーンを変えます |
| 入力 | Editor の GUI 経路へのマウス・キー入力の合成と、記録・再生 |

MCP の URL に `?group=diagnostics,authoring` のようにグループを付けると、`tools/list` がそのグループだけを返します。

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

これだけで `/tools` に現れ、MCP クライアントと CLI の両方から呼べます。JSON Schema はシグネチャから生成されます。

`[McpTool]` の属性は 8 つあります。

| プロパティ | 既定値 | 意味 |
|---|---|---|
| `Idempotency` | `Unsafe` | 接続失敗時に自動リトライしてよいか。読み取り専用なら `Safe` |
| `MainThread` | `true` | Editor メインスレッドが必要か。`false` なら Editor が固まっていても応答できる（Unity API を触らないツール限定） |
| `Destructive` | `false` | `true` なら `confirm: true` が無いと実行せず、`dry_run` に対応 |
| `UndoGroup` | `null` | 設定すると呼び出し 1 回が Undo 1 操作にまとまる |
| `Examples` | なし | ツールと一緒に公開する呼び出し例。モデルが引数を決める前に読みます |
| `AlwaysLoad` | `false` | ツール検索を経ずに常に文脈へ載せます。ほぼ毎回のセッションが最初に使うツールにだけ付けてください |
| `MaxResultSizeChars` | サーバー既定 | 大きな応答を切る位置。役に立つ部分が末尾に来るツールでは上げてください |
| `Group` | 名前の接頭辞から | `tools/list` が絞り込みに使うグループ。接頭辞とグループが一致しないときに指定します |

ツール名は `^[a-z][a-z0-9_]{0,63}$` です。説明文は、モデルがそのツールを選ぶ唯一の手がかりになります。何をするかだけでなく、どういうときに使うかを書いてください。

C# を書かずに、JSON ファイルでツールを追加することもできます。[定義ツール](docs/defined-tools.md) を参照してください。

## 実測値

3 つの経路は同じ結果を返します。ベンチマークは時間を測る前にそれを検証し、REST の `result`、MCP の `structuredContent`、CLI の標準出力が一致しなければ、1 回も計測せずに終了します。

| 経路 | 1 呼び出しの p50 | 100 呼び出しあたりの Editor 側ヒープ増加 |
|---|---|---|
| MCP（接続を保つ） | 2.3 ms | 1.3 MB |
| REST（接続を保つ） | 2.2 ms | 1.4 MB |
| CLI（1 呼び出しにつき 1 プロセス） | 27.0 ms | 49 MB |

CLI は 1 回の呼び出しごとにプロセスと TCP 接続を作り直します。ヒープ増加の差は、その接続ごとのバッファであって、ツールの処理ではありません。接続を保つ経路が速いのは当然で、CLI が引き換えに得ているのは、クライアントの設定も常駐プロセスも要らないことです。

CLI の 1 呼び出しは、プロセスの生成から出力までで 24.0 ms でした。そのうち `Main` に入るまでが 15.8 ms です。残る 8.2 ms が、引数の解析、Editor の発見、接続、往復、出力のすべてです。Editor との往復そのものは 3.4 ms でした。`UNITY_MCP_TRACE=1` を付けると、この内訳が出ます。

測定に使ったのは Core i9-14900KF と Windows 11 (10.0.26200) です。.NET は 10.0.100、Unity は 6000.5.10f1 です。経路ごとに 30 回計測し、その前に 3 回のウォームアップが入ります。計測中は Unity のプロセスが 9 個動いていました。再現するには `scripts/bench-cli-vs-mcp.ps1` を実行してください。何を測っているかの定義は [scripts/README.md](scripts/README.md) にあります。

## ドキュメント

- [ツール一覧](docs/tools.md): 88 個のツールの表と注意点
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

- サーバーは `127.0.0.1` にだけバインドします。`OPTIONS` を除く全リクエストに bearer token が必要です。`OPTIONS` は CORS のプリフライトで、本文のない 204 を返すだけです
- descriptor ファイルとトークンファイルは資格情報として扱ってください。これらを読めれば、Editor 内でコードを実行できます
- プレイヤービルドには、Development Build を含めて一切入りません。ソースはすべて `Editor/` 配下にあり、アセンブリ定義が Editor 限定です。CI が毎回検査します

詳細は [セキュリティ](docs/security.md) にあります。

## ライセンス

MIT
