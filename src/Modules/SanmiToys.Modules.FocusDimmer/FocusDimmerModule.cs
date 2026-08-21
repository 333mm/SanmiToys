using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SanmiToys.Core.Interfaces;
using SanmiToys.Core.Services;
using SanmiToys.Modules.FocusDimmer.Core;
using SanmiToys.Modules.FocusDimmer.Models;
using SanmiToys.Modules.FocusDimmer.Views;

namespace SanmiToys.Modules.FocusDimmer;

public class FocusDimmerModule : IToyModule
{
    private readonly SettingsService _settingsService;
    private FocusDimmerSettings _settings = new();
    private readonly List<DimmerOverlay> _overlays = new();
    private DimmerEngine? _engine;

    public string Id => "FocusDimmer";
    public string Name => "FocusDimmer";
    public string Description => LocalizationService.Instance["FocusDimmer_Desc"];
    public string IconGlyph => "\uE706";

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

    public FocusDimmerModule(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _settings = _settingsService.GetModuleSettings<FocusDimmerSettings>(Id);
        _settings.IsEnabled = _settingsService.IsModuleEnabled(Id, false);
    }

    public Task InitializeAsync()
    {
        InitOverlays();
        _engine = new DimmerEngine(_overlays, () => _settings);

        if (_settings.IsEnabled)
        {
            Start();
        }

        return Task.CompletedTask;
    }

    private void InitOverlays()
    {
        foreach (var ov in _overlays) ov.Dispose();
        _overlays.Clear();

        var currentProfiles = new List<MonitorProfile>();

        foreach (var screen in Screen.AllScreens)
        {
            var existingProfile = _settings.Profiles.FirstOrDefault(p => p.DeviceName == screen.DeviceName);
            if (existingProfile == null)
            {
                existingProfile = new MonitorProfile
                {
                    DeviceName = screen.DeviceName,
                    FriendlyName = screen.Primary ? $"メイン ({screen.DeviceName})" : $"サブ ({screen.DeviceName})"
                };
                existingProfile.CopyFrom(_settings.DefaultProfile);
            }
            existingProfile.ScreenRef = screen;
            existingProfile.FriendlyName = screen.Primary ? $"メイン ({screen.DeviceName})" : $"サブ ({screen.DeviceName})";

            currentProfiles.Add(existingProfile);

            var overlay = new DimmerOverlay(existingProfile, () => _settings);
            overlay.Show();
            overlay.SetVisibility(false);
            _overlays.Add(overlay);
        }

        _settings.Profiles = currentProfiles;
    }

    public void Start()
    {
        if (_engine != null)
        {
            _engine.IsEnabled = true;
            _engine.Start();
        }
    }

    public void Stop()
    {
        if (_engine != null)
        {
            _engine.IsEnabled = false;
            _engine.Stop();
        }
    }

    public void SetInspectorMode(bool active)
    {
        if (_engine != null)
        {
            _engine.IsPaused = active;
        }
    }

    public object? CreateSettingsView()
    {
        return new FocusDimmerSettingsView(this, _settingsService, _settings);
    }
}
