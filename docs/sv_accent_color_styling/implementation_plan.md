# SV スライダーバー背景および音量インジケーターのアクセントカラー適用

SV（SwiftVolume）モジュールにおいて、スライダーバーの背景トラックおよび音量インジケーター（ピークメーター）のカラーを Windows のアクセントカラーから取得し、スライダーバー側がより濃く・くっきりと表示されるようにスタイリングを調整します。

---

## 課題と原因

- **課題**: Windows の個人用設定でアクセントカラーを変更しても、アプリ側でシステムアクセントカラーが自動取得・適用されておらず、WPF-UI のデフォルト青色のままになっていました。
- **原因**: `App.xaml.cs` の起動時に `ApplicationThemeManager.ApplySystemTheme()` のみが呼ばれており、Windows のアクセントカラーを取得して適用する `ApplicationAccentColorManager` の呼び出しおよび監視が行われていませんでした。

---

## 提案する変更内容

### 1. Windows システムアクセントカラーの取得とリアルタイム監視
- `App.xaml.cs` にて、WinRT の `Windows.UI.ViewManagement.UISettings` を利用し、Windows OS 設定で指定された本物のアクセントカラー（`UIColorType.Accent`）を直接取得して `ApplicationAccentColorManager.Apply(color)` でアプリケーション全体のリソース（`AccentFillColorDefaultBrush` 等）に適用。
- `UISettings.ColorValuesChanged` イベントを購読し、ユーザーが Windows の設定画面でアクセントカラーを変更した際にもリアルタイムで自動追従・反映されるように実装。

### 2. スライダーバートラック背景（未選択レール）のアクセントカラー適用
- `MixerWindow.xaml` の `ToggleSliderStyle` 内にある未選択トラック背景（`TrackBackground`）を、従来の白半透明（`#20FFFFFF`）から Windows アクセントカラー（`{DynamicResource AccentFillColorDefaultBrush}`）の半透明（`Opacity="0.25"`）に変更。
- スライダーバーの選択済みトラック（音量レベル）は、不透明な濃いアクセントカラー（`Opacity="1.0"`）とし、明瞭なコントラストを確保。

### 3. 音量インジケーター（ピークメーター）のアクセントカラーと濃度調整
- マスター出力、マイク入力、および各アプリセッション・子セッションのメーターバー（`meterBar` / `childMeterBar`）において、Windows アクセントカラー（`AccentFillColorDefaultBrush`）を適用しつつ、スライダーバー（100% 濃度）に対して背後でふんわりと光る適切な透明度（`Opacity="0.45"`）を設定。

---

## 変更対象ファイル

- [MODIFY] [`SanmiToys.Host/App.xaml.cs`](file:///d:/Dev/SanmiToys/src/SanmiToys.Host/App.xaml.cs)
- [MODIFY] [`MixerWindow.xaml`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Views/MixerWindow.xaml)
- [MODIFY] [`MixerWindow.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Views/MixerWindow.xaml.cs)

---

## 検証計画

- `dotnet build SanmiToys.sln -c Release` を実行し、警告 0、エラー 0 でビルドできることを確認。
