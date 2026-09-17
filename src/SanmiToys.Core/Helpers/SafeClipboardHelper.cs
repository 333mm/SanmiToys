using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SanmiToys.Core.Services;
using WinRTClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;
using WinRTHistoryStatus = Windows.ApplicationModel.DataTransfer.ClipboardHistoryItemsResultStatus;

namespace SanmiToys.Core.Helpers;

/// <summary>
/// クリップボードの安全な退避・復元および Win+V 履歴消去を提供するヘルパークラス。
/// ヒープ破損を引き起こす低レベルAPI（GlobalAlloc/GlobalLock等）を一切使用せず、
/// WPFマネージドAPIとWinRT公式APIを用いて安全に操作します。
/// </summary>
public static class SafeClipboardHelper
{
    /// <summary>
    /// 現在のクリップボードの内容をマネージドIDataObjectとして安全に退避します。
    /// </summary>
    public static System.Windows.IDataObject? BackupClipboard()
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                return System.Windows.Clipboard.GetDataObject();
            }
            catch (COMException)
            {
                Thread.Sleep(10);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SafeClipboard", $"BackupClipboard error: {ex.Message}");
                break;
            }
        }
        return null;
    }

    /// <summary>
    /// 退避していたクリップボードの内容を安全に復元します。
    /// </summary>
    public static void RestoreClipboard(System.Windows.IDataObject? backup)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (backup != null)
                {
                    System.Windows.Clipboard.SetDataObject(backup, true);
                }
                else
                {
                    System.Windows.Clipboard.Clear();
                }
                return;
            }
            catch (COMException)
            {
                Thread.Sleep(10);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SafeClipboard", $"RestoreClipboard error: {ex.Message}");
                break;
            }
        }
    }

    /// <summary>
    /// クリップボードから文字列テキストを安全に取得します。
    /// </summary>
    public static string? GetClipboardText()
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    return System.Windows.Clipboard.GetText();
                }
                return null;
            }
            catch (COMException)
            {
                Thread.Sleep(10);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SafeClipboard", $"GetClipboardText error: {ex.Message}");
                break;
            }
        }
        return null;
    }

    /// <summary>
    /// Win+V（クリップボード履歴）の最新項目を削除します。
    /// 一時コピーで追加された項目を履歴一覧から消去して履歴の汚染を防ぎます。
    /// </summary>
    public static async Task DeleteLatestHistoryItemAsync()
    {
        try
        {
            if (WinRTClipboard.IsHistoryEnabled())
            {
                var historyResult = await WinRTClipboard.GetHistoryItemsAsync();
                if (historyResult.Status == WinRTHistoryStatus.Success &&
                    historyResult.Items.Count > 0)
                {
                    var latest = historyResult.Items[0];
                    WinRTClipboard.DeleteItemFromHistory(latest);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SafeClipboard", $"Failed to delete history item: {ex.Message}");
        }
    }
}
