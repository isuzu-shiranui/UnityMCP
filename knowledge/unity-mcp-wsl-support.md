# Unity MCP WSL対応改造ガイド

## 問題の概要

Unity MCPは現在、WSL環境からの接続に対応していません。主な原因は：

1. **ネットワークの分離**: WSL2とWindowsホストは異なるネットワークセグメントで動作
2. **localhost固定問題**: Unity MCPが`127.0.0.1`でハードコードされており、WSL↔Windows間の通信ができない
3. **双方向の問題**: 
   - WSL（Claude Code）→ Windows（Unity Editor）
   - Windows（TypeScriptサーバー）→ WSL（Claude Code）

## 技術的背景

### WSL2のネットワーク構成
- WSL2は仮想マシンとして動作し、独自のIPアドレスを持つ
- WindowsホストのIPは通常`/etc/resolv.conf`の`nameserver`に記載（例: `172.31.240.1`）
- WSL2からlocalhostへのアクセスはWSL内部のみを参照

### Unity MCPの通信ポート
- TCP: 27182（メイン通信）
- UDP: 27183（Discovery）

## 改造ポイント

### 1. 最小限の改造（推奨）

#### 環境変数対応
最もシンプルで影響範囲が小さい方法：

**McpServer.cs の修正**（コンストラクタ部分）:
```csharp
public McpServer(int port = 0)
{
    var settings = McpSettings.instance;
    
    // 環境変数からホストを取得（WSL対応）
    var envHost = Environment.GetEnvironmentVariable("UNITY_MCP_HOST");
    this.host = !string.IsNullOrEmpty(envHost) ? envHost : settings.host;
    
    this.port = port > 0 ? port : settings.port;
    // 以下略
}
```

**WSL側の設定**（`.bashrc`または`.zshrc`に追加）:
```bash
# Windows側のIPを自動取得して環境変数に設定
export UNITY_MCP_HOST=$(cat /etc/resolv.conf | grep nameserver | awk '{print $2}')
```

### 2. 設定ファイルでの対応

#### McpSettings.cs の拡張
```csharp
/// <summary>
/// カスタムホストを使用するか
/// </summary>
[SerializeField]
public bool useCustomHost = false;

/// <summary>
/// カスタムホストアドレス（WSL環境用）
/// </summary>
[SerializeField]
public string customHost = "";

/// <summary>
/// WSL環境での自動IP検出を使用するか
/// </summary>
[SerializeField]
public bool autoDetectWSLHost = false;
```

#### McpServer.cs の修正
```csharp
private string DetermineHost(McpSettings settings)
{
    // カスタムホスト設定が優先
    if (settings.useCustomHost && !string.IsNullOrEmpty(settings.customHost))
        return settings.customHost;
    
    // WSL自動検出が有効な場合
    if (settings.autoDetectWSLHost && IsRunningInWSL())
        return GetWindowsHostIP();
    
    // デフォルト
    return settings.host;
}

private static bool IsRunningInWSL()
{
    // WSL環境の検出（Linux上でMicrosoft/WSLの文字列を確認）
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && 
        File.Exists("/proc/version"))
    {
        var version = File.ReadAllText("/proc/version");
        return version.Contains("Microsoft") || version.Contains("WSL");
    }
    return false;
}

private static string GetWindowsHostIP()
{
    try
    {
        if (File.Exists("/etc/resolv.conf"))
        {
            var lines = File.ReadAllLines("/etc/resolv.conf");
            foreach (var line in lines)
            {
                if (line.TrimStart().StartsWith("nameserver"))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                        return parts[1];
                }
            }
        }
    }
    catch (Exception ex)
    {
        Debug.LogWarning($"Failed to auto-detect Windows host IP: {ex.Message}");
    }
    
    // フォールバック（WSL2のデフォルト）
    return "172.17.0.1";
}
```

### 3. TypeScriptサーバー側の対応

TypeScriptサーバー（`index.ts`）も修正が必要：

```typescript
// 全インターフェースでリッスン
server.listen(27182, '0.0.0.0', () => {
    console.log('MCP server listening on all interfaces');
});

// UDPブロードキャストも全インターフェース対応
udpSocket.bind(27183, '0.0.0.0');
```

### 4. GUI対応（McpSettingsProvider.cs）

設定画面にWSL関連オプションを追加：

```csharp
private void DrawNetworkSettings()
{
    EditorGUILayout.LabelField("Network Settings", EditorStyles.boldLabel);
    
    settings.host = EditorGUILayout.TextField("Host", settings.host);
    settings.port = EditorGUILayout.IntField("Port", settings.port);
    
    EditorGUILayout.Space();
    EditorGUILayout.LabelField("WSL Support", EditorStyles.boldLabel);
    
    settings.useCustomHost = EditorGUILayout.Toggle("Use Custom Host", settings.useCustomHost);
    if (settings.useCustomHost)
    {
        settings.customHost = EditorGUILayout.TextField("Custom Host", settings.customHost);
    }
    
    settings.autoDetectWSLHost = EditorGUILayout.Toggle("Auto-detect WSL Host", settings.autoDetectWSLHost);
    
    if (IsRunningInWSL())
    {
        EditorGUILayout.HelpBox(
            $"WSL環境を検出しました。Windows側のIP: {GetWindowsHostIP()}", 
            MessageType.Info);
    }
}
```

## 実装手順

1. **Unity MCPをフォーク**
   ```bash
   git clone https://github.com/isuzu-shiranui/UnityMCP.git
   cd UnityMCP
   git checkout -b feature/wsl-support
   ```

2. **最小限の変更を実装**
   - 環境変数対応のみ実装（最も影響が少ない）
   - McpServer.csのコンストラクタを修正

3. **テスト**
   - WSL2環境でClaude Codeを起動
   - Windows側でUnity Editorを起動
   - 環境変数を設定して接続確認

4. **段階的な機能追加**
   - 設定GUI対応
   - 自動検出機能
   - エラーハンドリング強化

## Windows側の設定

### ファイアウォール設定
PowerShell（管理者権限）で実行：
```powershell
# TCP 27182ポートを開放
New-NetFirewallRule -DisplayName "Unity MCP TCP" -Direction Inbound -Protocol TCP -LocalPort 27182 -Action Allow

# UDP 27183ポートを開放
New-NetFirewallRule -DisplayName "Unity MCP UDP" -Direction Inbound -Protocol UDP -LocalPort 27183 -Action Allow
```

### Unity Editor設定
1. Edit > Project Settings > Unity MCP
2. Host を `0.0.0.0` に設定（全インターフェースでリッスン）
3. Auto Start on Launch を有効化

## トラブルシューティング

### 接続できない場合
1. WSL側でWindows側のIPを確認
   ```bash
   cat /etc/resolv.conf | grep nameserver
   ```

2. Windowsファイアウォールの確認
   ```powershell
   Get-NetFirewallRule -DisplayName "*Unity MCP*"
   ```

3. ポートの使用状況確認
   ```bash
   # WSL側
   ss -tuln | grep 2718
   
   # Windows側（PowerShell）
   netstat -an | findstr 2718
   ```

### デバッグ方法
1. Unity EditorでMCP詳細ログを有効化
2. `UNITY_MCP_DEBUG=1`環境変数でデバッグモード
3. Wiresharkでパケットキャプチャ

## まとめ

WSL対応は技術的に可能で、最小限の変更で実現できます。環境変数を使った方法が最もシンプルで、既存のコードへの影響も最小限です。段階的に機能を追加していくことで、より使いやすいWSL対応を実現できます。