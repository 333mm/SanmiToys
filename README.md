# SanmiToys

> **Next-Generation Windows Desktop Utility Suite**  
> A modular productivity suite for power users on Windows.

[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![UI Fluent](https://img.shields.io/badge/UI-WPF--UI%20Fluent-0078D4?logo=windows)](https://wpfui.lepo.co/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011-0078D7)]()
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

---

## 🚀 Modules (機能一覧)

### 🖐️ **FluidDrag**
ウィンドウの空いている背景部分をクリック＆ドラッグするだけで、タイトルバーを探さずに直感的にウィンドウを移動。
- フルスクリーンアプリや最大化ウィンドウの自動除外
- プロセス別・タイトル別の柔軟な除外設定

### 💡 **FocusDimmer**
アクティブな作業ウィンドウ以外を美しく減光（調光）し、作業への没入感と集中力を最大化。
- マルチモニター対応（連動 / 個別設定）
- モダンなカラーパレット調光・アイドル自動減光
- ウィンドウインスペクターによるワンクリック除外・常時明るいアプリ登録

### 🔍 **SnapTrans**
画面上のテキストを矩形スニッピングして高速OCR認識＆即時翻訳・音声読み上げ（TTS）。
- Google / DeepL / Gemini / OpenAI 各種翻訳エンジン対応
- 翻訳前後のテキストの自動クリップボードコピー

### 🔊 **SwiftVolume**
タスクバー独立トレイアイコン、モダンなフライアウトミキサー、マウスホイール調音＆HUDオーバーレイ。
- デバイスごとの音量ミキサー操作
- マウスホイールによる音量調節とスタイリッシュなHUD通知

---

## 🌐 Supported Languages (多言語対応)

SanmiToys is fully localized in:
- 🇯🇵 日本語 (Japanese)
- 🇺🇸 English
- 🇨🇳 简体中文 (Simplified Chinese)
- 🇹🇼 繁體中文 (Traditional Chinese)
- 🇰🇷 한국어 (Korean)

---

## ☕ Support the Developer (開発者支援)

SanmiToys がお役に立ちましたら、継続的な開発や新機能追加のサポートをぜひお願いいたします！

- **🇯🇵 日本国内向け**: [OFUSE で支援する (クレジットカード等)](https://ofuse.me/d3a3316d)
- **🌍 Global**: [Buy Me a Coffee ☕](https://buymeacoffee.com/sanmi)

---

## 🛠️ Build & Run

```powershell
# Clone the repository
git clone https://github.com/333mm/SanmiToys.git

# Build solution
dotnet build SanmiToys.sln

# Run Host application
dotnet run --project src/SanmiToys.Host/SanmiToys.Host.csproj
```

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
