using System;
using System.Windows.Data;
using System.Windows.Markup;
using SanmiToys.Core.Services;
using FontFamily = System.Windows.Media.FontFamily;

namespace SanmiToys.Core.Markup;

[MarkupExtensionReturnType(typeof(string))]
public class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public LocExtension() { }

    public LocExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(Key))
            return string.Empty;

        var binding = new System.Windows.Data.Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = System.Windows.Data.BindingMode.OneWay
        };

        return binding.ProvideValue(serviceProvider);
    }
}

[MarkupExtensionReturnType(typeof(FontFamily))]
public class LocFontExtension : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new System.Windows.Data.Binding(nameof(LocalizationService.CurrentFontFamily))
        {
            Source = LocalizationService.Instance,
            Mode = System.Windows.Data.BindingMode.OneWay
        };

        return binding.ProvideValue(serviceProvider);
    }
}
