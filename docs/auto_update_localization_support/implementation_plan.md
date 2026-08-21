# Velopack インストーラー＆完全自動アップデート導入計画

## 概要
Velopack を導入し、**ワンクリックでインストール可能なインストーラー (`SanmiToys-Setup.exe`) の生成** と、**GitHub Releases と連携したアプリ内完全自動アップデート（バックグラウンドダウンロード・自動適用・再起動）** を実現します。

---

## ユーザー確認・検討事項
> [!NOTE]
> - インストーラー作成用の .NET グローバルツール `vpk` を使用して `SanmiToys-Setup.exe` を生成します。
> - アプリ内の「更新を確認」ボタンを押した際、最新版があれば **自動でダウンロードし、「再起動して更新」ボタンが表示される** ように連携します。

---

## 提案する変更

### 1. プロジェクト設定・パッケージ追加
#### [MODIFY] [SanmiToys.Host.csproj](file:///d:/Dev/SanmiToys/src/SanmiToys.Host/SanmiToys.Host.csproj)
- NuGet パッケージ `Velopack` を追加

### 2. アプリケーション起動エントリポイント
#### [MODIFY] [App.xaml.cs](file:///d:/Dev/SanmiToys/src/SanmiToys.Host/App.xaml.cs)
- `VelopackApp.Build().Run();` を追加（インストール・ショートカット生成・アップデート適用時のフックを処理）

### 3. アップデートマネージャーの統合
#### [MODIFY] [UpdateService.cs](file:///d:/Dev/SanmiToys/src/SanmiToys.Host/Services/UpdateService.cs)
- Velopack の `UpdateManager`（GitHub Releases ソース: `https://github.com/333mm/SanmiToys`）を使用した自動更新チェック・ダウンロード・適用処理を実装

### 4. インストーラービルドスクリプトの作成
#### [NEW] [build-installer.ps1](file:///d:/Dev/SanmiToys/build-installer.ps1)
- ワンコマンドで `dotnet publish` と `vpk pack` を実行し、`Releases/SanmiToys-Setup.exe` を生成する PowerShell スクリプトを作成

---

## 検証手順
1. `dotnet build SanmiToys.sln` でビルド確認（エラー・警告ゼロ）
2. `build-installer.ps1` を実行して `Releases/SanmiToys-Setup.exe` が正常に生成されることを確認
