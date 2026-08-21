namespace SanmiToys.Core.Interfaces;

public interface IToyModule
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    string IconGlyph { get; }
    bool IsEnabled { get; set; }

    Task InitializeAsync();
    void Start();
    void Stop();

    object? CreateSettingsView();
}
