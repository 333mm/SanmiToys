using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;

namespace SanmiToys.Core.Services;

/// <summary>
/// FocusDimmer 等の暗化オーバーレイから除外（常に明るくくり抜き）すべきオーバーレイ領域を管理するレジストリ
/// </summary>
public static class OverlayRegionRegistry
{
    private static readonly Dictionary<string, Func<List<Rectangle>>> _providers = new();
    private static readonly Dictionary<string, IntPtr> _windowHandles = new();
    private static int _revision = 0;

    public static int Revision => _revision;

    public static void NotifyChanged()
    {
        Interlocked.Increment(ref _revision);
    }

    public static void RegisterWindow(string key, IntPtr hwnd)
    {
        lock (_windowHandles)
        {
            _windowHandles[key] = hwnd;
        }
        NotifyChanged();
    }

    public static void UnregisterWindow(string key)
    {
        lock (_windowHandles)
        {
            _windowHandles.Remove(key);
        }
        NotifyChanged();
    }

    public static List<IntPtr> GetOverlayWindowHandles()
    {
        lock (_windowHandles)
        {
            return new List<IntPtr>(_windowHandles.Values);
        }
    }

    public static void Register(string key, Func<List<Rectangle>> provider)
    {
        lock (_providers)
        {
            _providers[key] = provider;
        }
        NotifyChanged();
    }

    public static void Unregister(string key)
    {
        lock (_providers)
        {
            _providers.Remove(key);
        }
        NotifyChanged();
    }

    public static List<Rectangle> GetAlwaysBrightRegions()
    {
        var result = new List<Rectangle>();
        lock (_providers)
        {
            foreach (var provider in _providers.Values)
            {
                try
                {
                    var rects = provider();
                    if (rects != null && rects.Count > 0)
                    {
                        result.AddRange(rects);
                    }
                }
                catch { }
            }
        }
        return result;
    }
}
