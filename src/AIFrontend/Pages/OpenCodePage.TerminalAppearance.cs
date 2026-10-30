using System;
using AIFrontend.Models;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Pages.Settings;
using AIFrontend.Services.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsTerminalControl.WinUI;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private void InitializeTerminalAppearance()
    {
        Loaded += (_, _) => { _homeSettings.Changed -= TerminalAppearanceChanged; _homeSettings.Changed += TerminalAppearanceChanged; ApplyTerminalAppearance(); };
        Unloaded += (_, _) => { if (!IsLoaded) _homeSettings.Changed -= TerminalAppearanceChanged; };
    }

    private void TerminalAppearanceChanged(object? sender, AppSettingsChangedEventArgs args)
        => DispatcherQueue.TryEnqueue(ApplyTerminalAppearance);

    private void ApplyTerminalAppearance()
    {
        var settings = _homeSettings.Current?.TerminalAppearance ?? new();
        var appearance = new TerminalAppearance(settings.FontFamily, (short)settings.FontSize, settings.ColorScheme, settings.CursorShape, settings.CursorBlink);
        _persistentAttachmentOwner?.View.Renderer.ApplyAppearance(appearance);
        _terminalView?.Renderer.ApplyAppearance(appearance);
    }

    private void BindTerminalAppearance(OpenCodePersistentPtyView view)
    {
        view.NewTerminalRequested += () =>
        { if (_persistentInventoryOwner is { } owner) _ = CreatePersistentTerminalAsync(owner.Context); };
        ApplyTerminalAppearance();
        view.AppearanceRequested += async () =>
        {
            if (!view.IsLoaded) return;
            using var airspace = view.Renderer.SuppressAirspace();
            var editor = new TerminalAppearanceEditor();
            editor.Apply(_homeSettings.Current?.TerminalAppearance ?? new());
            var error = new InfoBar { IsOpen = false, Severity = InfoBarSeverity.Error, IsClosable = false };
            editor.Changed += async appearance =>
            {
                try { await _homeSettings.UpdateAsync(current => current with { TerminalAppearance = appearance }); error.IsOpen = false; }
                catch (Exception) { error.Message = "保存失败"; error.IsOpen = true; }
            };
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "终端外观", CloseButtonText = "关闭",
                Content = new StackPanel { Spacing = 12, Children = { editor, error } } };
            RoutedEventHandler unloaded = (_, _) => dialog.Hide();
            view.Unloaded += unloaded;
            try { await dialog.ShowAsync(); }
            finally { view.Unloaded -= unloaded; }
        };
    }
}
