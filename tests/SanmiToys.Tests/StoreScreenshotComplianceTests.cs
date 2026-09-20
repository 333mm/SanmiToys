using System;
using System.IO;
using System.Windows.Media.Imaging;
using SanmiToys.Core.Services;
using Xunit;

namespace SanmiToys.Tests;

/// <summary>
/// Microsoft Store ポリシー 10.1.1.3 (Inaccurate Representation) に準拠した
/// ストア掲載用スクリーンショットの規格・アセット整合性を検証するテスト。
/// </summary>
public class StoreScreenshotComplianceTests
{
    private static readonly string[] RequiredScreenshots =
    [
        "Screenshot_01_Overview.png",
        "Screenshot_02_FluidDrag.png",
        "Screenshot_03_FocusDimmer.png",
        "Screenshot_04_SnapTrans.png",
        "Screenshot_05_SwiftVolume.png",
        "Screenshot_06_OmniGlance.png"
    ];

    private static string GetRepositoryRoot()
    {
        string current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "SanmiToys.sln")) ||
                File.Exists(Path.Combine(current, "SanmiToys.slnx")))
            {
                return current;
            }
            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        // デフォルトフォールバック
        return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    [Theory]
    [InlineData("Screenshot_01_Overview.png")]
    [InlineData("Screenshot_02_FluidDrag.png")]
    [InlineData("Screenshot_03_FocusDimmer.png")]
    [InlineData("Screenshot_04_SnapTrans.png")]
    [InlineData("Screenshot_05_SwiftVolume.png")]
    [InlineData("Screenshot_06_OmniGlance.png")]
    public void StoreScreenshots_MustExist_And_MeetStoreResolutionRequirements(string fileName)
    {
        string repoRoot = GetRepositoryRoot();
        string screenshotPath = Path.Combine(repoRoot, "Releases", "StoreListingAssets", "Screenshots", fileName);

        Assert.True(File.Exists(screenshotPath), $"ストア掲載用スクリーンショットが存在しません: {screenshotPath}");

        var fileInfo = new FileInfo(screenshotPath);
        // 空ファイルや極端に小さすぎるファイル（破損）ではないこと、かつストア上限(50MB)未満であること
        Assert.True(fileInfo.Length > 10_000, $"スクリーンショットファイルサイズが小さすぎます: {fileInfo.Length} bytes");
        Assert.True(fileInfo.Length < 50 * 1024 * 1024, $"スクリーンショットファイルサイズがストア上限を超えています: {fileInfo.Length} bytes");

        // 画像のヘッダー解析と解像度・アスペクト比検証
        using var stream = File.OpenRead(screenshotPath);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];

        Assert.NotNull(frame);
        // Microsoft Store 推奨解像度: 1920x1080 (16:9)
        Assert.Equal(1920, frame.PixelWidth);
        Assert.Equal(1080, frame.PixelHeight);
    }

    [Fact]
    public void DocsStoreScreenshots_MustBeSynchronized()
    {
        string repoRoot = GetRepositoryRoot();
        string docsScreenshotsDir = Path.Combine(repoRoot, "docs", "store-screenshots");

        Assert.True(Directory.Exists(docsScreenshotsDir), $"ドキュメント用スクリーンショットディレクトリが存在しません: {docsScreenshotsDir}");

        string[] expectedDocsFiles =
        [
            "01_Dashboard.png",
            "02_FluidDrag.png",
            "03_FocusDimmer.png",
            "04_SnapTrans.png",
            "05_SwiftVolume.png",
            "06_OmniGlance.png"
        ];

        foreach (var docFile in expectedDocsFiles)
        {
            string filePath = Path.Combine(docsScreenshotsDir, docFile);
            Assert.True(File.Exists(filePath), $"docs/store-screenshots 配下のファイルが同期されていません: {filePath}");

            var info = new FileInfo(filePath);
            Assert.True(info.Length > 10_000, $"同期された画像ファイルサイズが無効です: {filePath}");
        }
    }

    [Fact]
    public void Localization_EnglishStringsForStoreModules_MustBeAvailable()
    {
        var loc = LocalizationService.Instance;
        loc.CurrentLanguageCode = "en";

        Assert.Equal("en", loc.EffectiveLanguageCode);
        Assert.False(string.IsNullOrWhiteSpace(loc["Nav_Dashboard"]));
        Assert.False(string.IsNullOrWhiteSpace(loc["Nav_FluidDrag"]));
        Assert.False(string.IsNullOrWhiteSpace(loc["Nav_FocusDimmer"]));
        Assert.False(string.IsNullOrWhiteSpace(loc["Nav_SnapTrans"]));
        Assert.False(string.IsNullOrWhiteSpace(loc["Nav_SwiftVolume"]));
        Assert.False(string.IsNullOrWhiteSpace(loc["Nav_OmniGlance"]));
    }
}
