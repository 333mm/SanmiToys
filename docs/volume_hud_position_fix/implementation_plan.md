# HUD表示位置のズレ解消 実装計画

音量HUD（`VolumeHudWindow`）を表示する際、初回の表示位置と2回目以降の表示位置で数ピクセル〜十数ピクセルずれてしまう問題を修正します。

---

## 課題と原因分析

- **課題**: HUDの表示位置が初回と2回目以降で少しずれてジャンプする。
- **原因**: 
  - `VolumeHudWindow` は `SizeToContent="WidthAndHeight"` を使用していますが、初回表示の呼び出し時点では WPF のレイアウトレンダリングパスがまだ実行されていないため、`this.ActualWidth` / `this.ActualHeight` が 0 であり、フォールバック値（280px / 60px）で座標（`Left`, `Top`）が計算されていました。
  - 一方、2回目以降は前回のレンダリングで確定した実際のサイズ（例: 296px / 68px 等）が `ActualWidth` / `ActualHeight` に残っているため、初回と異なるサイズで座標計算が行われ、位置のズレが発生していました。

---

## 提案する変更内容

### 1. 決定論的コンテンツサイズ計測の導入
- [`VolumeHudWindow.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Views/VolumeHudWindow.xaml.cs) の `PositionWindow` において、`this.ActualWidth` に依存せず、ルート要素である `MainCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity))` を直接実行。
- `MainCard.DesiredSize` にマージン（`Margin.Left + Margin.Right` 等）を加算した正確なサイズを常に算出して位置座標（`Left`, `Top`）を計算するようにします。
- これにより、**初回・2回目以降を問わず、常に 100% 同一の正確なサイズに基づいて座標が決定**され、位置のズレが完全に解消されます。

### 2. 初期化時のウィンドウハンドル確保
- コンストラクタで `new WindowInteropHelper(this).EnsureHandle()` を呼び出し、ウィンドウハンドルとDPI情報が初回呼び出し前から確実に準備されるようにします。

---

## 変更対象ファイル

- [MODIFY] [`VolumeHudWindow.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Views/VolumeHudWindow.xaml.cs)

---

## 検証計画
- `dotnet build SanmiToys.sln -c Release` を実行し、警告 0・エラー 0 を確認。
