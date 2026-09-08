using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SanmiToys.Core.Interfaces;
using SanmiToys.Core.Services;
using Wpf.Ui.Controls;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Rectangle = System.Windows.Shapes.Rectangle;
using TextBlock = System.Windows.Controls.TextBlock;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace SanmiToys.Host.Views;

public partial class DashboardPage : Page
{
    private record ModuleVisualInfo(
        SymbolRegular Icon,
        Color AccentColor,
        string CategoryJa,
        string CategoryEn
    );

    private static readonly Dictionary<string, ModuleVisualInfo> _moduleVisuals = new()
    {
        ["OmniGlance"] = new(SymbolRegular.Glance24, Color.FromRgb(0x00, 0x78, 0xD4), "ダイナミック アイランド", "Dynamic Island"),
        ["SwiftVolume"] = new(SymbolRegular.Speaker224, Color.FromRgb(0x10, 0x7C, 0x41), "音量ミキサー", "Volume Control"),
        ["SnapTrans"] = new(SymbolRegular.Translate24, Color.FromRgb(0x8B, 0x5C, 0xF6), "画面翻訳・OCR", "OCR & Translation"),
        ["FocusDimmer"] = new(SymbolRegular.Lightbulb24, Color.FromRgb(0xF5, 0x9E, 0x0B), "画面集中・減光", "Screen Focus"),
        ["FluidDrag"] = new(SymbolRegular.CursorHover24, Color.FromRgb(0x06, 0xB6, 0xD4), "ウィンドウ操作", "Window Management"),
    };

    private readonly List<IToyModule> _modules;
    private readonly Action<string> _navigateModuleAction;
    private readonly Dictionary<string, (ToggleSwitch Toggle, Action<bool> UpdateStatusUi)> _moduleEntries = new();
    private bool _isUpdatingUi = false;

    public DashboardPage(List<IToyModule> modules, Action<string> navigateModuleAction)
    {
        InitializeComponent();
        _modules = modules;
        _navigateModuleAction = navigateModuleAction;

        BuildModuleCards();

        Loaded += (s, e) =>
        {
            UpdateAllModuleStates();
        };

        SettingsService.Instance.SettingsChanged += (modId) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!string.IsNullOrEmpty(modId) && _moduleEntries.TryGetValue(modId, out var entry))
                {
                    var mod = _modules.FirstOrDefault(m => m.Id == modId);
                    if (mod != null)
                    {
                        _isUpdatingUi = true;
                        try
                        {
                            entry.Toggle.IsChecked = mod.IsEnabled;
                            entry.UpdateStatusUi(mod.IsEnabled);
                        }
                        finally
                        {
                            _isUpdatingUi = false;
                        }
                        UpdateSummary();
                    }
                }
                else
                {
                    UpdateAllModuleStates();
                }
            });
        };

        LocalizationService.Instance.LanguageChanged += () =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                BuildModuleCards();
                UpdateAllModuleStates();
            });
        };
    }

    public void RefreshState()
    {
        Dispatcher.InvokeAsync(UpdateAllModuleStates);
    }

    private void UpdateAllModuleStates()
    {
        _isUpdatingUi = true;
        try
        {
            foreach (var mod in _modules)
            {
                if (_moduleEntries.TryGetValue(mod.Id, out var entry))
                {
                    entry.Toggle.IsChecked = mod.IsEnabled;
                    entry.UpdateStatusUi(mod.IsEnabled);
                }
            }
        }
        finally
        {
            _isUpdatingUi = false;
        }

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int enabledCount = _modules.Count(m => m.IsEnabled);
        int totalCount = _modules.Count;
        bool isJa = LocalizationService.Instance.EffectiveLanguageCode == "ja";

        ActiveModulesSummaryText.Text = isJa
            ? $"{enabledCount} / {totalCount} 有効"
            : $"{enabledCount} / {totalCount} Active";

        EnableAllBtn.Content = isJa ? "すべて有効" : "Enable All";
        DisableAllBtn.Content = isJa ? "すべて無効" : "Disable All";
    }

    private void BuildModuleCards()
    {
        ModulesPanel.Children.Clear();
        _moduleEntries.Clear();

        bool isJa = LocalizationService.Instance.EffectiveLanguageCode == "ja";

        foreach (var module in _modules)
        {
            var visual = _moduleVisuals.TryGetValue(module.Id, out var v)
                ? v
                : new ModuleVisualInfo(SymbolRegular.AppGeneric24, Color.FromRgb(0x00, 0x78, 0xD4), "ユーティリティ", "Utility");

            var card = new CardControl
            {
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(16, 12, 16, 12)
            };

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 1. アクセントカラー角丸プレート + アイコン
            var iconBrush = new SolidColorBrush(visual.AccentColor);
            iconBrush.Freeze();
            var iconBgBrush = new SolidColorBrush(Color.FromArgb(0x1C, visual.AccentColor.R, visual.AccentColor.G, visual.AccentColor.B));
            iconBgBrush.Freeze();
            var iconBorderBrush = new SolidColorBrush(Color.FromArgb(0x38, visual.AccentColor.R, visual.AccentColor.G, visual.AccentColor.B));
            iconBorderBrush.Freeze();

            var iconPlate = new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(11),
                Background = iconBgBrush,
                BorderBrush = iconBorderBrush,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var icon = new SymbolIcon
            {
                Symbol = visual.Icon,
                FontSize = 22,
                Foreground = iconBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            iconPlate.Child = icon;
            Grid.SetColumn(iconPlate, 0);
            headerGrid.Children.Add(iconPlate);

            // 2. モジュール情報 (タイトル + ステータスバッジ + 説明)
            var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            var titleRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var titleText = new TextBlock
            {
                Text = module.Name,
                FontWeight = FontWeights.SemiBold,
                FontSize = 15,
                Foreground = (Brush)FindResource("TextFillColorPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            titleRow.Children.Add(titleText);

            // ステータスバッジ (有効 / 停止中)
            var statusBadge = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var statusContent = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var statusDot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            statusContent.Children.Add(statusDot);

            var statusText = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            statusContent.Children.Add(statusText);
            statusBadge.Child = statusContent;

            titleRow.Children.Add(statusBadge);
            textPanel.Children.Add(titleRow);

            var descText = new TextBlock
            {
                Text = module.Description,
                Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"),
                FontSize = 12.5,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            textPanel.Children.Add(descText);

            Grid.SetColumn(textPanel, 1);
            headerGrid.Children.Add(textPanel);

            card.Header = headerGrid;

            // 3. アクションパネル (トグルスイッチ + 垂直線 + 設定ボタン)
            var actionPanel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var toggle = new ToggleSwitch
            {
                IsChecked = module.IsEnabled,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var capturedModule = module;
            toggle.Checked += (s, e) =>
            {
                if (!_isUpdatingUi)
                {
                    capturedModule.IsEnabled = true;
                    UpdateModuleCardStatus(statusBadge, statusDot, statusText, true, isJa);
                    UpdateSummary();
                }
            };
            toggle.Unchecked += (s, e) =>
            {
                if (!_isUpdatingUi)
                {
                    capturedModule.IsEnabled = false;
                    UpdateModuleCardStatus(statusBadge, statusDot, statusText, false, isJa);
                    UpdateSummary();
                }
            };
            actionPanel.Children.Add(toggle);

            var sep = new Rectangle
            {
                Width = 1,
                Height = 22,
                Fill = (Brush)FindResource("DividerStrokeColorDefaultBrush"),
                Margin = new Thickness(4, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            actionPanel.Children.Add(sep);

            var settingsBtn = new Wpf.Ui.Controls.Button
            {
                Content = LocalizationService.Instance["Dashboard_SettingsBtn"],
                Icon = new SymbolIcon { Symbol = SymbolRegular.Settings20, FontSize = 14 },
                Appearance = ControlAppearance.Secondary,
                Padding = new Thickness(12, 6, 12, 6),
                VerticalAlignment = VerticalAlignment.Center
            };
            string modId = module.Id;
            settingsBtn.Click += (s, e) => _navigateModuleAction(modId);
            actionPanel.Children.Add(settingsBtn);

            card.Content = actionPanel;

            // 初期ステータスバッジの描画
            UpdateModuleCardStatus(statusBadge, statusDot, statusText, module.IsEnabled, isJa);

            _moduleEntries[module.Id] = (toggle, (enabled) =>
            {
                UpdateModuleCardStatus(statusBadge, statusDot, statusText, enabled, isJa);
            });

            ModulesPanel.Children.Add(card);
        }

        UpdateSummary();
    }

    private static void UpdateModuleCardStatus(Border badge, Ellipse dot, TextBlock text, bool isEnabled, bool isJa)
    {
        if (isEnabled)
        {
            badge.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x10, 0xB9, 0x81));
            badge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0x10, 0xB9, 0x81));
            badge.BorderThickness = new Thickness(1);
            dot.Fill = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
            text.Text = isJa ? "有効" : "Active";
            text.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
        }
        else
        {
            badge.Background = new SolidColorBrush(Color.FromArgb(0x18, 0x8A, 0x8A, 0x8E));
            badge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 0x8A, 0x8A, 0x8E));
            badge.BorderThickness = new Thickness(1);
            dot.Fill = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8E));
            text.Text = isJa ? "停止中" : "Disabled";
            text.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8E));
        }
    }

    private void OnEnableAllClicked(object sender, RoutedEventArgs e)
    {
        _isUpdatingUi = true;
        try
        {
            foreach (var mod in _modules)
            {
                mod.IsEnabled = true;
                if (_moduleEntries.TryGetValue(mod.Id, out var entry))
                {
                    entry.Toggle.IsChecked = true;
                    entry.UpdateStatusUi(true);
                }
            }
        }
        finally
        {
            _isUpdatingUi = false;
        }
        UpdateSummary();
    }

    private void OnDisableAllClicked(object sender, RoutedEventArgs e)
    {
        _isUpdatingUi = true;
        try
        {
            foreach (var mod in _modules)
            {
                mod.IsEnabled = false;
                if (_moduleEntries.TryGetValue(mod.Id, out var entry))
                {
                    entry.Toggle.IsChecked = false;
                    entry.UpdateStatusUi(false);
                }
            }
        }
        finally
        {
            _isUpdatingUi = false;
        }
        UpdateSummary();
    }
}
