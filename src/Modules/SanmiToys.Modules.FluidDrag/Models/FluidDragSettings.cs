using System;
using System.Collections.Generic;
using System.Linq;

namespace SanmiToys.Modules.FluidDrag.Models;

public enum ModifierKeyMode
{
    None,
    Alt,
    Win,
    Ctrl,
    Shift
}

public class FluidDragSettings
{
    public bool IsEnabled { get; set; } = false;
    public ModifierKeyMode EnableModifierKey { get; set; } = ModifierKeyMode.None;
    public ModifierKeyMode DisableModifierKey { get; set; } = ModifierKeyMode.None;

    public ModifierKeyMode ModifierKey
    {
        get => EnableModifierKey;
        set => EnableModifierKey = value;
    }
    public int DragThresholdPixels { get; set; } = 4;
    public bool ExcludeMaximizedWindows { get; set; } = true;
    public bool DisableWhenFullscreen { get; set; } = true;
    public bool ExcludeGames { get; set; } = true;

    public List<string> ExcludedProcesses { get; set; } = new()
    {
        "explorer",
        "Taskmgr",
        "devenv",
        "Photoshop",
        "Illustrator",
        "Blender",
        "Unity",
        "UnrealEditor",
        "steam",
        "epicgameslauncher",
        "riotclient",
        "valorant",
        "genshinimpact",
        "minecraft",
        "roblox"
    };

    public List<string> ExcludedWindowTitles { get; set; } = new();

    public string ExcludedProcessesCsv
    {
        get => string.Join(", ", ExcludedProcesses);
        set
        {
            if (value != null)
            {
                ExcludedProcesses = value.Split(',')
                    .Select(x => x.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase))
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
    }

    public string ExcludedWindowTitlesCsv
    {
        get => string.Join(", ", ExcludedWindowTitles);
        set
        {
            if (value != null)
            {
                ExcludedWindowTitles = value.Split(',')
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
    }
}
