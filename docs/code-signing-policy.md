# コード署名ポリシー

Windows 版のコマンドラインツール `isuzu-unity-cli-win-x64.exe` には、Authenticode 署名を付けて配布します。[README に戻る](../README.md)

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## 署名する対象

署名するのは、このリポジトリのソースから GitHub Actions がビルドした `isuzu-unity-cli` の Windows 版だけです。ビルドは GitHub がホストするランナーで、リリースのタグが指すコミットから行います。手元でビルドしたファイルや、他のプロジェクトのファイルには署名しません。

Claude Desktop 用の `isuzu-unity-cli.mcpb` に入っている Windows 版も、署名済みの同じファイルです。macOS 版と Linux 版、Unity パッケージには署名しません。

署名を始める前のリリースに含まれる Windows 版は、署名されていません。

## 担当者

| 役割 | 担当 |
|---|---|
| コミッター（レビューなしでソースを変更できる人） | [isuzu-shiranui](https://github.com/isuzu-shiranui) |
| レビュアー（外部からのプルリクエストをすべてレビューする人） | [isuzu-shiranui](https://github.com/isuzu-shiranui) |
| 承認者（署名の要求を 1 件ずつ承認する人） | [isuzu-shiranui](https://github.com/isuzu-shiranui) |

すべての担当者は、GitHub と SignPath の両方で多要素認証を使います。リリースのたびに、承認者が SignPath 上で署名の要求を手動で承認します。

## プライバシー

このプログラムは、利用者またはプログラムを導入・操作する人が要求した場合を除き、ネットワーク上の他のシステムに情報を送りません。

通信する相手と、通信する場面は次のとおりです。

- Unity Editor: `127.0.0.1` 上の Editor にだけ接続します。外部のネットワークには出ません。
- GitHub: `isuzu-unity-cli doctor` と `isuzu-unity-cli update` は、新しいリリースがあるかを `api.github.com` に問い合わせます。`update` と `upgrade` は、新しい版を GitHub からダウンロードします。どちらも、利用者がそのコマンドを実行したときだけ行います。

GitHub への通信には、GitHub の[プライバシーステートメント](https://docs.github.com/ja/site-policy/privacy-policies/github-general-privacy-statement)が適用されます。
