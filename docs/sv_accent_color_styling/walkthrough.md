# SV アクセントカラー明度・彩度最適化ウォークスルー

ダークテーマ（黒背景）上でアクセントカラーが濃く沈んで見えていた問題に対し、Windows 11 公式のダークモード用カラーパレット（`AccentLight2` / `AccentLight1`）の優先採用と、HSV色空間による明度ブースト・彩度最適化を実装しました。

---

## 修正内容のまとめ

### 1. Windows 11 公式ダークテーマ用アクセントカラー（AccentLight2）の優先取得
- **対応**: [`App.xaml.cs`](file:///d:/Dev/SanmiToys/src/SanmiToys.Host/App.xaml.cs) にて、WinRT `UISettings` から Windows 11 がダークモード UI（設定画面や音量スライダー等）で使用する公式の明るいアクセントカラー（`UIColorType.AccentLight2` および `AccentLight1`）を最優先で取得するようにしました。

### 2. HSV色空間による明度ブーストと彩度調整（AdjustColorForDarkTheme）
- **対応**: 取得したアクセントカラーに対し、明度（Value）を最大 1.3 倍（下限 0.82）まで引き上げ、彩度（Saturation）を適度に抑えて上品で抜け感のある発色に自動補正するアルゴリズムを導入。黒背景 `#141414` 上でも濃く沈まず、パッと明るく鮮明に視認できるようになりました。

---

## 変更ファイル

- [`SanmiToys.Host/App.xaml.cs`](file:///d:/Dev/SanmiToys/src/SanmiToys.Host/App.xaml.cs)

---

## 検証結果
- `dotnet build SanmiToys.sln -c Release`：**0 警告、0 エラー** でビルド成功を確認。
