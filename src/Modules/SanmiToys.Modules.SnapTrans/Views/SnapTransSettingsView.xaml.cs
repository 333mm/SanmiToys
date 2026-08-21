using System.Windows;
using System.Windows.Controls;
using SanmiToys.Core.Services;
using SanmiToys.Modules.SnapTrans.Models;

namespace SanmiToys.Modules.SnapTrans.Views;

public partial class SnapTransSettingsView : System.Windows.Controls.UserControl
{
    private readonly SnapTransModule _module;
    private readonly SettingsService _settingsService;
    private readonly SnapTransSettings _settings;
    private bool _isInitializing = true;

    public SnapTransSettingsView(SnapTransModule module, SettingsService settingsService, SnapTransSettings settings)
    {
        InitializeComponent();
        _module = module;
        _settingsService = settingsService;
        _settings = settings;

        LoadSettingsToUi();
        _isInitializing = false;
    }

    private void LoadSettingsToUi()
    {
        EnableSwitch.IsChecked = _settings.IsEnabled;
        UpdateHotkeyBoxDisplay();

        ProviderCombo.SelectedIndex = (int)_settings.Provider;

        TargetLangCombo.SelectedIndex = _settings.TargetLanguage switch
        {
            "en" => 1,
            "zh-CN" => 2,
            "ko" => 3,
            _ => 0
        };

        DeepLApiKeyBox.Password = _settings.DeepLApiKey;
        GeminiApiKeyBox.Password = _settings.GeminiApiKey;
        OpenAiApiKeyBox.Password = _settings.OpenAiApiKey;

        AutoCopySwitch.IsChecked = _settings.AutoCopyToClipboard;
        CopySourceSwitch.IsChecked = _settings.CopyOcrToClipboard;
        CopyTranslatedSwitch.IsChecked = _settings.CopyTranslationToClipboard;
        AutoCopyOptionsPanel.Visibility = _settings.AutoCopyToClipboard ? Visibility.Visible : Visibility.Collapsed;
        AutoSpeakSwitch.IsChecked = _settings.AutoSpeakResult;
    }

    private void SaveSettings()
    {
        if (_isInitializing) return;
        _settingsService.SetModuleSettings(_module.Id, _settings);
    }

    private void OnEnableChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _module.IsEnabled = EnableSwitch.IsChecked == true;
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.Provider = (TranslationProviderType)ProviderCombo.SelectedIndex;
        SaveSettings();
    }

    private void OnTargetLangChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.TargetLanguage = TargetLangCombo.SelectedIndex switch
        {
            1 => "en",
            2 => "zh-CN",
            3 => "ko",
            _ => "ja"
        };
        SaveSettings();
    }

    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.DeepLApiKey = DeepLApiKeyBox.Password;
        _settings.GeminiApiKey = GeminiApiKeyBox.Password;
        _settings.OpenAiApiKey = OpenAiApiKeyBox.Password;
        SaveSettings();
    }

    private bool _isSyncingCopySwitches = false;

    private void OnAutoCopyChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        bool isAutoCopy = AutoCopySwitch.IsChecked == true;
        _settings.AutoCopyToClipboard = isAutoCopy;
        AutoCopyOptionsPanel.Visibility = isAutoCopy ? Visibility.Visible : Visibility.Collapsed;

        // 親がONになった際に両方OFFの場合はデフォルトで翻訳後テキストをONにする
        if (isAutoCopy && CopySourceSwitch.IsChecked != true && CopyTranslatedSwitch.IsChecked != true)
        {
            _isSyncingCopySwitches = true;
            CopyTranslatedSwitch.IsChecked = true;
            _settings.CopyTranslationToClipboard = true;
            _settings.CopyOcrToClipboard = false;
            _isSyncingCopySwitches = false;
        }

        SaveSettings();
    }

    private void OnCopySourceChecked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isSyncingCopySwitches) return;
        _isSyncingCopySwitches = true;
        CopyTranslatedSwitch.IsChecked = false;
        _settings.CopyOcrToClipboard = true;
        _settings.CopyTranslationToClipboard = false;
        _isSyncingCopySwitches = false;
        SaveSettings();
    }

    private void OnCopySourceUnchecked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isSyncingCopySwitches) return;
        _settings.CopyOcrToClipboard = false;
        SaveSettings();
    }

    private void OnCopyTranslatedChecked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isSyncingCopySwitches) return;
        _isSyncingCopySwitches = true;
        CopySourceSwitch.IsChecked = false;
        _settings.CopyTranslationToClipboard = true;
        _settings.CopyOcrToClipboard = false;
        _isSyncingCopySwitches = false;
        SaveSettings();
    }

    private void OnCopyTranslatedUnchecked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isSyncingCopySwitches) return;
        _settings.CopyTranslationToClipboard = false;
        SaveSettings();
    }

    private void OnAutoSpeakChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.AutoSpeakResult = AutoSpeakSwitch.IsChecked == true;
        SaveSettings();
    }

    private void UpdateHotkeyBoxDisplay()
    {
        if (string.IsNullOrEmpty(_settings.HotkeyKey) || _settings.HotkeyKey == "None")
        {
            HotkeyInputBox.Text = SanmiToys.Core.Services.LocalizationService.Instance["Common_NotSet"];
            return;
        }

        var parts = new System.Collections.Generic.List<string>();
        if (_settings.HotkeyCtrl) parts.Add("Ctrl");
        if (_settings.HotkeyAlt) parts.Add("Alt");
        if (_settings.HotkeyShift) parts.Add("Shift");
        if (_settings.HotkeyWin) parts.Add("Win");
        parts.Add(_settings.HotkeyKey);

        HotkeyInputBox.Text = string.Join(" + ", parts);
    }

    private void OnHotkeyInputBoxGotFocus(object sender, RoutedEventArgs e)
    {
        HotkeyInputBox.Text = SanmiToys.Core.Services.LocalizationService.Instance["Common_PressKeys"];
    }

    private void OnHotkeyInputBoxLostFocus(object sender, RoutedEventArgs e)
    {
        UpdateHotkeyBoxDisplay();
    }

    private void OnHotkeyInputBoxPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;

        if (e.Key == System.Windows.Input.Key.Escape)
        {
            System.Windows.Input.Keyboard.ClearFocus();
            return;
        }

        if (e.Key is System.Windows.Input.Key.Delete or System.Windows.Input.Key.Back)
        {
            _settings.HotkeyKey = "None";
            _settings.HotkeyCtrl = false;
            _settings.HotkeyAlt = false;
            _settings.HotkeyShift = false;
            _settings.HotkeyWin = false;
            SaveSettings();
            _module.UpdateHotkeyRegistration();
            System.Windows.Input.Keyboard.ClearFocus();
            return;
        }

        // 装飾キーの状態取得
        bool ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        bool alt = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Alt) != 0 || e.Key == System.Windows.Input.Key.System;
        bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
        bool win = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Windows) != 0 || 
                   System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LWin) || 
                   System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RWin);

        // 実際のキー（Altキー押下時は SystemKey を取得）
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;

        // 装飾キー単体の場合は途中経過を表示
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or
                   System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt or
                   System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift or
                   System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin or
                   System.Windows.Input.Key.None)
        {
            var tempParts = new System.Collections.Generic.List<string>();
            if (ctrl) tempParts.Add("Ctrl");
            if (alt) tempParts.Add("Alt");
            if (shift) tempParts.Add("Shift");
            if (win) tempParts.Add("Win");
            tempParts.Add("...");
            HotkeyInputBox.Text = string.Join(" + ", tempParts);
            return;
        }

        // メインキーが押されたのでホットキーとして確定
        string keyName = key.ToString();

        // テンキーや記号の分かりやすい表記変換
        if (keyName.StartsWith("D") && keyName.Length == 2 && char.IsDigit(keyName[1]))
        {
            keyName = keyName.Substring(1);
        }
        else if (keyName.StartsWith("NumPad") && keyName.Length == 7 && char.IsDigit(keyName[6]))
        {
            keyName = "Num" + keyName.Substring(6);
        }

        _settings.HotkeyCtrl = ctrl;
        _settings.HotkeyAlt = alt;
        _settings.HotkeyShift = shift;
        _settings.HotkeyWin = win;
        _settings.HotkeyKey = keyName;

        SaveSettings();
        _module.UpdateHotkeyRegistration();
        System.Windows.Input.Keyboard.ClearFocus();
    }

    private void OnClearHotkeyClicked(object sender, RoutedEventArgs e)
    {
        _settings.HotkeyKey = "None";
        _settings.HotkeyCtrl = false;
        _settings.HotkeyAlt = false;
        _settings.HotkeyShift = false;
        _settings.HotkeyWin = false;
        SaveSettings();
        _module.UpdateHotkeyRegistration();
        UpdateHotkeyBoxDisplay();
    }

    private void OnTriggerSnippingClicked(object sender, RoutedEventArgs e)
    {
        _module.TriggerSnipping();
    }
}
