# SwiftVolume (SV) 各種改善の実装計画

SV（SwiftVolume）モジュールにおけるアプリ一覧の読み込み高速化、管理者権限アプリ・システムアプリのアイコン取得、代替アイコンの視認性改善、音量インジケーター感度統一の修正を行います。

## 課題の背景と原因

1. **アプリリストの読み込み・表示が遅い & 管理者権限アプリ（原神など）のアイコンが取得できない問題**:
   - `Process.GetProcessById(pid).MainModule` は、管理者権限で実行中のアプリ（ゲーム等）やシステムプロセスに対して `Win32Exception` (Access is denied) をスローします。
   - 例外が発生すると `exePath` が取得できず、キャッシュも保存されないため、毎回の表示・更新時に重い例外処理が繰り返され、読み込みが著しく遅延していました。
   - また `exePath` が取れないため、`FileVersionInfo` からの表示名や `ExtractAssociatedIcon` によるアプリアイコンの取得に失敗していました。
2. **代替アイコンが見辛い問題**:
   - アイコンがないアプリで表示される `SymbolIcon (AppGeneric24)` に前景色（Foreground）が明示されておらず、ダークテーマ背景上で黒ずんで視認性が悪くなっていました。
3. **デバイス音量インジケーターの反応が大きすぎる問題**:
   - `MeteringService.GetPeakLevel` 内でピーク値が `raw * 3.0f` されており、さらに `MixerWindow` 側で `raw * 1.5f` されていたため、実質 4.5倍（アプリリストの1.5倍に対して3倍過敏）になっていました。

---

## 提案する変更内容

### 1. `SwiftVolumeNativeMethods.cs` の拡張
- `PROCESS_QUERY_LIMITED_INFORMATION` (0x1000) を用いた `OpenProcess`、`QueryFullProcessImageName`、`CloseHandle` の Win32 P/Invoke を定義。
- これにより標準権限のアプリからでも管理者権限アプリ・システムプロセスの完全な exe パスを高速・安全に取得可能にします。

### 2. `DeviceEnumerationService.cs` のプロセス情報取得ロジックの改善
- プロセスIDからの exe パス取得において、まずは `OpenProcess` + `QueryFullProcessImageName` を優先的に使用（例外を発生させず高速）。
- 取得した exe パスから `FileVersionInfo`（日本語名・製品名）および `ExtractAssociatedIcon` によるアイコン取得を実行。
- プロセス情報キャッシュ（`_processMetaCache`）を拡充し、PID やプロセス名でのフォールバックおよびネガティブキャッシュ（失敗時もキャッシュして無駄な再探索を防止）を導入。

### 3. `MixerWindow.xaml.cs` の代替アイコン視認性向上
- アイコン未取得時の `SymbolIcon (AppGeneric24)` に、`Foreground = (Brush)FindResource("TextFillColorSecondaryBrush")` を設定し、背景上での視認性を大幅に改善。
- `ExpandedDevicesPanel`（全デバイス展開パネル）側の代替アイコンも同様に修正。

### 4. `MeteringService.cs` および `MixerWindow.xaml.cs` の音量インジケーター感度統一
- `MeteringService.GetPeakLevel` 内の過剰な 3.0 倍スケーリングを廃止し、生のピーク値（`raw`）を返却。
- `MixerWindow` 側でデバイスマスター（出力・入力）と各アプリセッションのインジケーター感度をすべて統一（`raw * 1.5f`、アタック・リリース係数も同一パラメータに統一）。

---

## 変更対象ファイル

- [MODIFY] [`SwiftVolumeNativeMethods.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Core/SwiftVolumeNativeMethods.cs)
- [MODIFY] [`DeviceEnumerationService.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Core/DeviceEnumerationService.cs)
- [MODIFY] [`MeteringService.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Core/MeteringService.cs)
- [MODIFY] [`MixerWindow.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Views/MixerWindow.xaml.cs)

---

## 検証手順

### ビルド確認
- `dotnet build SanmiToys.sln -c Release` を実行し、警告 0、エラー 0 でビルドできることを確認。
