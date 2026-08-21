# FDrag ダッシュボードアイコン変更 ウォークスルー

FluidDrag（FDrag）のアイコンを、手のひら（`HandRight24`）からマウスカーソルでドラッグ・ホバーするイメージ（`CursorHover24`）に変更しました。

---

## 修正内容のまとめ

### 1. ダッシュボード上のモジュールカード
- [`SanmiToys.Host/Views/DashboardPage.xaml.cs`](file:///d:/Dev/SanmiToys/src/SanmiToys.Host/Views/DashboardPage.xaml.cs) のアイコンマッピングにおいて、`FluidDrag` を `SymbolRegular.CursorHover24` に更新。

### 2. メインウィンドウのナビゲーションメニュー
- [`SanmiToys.Host/MainWindow.xaml`](file:///d:/Dev/SanmiToys/src/SanmiToys.Host/MainWindow.xaml) の `FluidDrag` 用ナビゲーションアイテムのアイコンを `{ui:SymbolIcon CursorHover24}` に更新。

---

## 検証結果
- `dotnet build SanmiToys.sln -c Release`：**0 警告、0 エラー** でビルド成功を確認。
