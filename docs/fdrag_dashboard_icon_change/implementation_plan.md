# FDrag ダッシュボードアイコン変更 実装計画

FluidDrag（FDrag）モジュールのダッシュボードカードおよびサイドナビゲーションメニューのアイコンを、従来の「手（HandRight24）」から「マウスカーソルでドラッグ操作するイメージ（CursorHover24）」に変更します。

---

## 変更内容

### 1. ダッシュボード上のモジュールカードアイコン変更
- [`SanmiToys.Host/Views/DashboardPage.xaml.cs`](file:///d:/Dev/SanmiToys/src/SanmiToys.Host/Views/DashboardPage.xaml.cs) の `BuildModuleCards` における `FluidDrag` のアイコンシンボルを `SymbolRegular.HandRight24` から `SymbolRegular.CursorHover24` に変更。

### 2. ナビゲーションメニューのアイコン変更
- [`SanmiToys.Host/MainWindow.xaml`](file:///d:/Dev/SanmiToys/src/SanmiToys.Host/MainWindow.xaml) の `FluidDrag` 用 `NavigationViewItem` の `Icon` を `{ui:SymbolIcon CursorHover24}` に変更。

---

## 検証計画
- `dotnet build SanmiToys.sln -c Release` を実行し、警告 0・エラー 0 を確認。
