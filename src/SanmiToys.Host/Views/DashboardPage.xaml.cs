using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using SanmiToys.Core.Interfaces;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SanmiToys.Host.Views;

public partial class DashboardPage : Page
{
    private readonly List<IToyModule> _modules;
    private readonly Action<string> _navigateModuleAction;
    private readonly Dictionary<string, (ToggleSwitch Toggle, Action UpdateState)> _moduleToggles = new();
    private bool _isUpdatingUi = false;

    public DashboardPage(List<IToyModule> modules, Action<string> navigateModuleAction)
    {
        InitializeComponent();
        _modules = modules;
        _navigateModuleAction = navigateModuleAction;
        
        BuildModuleCards();

        this.Loaded += (s, e) =>
        {
            UpdateAllModuleStates();
        };

        SanmiToys.Core.Services.SettingsService.Instance.SettingsChanged += (modId) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!string.IsNullOrEmpty(modId) && _moduleToggles.TryGetValue(modId, out var entry))
                {
                    entry.UpdateState();
                }
                else
                {
                    UpdateAllModuleStates();
                }
            });
        };

        SanmiToys.Core.Services.LocalizationService.Instance.LanguageChanged += () =>
        {
            Dispatcher.InvokeAsync(BuildModuleCards);
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
            foreach (var entry in _moduleToggles.Values)
            {
                entry.UpdateState();
            }
        }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    private void BuildModuleCards()
    {
        ModulesPanel.Children.Clear();
        _moduleToggles.Clear();

        foreach (var module in _modules)
        {
            var card = new CardControl
            {
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(16, 14, 16, 14)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconSymbol = module.Id switch
            {
                "FluidDrag" => SymbolRegular.CursorHover24,
                "FocusDimmer" => SymbolRegular.Lightbulb24,
                "SnapTrans" => SymbolRegular.Translate24,
                "SwiftVolume" => SymbolRegular.Speaker224,
                "OmniGlance" => SymbolRegular.Glance24,
                _ => SymbolRegular.AppGeneric24
            };

            var icon = new SymbolIcon
            {
                Symbol = iconSymbol,
                FontSize = 24,
                Foreground = (System.Windows.Media.Brush)FindResource("AccentTextFillColorPrimaryBrush"),
                Margin = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var titleText = new TextBlock
            {
                Text = module.Name,
                FontWeight = FontWeights.SemiBold,
                FontSize = 15
            };
            textPanel.Children.Add(titleText);

            var descText = new TextBlock
            {
                Text = module.Description,
                Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush"),
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            textPanel.Children.Add(descText);

            Grid.SetColumn(textPanel, 1);
            grid.Children.Add(textPanel);

            card.Header = grid;

            var actionPanel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var toggle = new ToggleSwitch
            {
                IsChecked = module.IsEnabled,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var capturedModule = module;
            toggle.Checked += (s, e) =>
            {
                if (!_isUpdatingUi)
                {
                    capturedModule.IsEnabled = true;
                }
            };
            toggle.Unchecked += (s, e) =>
            {
                if (!_isUpdatingUi)
                {
                    capturedModule.IsEnabled = false;
                }
            };
            actionPanel.Children.Add(toggle);

            var settingsBtn = new Wpf.Ui.Controls.Button
            {
                Content = SanmiToys.Core.Services.LocalizationService.Instance["Dashboard_SettingsBtn"],
                Icon = new SymbolIcon { Symbol = SymbolRegular.Settings20, FontSize = 14 },
                Appearance = ControlAppearance.Secondary,
                VerticalAlignment = VerticalAlignment.Center
            };
            string modId = module.Id;
            settingsBtn.Click += (s, e) => _navigateModuleAction(modId);
            actionPanel.Children.Add(settingsBtn);

            card.Content = actionPanel;

            _moduleToggles[module.Id] = (toggle, () =>
            {
                _isUpdatingUi = true;
                try
                {
                    toggle.IsChecked = capturedModule.IsEnabled;
                }
                finally
                {
                    _isUpdatingUi = false;
                }
            });

            ModulesPanel.Children.Add(card);
        }
    }
}
