# SanmiToys 各モジュール完全機能実装 & 統合再設計 計画書

ユーザーのご指示に基づき、各アプリ（**SwiftVolume**, **FocusDimmer**, **SnapTrans**, **FluidDrag**）の既存の構成・UI・機能を詳細に調査し、機能を省略することなく本来の挙動（独立フライアウト、専用トレイアイコン、プロセス選択、詳細設定等）を完全移植し、SanmiToys のメインウィンドウから統括制御できるように再構築します。

---

## 調査結果と各モジュールの完全移植方針

### 1. SwiftVolume
* **既存の機能構成**:
  * **2つのタスクトレイアイコン**: スピーカー（音量バー・アクティブ状態動的描画）とマイク（ミュート・アクティブ状態動的描画）。中クリックで即時ミュート、右クリックでコンテキストメニュー。
  * **専用フライアウトミキサー (`MainWindow.xaml`)**:
    * アクリル/Mica背景のポップアップウィンドウ（タスクトレイ位置やカーソル位置に自動吸着）。
    * マスター音量スライダー、リアルタイムピークメーター、ミュート切り替え。
    * 再生/録音デバイスの一覧とワンクリック切り替え。
    * アプリごとの個別音量スライダー・ミュート（Audio Sessions）。
    * 複数デバイス展開ビュー（IsExpanded）。
  * **バックグラウンドサービス**:
    * タスクバーホイール音量調節 (`GlobalMouseWheelHookService`)
    * リアルタイム音声メーター (`MeteringService`)
    * セッション監視 (`SessionMonitorService`)
    * グローバルショートカット (`HotkeyService`)
    * 音量HUD (`HudService`)
* **SanmiToys統合方針**:
  * `SwiftVolumeModule` を **有効（ON）** にすると、専用のスピーカー＆メイクトレイアイコンがタスクトレイに出現し、クリックで完全なフライアウトミキサーが開きます。
  * `SwiftVolumeModule` を **無効（OFF）** にすると、トレイアイコンとミキサーは破棄・停止します。
  * SanmiToys メインウィンドウの SwiftVolume 設定ページにて、HUD表示、タスクバーホイール、マイクアイコン表示、ショートカット等の全オプションを一括管理・設定します。

---

### 2. FocusDimmer
* **既存の機能構成**:
  * **マルチモニター対応の調光オーバーレイ (`DimmerOverlay`)**:
    * 減光率（不透明度）、オーバーレイカラー（HEX指定・カラーピッカー）。
    * タスクバー除外、Topmost（最前面）ウィンドウ除外、デスクトップクリック時の動作。
    * 放置時のアイドル減光（指定秒数経過でさらに減光）。
  * **Windhawk Translucent Windows 互換性**:
    * `WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` の即時設定。
    * `DisableBackdropAndBlur` による DWM グラス・ブラーの強制無効化。
  * **除外・常時減光リスト**:
    * プロセス名指定による「常時明るくするアプリ」「常時減光するアプリ」の管理。
* **SanmiToys統合方針**:
  * SanmiToys メインウィンドウの FocusDimmer 設定ページに、不透明度スライダー、カラー選択、アイドル減光、除外設定、プロセス追加UIを完全集約。

---

### 3. SnapTrans
* **既存の機能構成**:
  * **OCR & 翻訳パイプライン**:
    * `Windows.Media.Ocr`（Windows標準高速OCR）
    * 4大翻訳エンジン: Google 翻訳 (Web無料)、DeepL API、Google Gemini API、OpenAI API (GPT-4o-mini)。
  * **スニッピング領域選択 (`SnippingWindow`)**:
    * 全画面半透明オーバーレイからの矩形ドラッグキャプチャ。
  * **結果表示 & アクション (`ResultOverlay`)**:
    * 翻訳結果の角丸カード表示、クリップボード自動コピー、音声読み上げ (TTS: `System.Speech`)。
  * **履歴機能 (`HistoryManager`) & ゲーミングモード (`GamingOverlayWindow`)**:
    * 過去の翻訳ログ閲覧、リアルタイム画面監視翻訳。
* **SanmiToys統合方針**:
  * SanmiToys メインウィンドウの SnapTrans 設定ページで、翻訳プロバイダ、APIキー、ターゲット言語、OCR言語、自動コピー、TTS、ショートカット設定を完全管理。

---

### 4. FluidDrag
* **既存の機能構成**:
  * **低レベルマウスフックによるウィンドウ移動 (`WindowDragEngine`)**:
    * ウィンドウの空き領域（非クライアント・背景）ドラッグによるウィンドウ移動。
    * 修飾キー条件（なし / Alt / Win / Ctrl / Shift）、ドラッグ開始しきい値ピクセル。
  * **除外設定**:
    * 最大化ウィンドウの除外、全画面ウィンドウの除外、特定プロセスの除外。
* **SanmiToys統合方針**:
  * SanmiToys メインウィンドウの FluidDrag 設定ページで、修飾キー・感度・除外プロセスの編集を統合。

---

## 統合アーキテクチャ設計

```
d:/Dev/SanmiToys/
├── SanmiToys.sln
├── src/
│   ├── SanmiToys.Core/                     # 共通基盤 (IToyModule, SettingsService, NativeMethods)
│   ├── SanmiToys.Host/                     # SanmiToys メインウィンドウ (Fluent UI), タスクトレイ (SanmiToys親アイコン), スタートアップ
│   └── Modules/
│       ├── SanmiToys.Modules.FluidDrag/    # フル機能移植
│       ├── SanmiToys.Modules.FocusDimmer/  # フル機能移植 (Windhawk互換完備)
│       ├── SanmiToys.Modules.SnapTrans/    # フル機能移植 (OCR, 多言語, 各種API, TTS, オーバーレイ)
│       └── SanmiToys.Modules.SwiftVolume/  # フル機能移植 (スピーカー/マイクトレイアイコン, フライアウトミキサーUI, オーディオ制御)
```

---

## 実装手順とタスク

1. **`SanmiToys.Modules.SwiftVolume` のフル機能移植**:
   - `SwiftVolume.Core` のオーディオ制御（`AudioManager`, `DeviceEnumerationService`, `MeteringService`, `SessionMonitorService`, `GlobalMouseWheelHookService`, `HudService`, `HotkeyService`）をモジュール内に統合。
   - `H.NotifyIcon.Wpf` によるスピーカー＆マイクトレイアイコン、および専用フライアウトミキサーウィンドウ（`MixerWindow.xaml`）を実装。
   - `SwiftVolumeModule.IsEnabled` が `true` の時のみトレイアイコンとミキサーを生成・常駐させ、`false` で完全停止・破棄。
   - SanmiToys 設定画面（`SwiftVolumeSettingsView`）に全オプション（タスクバーホイール、HUD、マイクアイコン表示、ホットキー等）を装備。

2. **`SanmiToys.Modules.FocusDimmer` のフル機能移植**:
   - マルチモニターオーバーレイ、Windhawk互換（`DisableBackdropAndBlur`）、プロセス除外・常時減光リスト管理UIの強化。

3. **`SanmiToys.Modules.SnapTrans` のフル機能移植**:
   - OCR、4大翻訳プロバイダー、TTS、スニッピング、Fluent結果オーバーレイ、APIキー管理UIの強化。

4. **`SanmiToys.Modules.FluidDrag` のフル機能移植**:
   - 修飾キー・感度・除外プロセスのフル設定UI。

5. **`SanmiToys.Host` のダッシュボード & オプション連携**:
   - 全モジュール初期状態 `OFF`。
   - トグルで各モジュールのトレイアイコンやフックが即座に起動・終了。
   - 各ページから全機能の設定変更・保存。

6. **ビルド検証**:
   - 全プロジェクト警告 0・エラー 0 を確認。
