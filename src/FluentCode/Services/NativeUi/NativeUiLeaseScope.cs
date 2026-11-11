// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;

namespace FluentCode.Services.NativeUi;

/// <summary>
/// Holds a set of leases that must be released together, and releases each of them exactly once.
/// </summary>
/// <remarks>
/// <para>WinUI-free so it is compiled and unit-checked by <c>tests/native-ui/run.ps1</c> directly; the
/// popup side is <c>Pages/OpenCode/NativeUi/NativeUiAirspace.cs</c>.</para>
/// <para>Two failure modes matter for the terminal's airspace: releasing a lease twice (the terminal's
/// hidden-HWND count goes negative and it can stay hidden), and dropping a captured lease (the terminal
/// stays visible under a popup). <see cref="Release"/> is idempotent and covers both. A lease that throws
/// on release never prevents the rest from being released.</para>
/// <para>The empty scope allocates nothing until the first <see cref="Add"/>.</para>
/// </remarks>
internal sealed class NativeUiLeaseScope : IDisposable
{
    private List<IDisposable>? _leases;

    /// <summary>Number of leases captured and not yet released. Zero after <see cref="Release"/>.</summary>
    internal int Count => _leases?.Count ?? 0;

    /// <summary>Captures one lease. Adding to a released scope keeps it for the next release.</summary>
    internal void Add(IDisposable lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        (_leases ??= []).Add(lease);
    }

    /// <summary>
    /// Releases every captured lease once and forgets it. Calling this again does nothing, so a defensive
    /// release before starting the next popup session cannot touch another session's leases.
    /// </summary>
    internal void Release()
    {
        List<IDisposable>? leases = _leases;
        _leases = null;
        if (leases is null) return;
        foreach (IDisposable lease in leases)
        {
            try { lease.Dispose(); }
            catch (Exception) { /* The leased owner is already gone; the lease is spent either way. */ }
        }
    }

    /// <inheritdoc />
    public void Dispose() => Release();
}
