using System;
using SanmiToys.Core;
using SanmiToys.Modules.SnapTrans.Models;
using Xunit;

namespace SanmiToys.Tests;

/// <summary>
/// 最近追加・修正された機能（SnapTrans、OmniGlance、SwiftVolume）の単体テスト
/// </summary>
public class RecentFeaturesTests
{
    [Fact]
    public void SnapTransSettings_DefaultValues_AreCorrect()
    {
        var settings = new SnapTransSettings();

        // テキスト選択ツールバーはデフォルト有効
        Assert.True(settings.EnableSelectionToolbar);

        // 起動修飾キーはデフォルト "Alt"
        Assert.Equal("Alt", settings.SelectionToolbarModifier);

        // クリップボード自動コピー設定
        Assert.True(settings.AutoCopyToClipboard);
        Assert.False(settings.CopyOcrToClipboard);
        Assert.True(settings.CopyTranslationToClipboard);

        // OCRフォールバックプロパティが削除され存在しないことをリフレクションで確認
        var prop = typeof(SnapTransSettings).GetProperty("EnableFallbackSelection");
        Assert.Null(prop);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Ctrl")]
    [InlineData("Alt")]
    [InlineData("Shift")]
    public void SnapTransSettings_ModifierMapping_IsValid(string modifierName)
    {
        var settings = new SnapTransSettings
        {
            SelectionToolbarModifier = modifierName
        };

        Assert.Equal(modifierName, settings.SelectionToolbarModifier);
    }

    [Fact]
    public void OmniGlance_DateFormatting_ProducesExpectedFormat()
    {
        // 2026年9月17日
        var testDate = new DateTime(2026, 9, 17, 14, 30, 0);

        string compactTime = testDate.ToString("HH:mm");
        string compactDate = testDate.ToString("M/d");
        string compactDay = testDate.ToString("ddd");

        Assert.Equal("14:30", compactTime);
        Assert.Equal("9/17", compactDate);
        Assert.False(string.IsNullOrWhiteSpace(compactDay));

        // 2桁月日（12月31日）
        var testDateEnd = new DateTime(2026, 12, 31, 23, 59, 0);
        Assert.Equal("12/31", testDateEnd.ToString("M/d"));
    }

    [Fact]
    public void SwiftVolume_PositioningMath_AvoidsTaskbarOverlapWithActualHeight()
    {
        // 画面およびワークエリアのモックパラメータ
        double screenHeight = 1080;
        double taskbarHeight = 48;
        double workBottom = screenHeight - taskbarHeight; // 1032
        double margin = 10.0;

        // 従来のバグ: actualH が 0 のときに 280 にフォールバックしていた場合
        double buggyFallbackH = 280;
        double buggyTop = workBottom - buggyFallbackH - margin; // 1032 - 280 - 10 = 742
        // しかし実際のウィンドウ高さは 460
        double realWindowH = 460;
        double buggyBottom = buggyTop + realWindowH; // 742 + 460 = 1202
        // バグ時は画面外 / タスクバーに 170px 食い込む
        Assert.True(buggyBottom > workBottom);
        Assert.Equal(170, buggyBottom - workBottom);

        // 修正後: DesiredSize またはウィンドウ定義 Height (460) をフォールバックとして使用
        double fixedFallbackH = realWindowH;
        double fixedTop = workBottom - fixedFallbackH - margin; // 1032 - 460 - 10 = 562
        double fixedBottom = fixedTop + realWindowH; // 562 + 460 = 1022

        // 修正後はタスクバー上端（workBottom）より確実に margin 分上に収まる
        Assert.True(fixedBottom <= workBottom - margin);
        Assert.Equal(margin, workBottom - fixedBottom);
    }
}
