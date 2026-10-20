using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using Microsoft.Win32;

namespace AIFrontend.Services.Taskbar;

/// <summary>
/// Private file HICONs only. No finalizer/SafeHandle: native release stays on the creating STA.
/// Keep the applied icon until replacement succeeds, including across a failed theme/reapply attempt.
/// </summary>
internal sealed class TaskbarIconCache
{
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly List<IntPtr> _owned = new();
    private IntPtr _appliedIcon;
    private IconKey? _appliedKey;
    private IconKey? _candidateKey;

    private readonly record struct IconKey(TaskbarStatus Status, bool Light, int Size);

    internal bool HasOwnedIcons => _owned.Count != 0;
    internal string ThemeSource { get; private set; } = "NotRead";

    internal void Invalidate()
    {
        CheckOwner();
        _appliedKey = null; // Invalidate reuse, not the still-applied handle's lifetime.
    }

    internal TaskbarOperationResult? GetIcon(TaskbarStatus status, IntPtr hwnd, out IntPtr icon)
    {
        CheckOwner();
        icon = IntPtr.Zero;
        _candidateKey = null;
        // Clean a previous failed candidate before allocating another; at most two owned icons.
        var cleanup = ReleaseExcept(_appliedIcon);
        if (cleanup is not null) return cleanup;
        string? file = status switch
        {
            TaskbarStatus.NeedsAttention => "taskbar-attention.ico",
            TaskbarStatus.Completed => "taskbar-completed.ico",
            TaskbarStatus.Failed => "taskbar-failed.ico",
            _ => null,
        };
        if (file is null) return null;

        bool light = ReadTaskbarTheme();
        uint dpi = TaskbarNative.GetDpiForWindow(hwnd);
        if (dpi == 0) return new(TaskbarOperationStatus.NativeError, "ReadIconDpi");
        int size = dpi <= 96 ? 16 : dpi <= 120 ? 20 : dpi <= 144 ? 24 : 32;
        _candidateKey = new(status, light, size);
        if (_appliedKey == _candidateKey && _appliedIcon != IntPtr.Zero)
        {
            icon = _appliedIcon;
            return null;
        }

        // No caller-supplied path, URI, SVG conversion or stock-icon fallback.
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Taskbar",
            light ? "light-taskbar" : "dark-taskbar", file);
        icon = TaskbarNative.LoadImage(IntPtr.Zero, path, TaskbarNative.ImageIcon,
            size, size, TaskbarNative.LoadFromFile);
        if (icon == IntPtr.Zero)
            return new(TaskbarOperationStatus.NativeError, "LoadTaskbarAsset",
                Win32Error: Marshal.GetLastWin32Error());
        _owned.Add(icon);
        return null;
    }

    // Call only after SetOverlayIcon succeeds; never retire the previous icon on COM failure.
    internal TaskbarOperationResult? Commit(IntPtr icon)
    {
        CheckOwner();
        _appliedIcon = icon;
        _appliedKey = icon == IntPtr.Zero ? null : _candidateKey;
        return ReleaseExcept(icon);
    }

    internal TaskbarOperationResult? ReleaseAll()
    {
        CheckOwner();
        _appliedIcon = IntPtr.Zero;
        _appliedKey = null;
        _candidateKey = null;
        return ReleaseExcept(IntPtr.Zero);
    }

    private TaskbarOperationResult? ReleaseExcept(IntPtr keep)
    {
        TaskbarOperationResult? failure = null;
        for (int i = _owned.Count - 1; i >= 0; i--)
        {
            if (_owned[i] == keep) continue;
            if (TaskbarNative.DestroyIcon(_owned[i])) _owned.RemoveAt(i);
            else
            {
                int error = Marshal.GetLastWin32Error();
                failure ??= new(TaskbarOperationStatus.NativeError, "DestroyTaskbarAsset", Win32Error: error);
                // Retain failed ownership for an explicit later call/Dispose; never double-free success.
            }
        }
        return failure;
    }

    private bool ReadTaskbarTheme()
    {
        var highContrast = new TaskbarNative.HighContrast
        {
            Size = (uint)Marshal.SizeOf<TaskbarNative.HighContrast>(),
        };
        if (!TaskbarNative.GetHighContrast(TaskbarNative.SpiGetHighContrast,
            highContrast.Size, ref highContrast, 0))
        {
            ThemeSource = "HighContrastQueryFailedFallbackDark";
            return false;
        }
        if ((highContrast.Flags & TaskbarNative.HighContrastOn) != 0)
        {
            // Best-effort contrast-theme background proxy, not app RequestedTheme.
            uint color = TaskbarNative.GetSysColor(TaskbarNative.ColorWindow);
            bool light = 299 * (color & 255) + 587 * ((color >> 8) & 255) +
                114 * ((color >> 16) & 255) >= 128000;
            ThemeSource = light ? "HighContrastWindowLight" : "HighContrastWindowDark";
            return light;
        }

        ThemeSource = "RegistryFallbackDark";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            if (key?.GetValue("SystemUsesLightTheme") is int value && value is 0 or 1)
            {
                ThemeSource = value == 1 ? "SystemLight" : "SystemDark";
                return value == 1;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Best-effort read only; fixed ThemeSource reports the fallback, never exception text.
        }
        return false;
    }

    private void CheckOwner()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId ||
            Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("TaskbarAssetWrongThread");
    }
}
