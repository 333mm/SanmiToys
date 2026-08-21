using System.Threading.Tasks;
using SanmiToys.Core.Interfaces;
using SanmiToys.Core.Services;
using SanmiToys.Modules.FluidDrag.Core;
using SanmiToys.Modules.FluidDrag.Models;
using SanmiToys.Modules.FluidDrag.Views;

namespace SanmiToys.Modules.FluidDrag;

public class FluidDragModule : IToyModule
{
    private readonly SettingsService _settingsService;
    private FluidDragSettings _settings = new();
    private WindowDragEngine? _engine;

    public string Id => "FluidDrag";
    public string Name => "FluidDrag";
    public string Description => LocalizationService.Instance["FluidDrag_Desc"];
    public string IconGlyph => "\uE8B9";

    public bool IsEnabled
    {
        get => _settings.IsEnabled;
        set
        {
            if (_settings.IsEnabled != value)
            {
                _settings.IsEnabled = value;
                _settingsService.SetModuleSettings(Id, _settings);
                _settingsService.SetModuleEnabled(Id, value);
                if (value) Start(); else Stop();
            }
        }
    }

    public FluidDragModule(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _settings = _settingsService.GetModuleSettings<FluidDragSettings>(Id);
        _settings.IsEnabled = _settingsService.IsModuleEnabled(Id, false);
    }

    public Task InitializeAsync()
    {
        _engine = new WindowDragEngine(() => _settings);

        if (_settings.IsEnabled)
        {
            Start();
        }

        return Task.CompletedTask;
    }

    public void Start()
    {
        _engine?.Start();
    }

    public void Stop()
    {
        _engine?.Stop();
    }

    public object? CreateSettingsView()
    {
        return new FluidDragSettingsView(this, _settingsService, _settings);
    }
}
