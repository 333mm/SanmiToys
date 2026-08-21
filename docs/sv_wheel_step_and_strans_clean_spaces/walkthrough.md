# SV音量調整（タスクバー廃止・トレイ1%固定） & STrans不要スペース排除 ウォークスルー

## 修正内容のまとめ

### 1. SV: 音量操作ステップ設定削除 & トレイアイコン1%スクロール標準化
- **タスクバー全体でのスクロール音量調整を完全廃止**:
  - タスクバーボタンやタスクバー上の何もない余白領域でのホイール操作による音量調整を無効化。
  - [`SwiftVolumeSettings.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Models/SwiftVolumeSettings.cs) から `EnableTaskbarScroll` および `VolumeStepPercent` を削除。
  - [`SwiftVolumeSettingsView.xaml`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Views/SwiftVolumeSettingsView.xaml) および [`xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Views/SwiftVolumeSettingsView.xaml.cs) から「タスクバー上でのマウスホイール音量調節」および「音量変化ステップ」設定UIを完全削除。
- **SVタスクトレイアイコン上での 1% スクロールを標準化**:
  - [`GlobalVolumeWheelEngine.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Core/GlobalVolumeWheelEngine.cs) および [`SwiftVolumeTrayManager.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Core/SwiftVolumeTrayManager.cs) により、画面右下のタスクトレイ（通知領域）のスピーカーアイコン上にカーソルがあるときのみ、**1ノッチあたり 1% ずつ（`delta = 1.0f` / `-1.0f`）音量を増減**するように統一。
- **HUD初回表示位置のズレを解消**:
  - [`VolumeHudWindow.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Views/VolumeHudWindow.xaml.cs) の `PositionWindow` において `MainCard.Measure` による決定論的サイズ計測を行い、初回表示と2回目以降の表示位置のズレを完全に根絶。

### 2. STrans: キャプチャ後テキスト内の不要スペース文字の排除
- **単語結合のインテリジェント化**:
  - [`OcrService.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SnapTrans/Services/OcrService.cs) の `FormatOcrResult` において、日本語（ひらがな・カタカナ・漢字・全角記号）の文字間にはスペースを挟まず連結。英単語同士のみ半角スペースを保持。
- **CJKスペースクリーンアップ（`CleanOcrText`）**:
  - 日本語・CJK文字同士の間に混入した半角・全角スペースを自動削除（例: 「無 駄 な ス ペ ー ス」 → 「無駄なスペース」）。
  - 日本語文字と全角約物（「」、。！？（）等）の前後の不要なスペースを削除。
  - 連続する不要な空白（タブ・全角空白等）を正規化。

---

## 検証結果
- `dotnet build SanmiToys.sln -c Release`：**0 警告、0 エラー** でビルド成功を確認。
