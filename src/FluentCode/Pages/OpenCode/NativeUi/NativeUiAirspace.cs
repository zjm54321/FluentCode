// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using FluentCode.Services.NativeUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// Hides the terminal's child HWND (its "airspace") while a native popup overlaps it. The XAML side of
/// <see cref="NativeUiLeaseScope"/>: <see cref="Begin"/> captures one lease per terminal it finds,
/// <see cref="Dispose"/> releases exactly those leases once. <see cref="Track"/> wires the same pair for a
/// <see cref="Flyout"/>. Contract: /tmp/opencode/selfdrawn-audit/CONTRACT-N0-airspace.md.
/// </summary>
/// <remarks>
/// <para>UI thread only, and the scope holds no static state or static event, so a flyout that is closed,
/// re-shown or abandoned together with its card cannot leave the terminal hidden.</para>
/// <para>The terminal is normally not an ancestor of the popup, so one call site can find more than one
/// renderer (for example a tab group and a docked pane); all of them are held together and released
/// together.</para>
/// </remarks>
internal sealed class NativeUiAirspace : IDisposable
{
    private readonly NativeUiLeaseScope _leases = new();

    private NativeUiAirspace() { }

    /// <summary>
    /// Captures a suppression lease for every terminal in <paramref name="root"/>'s subtree, including the
    /// root itself. Call this immediately before the popup becomes visible and dispose the result when it is
    /// fully closed.
    /// </summary>
    /// <param name="root">
    /// A node attached to the live visual tree — <c>XamlRoot.Content</c> for a dialog, the flyout's
    /// <c>Target</c> for a flyout. A null, detached or closing root captures nothing and never throws.
    /// </param>
    /// <param name="snapshot">
    /// <see langword="true"/> keeps the terminal's last frame drawn in place while the HWND is hidden, so a
    /// popup opened over it does not blank that band of the window.
    /// </param>
    internal static NativeUiAirspace Begin(DependencyObject? root, bool snapshot = true)
    {
        var scope = new NativeUiAirspace();
        if (root is null) return scope;
        try
        {
            var pending = new Stack<DependencyObject>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                DependencyObject node = pending.Pop();
                // The terminal hosts its own HWND: take its lease and never walk into its subtree.
                if (node is OpenCodePersistentPtyRenderer terminal) { scope._leases.Add(terminal.SuppressAirspace(snapshot)); continue; }
                int count = VisualTreeHelper.GetChildrenCount(node);
                for (int index = 0; index < count; index++) pending.Push(VisualTreeHelper.GetChild(node, index));
            }
        }
        catch (Exception)
        {
            // A detached or closing tree: keep what was captured; the caller still disposes this scope.
        }
        return scope;
    }

    /// <summary>
    /// Pairs capture and release for one flyout: <c>Opening</c> captures, <c>Closed</c> releases. Call once
    /// while the flyout is being built. An <c>Opening</c> that arrives before the previous <c>Closed</c>
    /// releases the previous scope instead of holding two.
    /// </summary>
    internal static void Track(Flyout flyout)
    {
        ArgumentNullException.ThrowIfNull(flyout);
        NativeUiAirspace? scope = null;
        flyout.Opening += (_, _) =>
        {
            scope?.Dispose();
            scope = Begin(flyout.Target?.XamlRoot?.Content ?? flyout.Target, snapshot: true);
        };
        flyout.Closed += (_, _) => { scope?.Dispose(); scope = null; };
    }

    /// <summary>Releases every captured lease exactly once; a second call is a no-op.</summary>
    public void Dispose() => _leases.Release();
}
