using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SanmiToys.Modules.FluidDrag.Core;

public static class CursorHelper
{
    private static readonly HashSet<IntPtr> NonNormalCursors = new();
    private static IntPtr _cachedArrowCursor = IntPtr.Zero;

    static CursorHelper()
    {
        RefreshCursorHandles();
    }

    public static void RefreshCursorHandles()
    {
        _cachedArrowCursor = FluidDragNativeMethods.LoadCursor(IntPtr.Zero, FluidDragNativeMethods.IDC_ARROW);

        NonNormalCursors.Clear();
        int[] specialCursorIds = new[]
        {
            FluidDragNativeMethods.IDC_IBEAM,
            FluidDragNativeMethods.IDC_WAIT,
            FluidDragNativeMethods.IDC_CROSS,
            FluidDragNativeMethods.IDC_UPARROW,
            FluidDragNativeMethods.IDC_SIZENWSE,
            FluidDragNativeMethods.IDC_SIZENESW,
            FluidDragNativeMethods.IDC_SIZEWE,
            FluidDragNativeMethods.IDC_SIZENS,
            FluidDragNativeMethods.IDC_SIZEALL,
            FluidDragNativeMethods.IDC_NO,
            FluidDragNativeMethods.IDC_HAND,
            FluidDragNativeMethods.IDC_APPSTARTING,
            FluidDragNativeMethods.IDC_HELP
        };

        foreach (var id in specialCursorIds)
        {
            IntPtr handle = FluidDragNativeMethods.LoadCursor(IntPtr.Zero, id);
            if (handle != IntPtr.Zero)
            {
                NonNormalCursors.Add(handle);
            }
        }
    }

    public static bool IsNormalCursor()
    {
        var pci = new FluidDragNativeMethods.CURSORINFO
        {
            cbSize = Marshal.SizeOf(typeof(FluidDragNativeMethods.CURSORINFO))
        };

        if (!FluidDragNativeMethods.GetCursorInfo(out pci))
        {
            return false;
        }

        if ((pci.flags & FluidDragNativeMethods.CURSOR_SHOWING) == 0)
        {
            return false;
        }

        IntPtr currentCursor = pci.hCursor;
        if (currentCursor == IntPtr.Zero)
        {
            return false;
        }

        if (_cachedArrowCursor != IntPtr.Zero && currentCursor == _cachedArrowCursor)
        {
            return true;
        }

        if (NonNormalCursors.Contains(currentCursor))
        {
            return false;
        }

        IntPtr latestArrow = FluidDragNativeMethods.LoadCursor(IntPtr.Zero, FluidDragNativeMethods.IDC_ARROW);
        if (latestArrow != IntPtr.Zero && currentCursor == latestArrow)
        {
            _cachedArrowCursor = latestArrow;
            return true;
        }

        return !NonNormalCursors.Contains(currentCursor);
    }
}
