# SV音量調整（タスクバー廃止・トレイ1%固定）& STrans不要スペース排除 実装計画

ユーザー様のご要望に基づき、タスクバー上での音量調節を完全に廃止し、SVタスクトレイアイコン上でのマウススクロール時のみ 1% ずつ音量を調整できるように標準化します。また、HUDの初回表示位置ズレの解消と、STransにおけるキャプチャ後テキストの不要なスペース排除を合わせて実施します。

---

## 提案する変更内容

### 1. SV: タスクバー上音量調整の廃止 & トレイアイコン1%スクロールの標準化
- **タスクバー音量調整機能の廃止**:
  - [`GlobalVolumeWheelEngine.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Core/GlobalVolumeWheelEngine.cs) のタスクバー低レベルフックを停止・無効化（タスクバー上でホイールを回しても音量は変化しません）。
  - [`SwiftVolumeSettings.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Models/SwiftVolumeSettings.cs) から `EnableTaskbarScroll` および `VolumeStepPercent` を削除。
  - [`SwiftVolumeSettingsView.xaml`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Views/SwiftVolumeSettingsView.xaml) および [`xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Views/SwiftVolumeSettingsView.xaml.cs) から「タスクバー上でのマウスホイール音量調節」および「音量変化ステップ」の設定UI・コードを削除。
- **SVタスクトレイアイコン上での 1% スクロール標準化**:
  - [`SwiftVolumeTrayManager.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Core/SwiftVolumeTrayManager.cs) の `_speakerIcon`（TaskbarIcon）に `TrayMouseWheel` イベントハンドラを実装。
  - スピーカーアイコンの上にマウスカーソルを置いてスクロールしたときのみ、**1% ずつ（`delta = 1.0f` / `-1.0f`）音量を増減**させ、音量HUDを表示するようにします。
- **HUD初回位置ズレ解消**:
  - [`VolumeHudWindow.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Views/VolumeHudWindow.xaml.cs) の `PositionWindow` において `MainCard.Measure` による決定論的サイズ計測を導入し、初回と2回目以降の位置ズレを完全に解消。

### 2. STrans: キャプチャ後テキストの無駄なスペース文字の排除
- **単語結合の最適化**:
  - [`OcrService.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SnapTrans/Services/OcrService.cs) の `FormatOcrResult` において、日本語（ひらがな・カタカナ・漢字・全角記号）の文字間にはスペースを挟まず連結。英単語同士のみ半角スペースを保持。
- **テキスト正規化処理（`CleanOcrText`）**:
  - 日本語・CJK文字同士の間に混入した半角・全角スペースを自動削除（例: 「無 駄 な ス ペ ー ス」 → 「無駄なスペース」）。
  - 日本語文字と全角約物（「」、。！？（）等）の前後の不要なスペースを削除。
  - 連続するスペースや行頭・行末の不要な空白をクリーンアップ。

---

## 変更対象ファイル

- [MODIFY] [`SwiftVolumeSettings.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Models/SwiftVolumeSettings.cs)
- [MODIFY] [`SwiftVolumeSettingsView.xaml`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Views/SwiftVolumeSettingsView.xaml)
- [MODIFY] [`SwiftVolumeSettingsView.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Views/SwiftVolumeSettingsView.xaml.cs)
- [MODIFY] [`SwiftVolumeModule.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/SwiftVolumeModule.cs)
- [MODIFY] [`SwiftVolumeTrayManager.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Core/SwiftVolumeTrayManager.cs)
- [MODIFY] [`GlobalVolumeWheelEngine.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Core/GlobalVolumeWheelEngine.cs)
- [MODIFY] [`VolumeHudWindow.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Views/VolumeHudWindow.xaml.cs)
- [MODIFY] [`OcrService.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SnapTrans/Services/OcrService.cs)

---

## 検証計画
- `dotnet build SanmiToys.sln -c Release` を実行し、警告 0・エラー 0 を確認。
