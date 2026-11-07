// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentCode.Services.NativeUi;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.OpenCode;

internal sealed class OpenCodeNativeUiPartView : IDisposable
{
    private readonly StackPanel _root = new();
    private readonly TextBlock _status = new();
    private readonly NativeUiCatalog _catalog = NativeUiCatalog.Default;
    private NativeUiRuntime? _runtime;
    private NativeUiRenderer? _renderer;
    private string? _source;
    private string? _title;
    private string? _partId;
    private bool _disposed;
    internal FrameworkElement Element => _root;
    internal NativeUiRuntime? Runtime => _runtime;
    internal Func<NativeUiAssistantRequest, Task<NativeUiHostResult>>? ToAssistant { get; set; }
    internal OpenCodeNativeUiPartView() { NativeUiTheme.Host(_root); _root.Spacing = NativeUiTheme.Gap(2); _root.Tag = this; }
    internal void Update(OpenCodeContentPart part)
    {
        if (_partId != part.Id) { ClearRuntime(); _source = null; _partId = part.Id; }
        Update(part.State);
    }
    internal void Update(System.Text.Json.JsonElement? state)
    {
        if (_disposed) return;
        var input = NativeUiToolInput.Read(state, _catalog.Limits);
        if (input.Streaming) { ClearRuntime(); _source = null; _root.Children.Clear(); _root.Children.Add(NativeUiTheme.Notice("正在生成交互卡片…", "输入完成后显示原生控件")); return; }
        if (input.Error is { } inputError) { Error(inputError); return; }
        if (_runtime is not null && input.Source == _source && input.Title == _title) return; // Tool progress/success must not reset local state.
        ClearRuntime(); _source = input.Source; _title = input.Title;
        var parsed = new NativeUiParser(_catalog).Parse(input.Source ?? "");
        var errors = parsed.Errors.Where(e => e.Severity != "warning").Select(e => $"{e.Line}:{e.Column} [{e.Code}] {e.Message}").ToList();
        if (parsed.Incomplete) errors.Add("程序输入尚未完整");
        if (parsed.Unresolved.Count > 0) errors.Add("未解析的引用：" + string.Join(", ", parsed.Unresolved.Distinct()));
        if (parsed.Root is null && parsed.RootExpression is null && errors.Count == 0) errors.Add("没有可渲染的 root 组件");
        if (errors.Count > 0) { Error(string.Join("\n", errors.Take(3))); return; }
        try
        {
            NativeUiSafety.Check(parsed.StateDeclarations, _catalog.Limits);
            _runtime = new(parsed, _catalog) { ToAssistant = request => ToAssistant?.Invoke(request) ?? Task.FromResult(new NativeUiHostResult(false, "当前输入区不可用")) };
            if (_partId is { } id && NativeUiStateStore.Read(id, input.Source ?? "") is { } snapshot) _runtime.Restore(snapshot);
            _renderer = new(_runtime); _runtime.Changed += Refresh;
            _root.Children.Clear();
            if (!string.IsNullOrWhiteSpace(input.Title))
            {
                var heading = new TextBlock { Text = input.Title }; NativeUiTheme.Text(heading, new("Caption", new Dictionary<string, object?>())); _root.Children.Add(heading);
            }
            _root.Children.Add(_renderer.Element); _root.Children.Add(_status); Refresh();
        }
        catch (NativeUiLimitException error) { Error(error.Message); }
    }
    private void Refresh()
    {
        if (_disposed || _runtime is null || _renderer is null) return;
        try
        {
            SaveState();
            var root = _runtime.EvaluateRoot();
            if (root is null) { Error("root 当前没有得到组件"); return; }
            _renderer.Render(root);
            _status.Text = string.Join(" · ", new[] { _runtime.Unsupported.Count > 0 ? "未支持：" + string.Join(", ", _runtime.Unsupported) : "", _runtime.Status }.Where(text => text.Length > 0));
            _status.Visibility = _status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            NativeUiTheme.Text(_status, new("Caption", new Dictionary<string, object?>()));
        }
        catch (NativeUiLimitException error) { Error(error.Message); }
    }
    private void Error(string message)
    { ClearRuntime(); _root.Children.Clear(); _root.Children.Add(NativeUiTheme.Notice("交互卡片无法渲染", NativeUiToolInput.Compact(message), error: true)); }
    private void ClearRuntime()
    {
        SaveState();
        if (_runtime is not null) _runtime.Changed -= Refresh;
        _renderer?.Dispose(); _runtime?.Dispose(); _renderer = null; _runtime = null;
    }
    private void SaveState() { if (_runtime is not null && _partId is { Length: > 0 } id && _source is { } source) NativeUiStateStore.Save(id, source, _runtime.Snapshot()); }
    public void Dispose() { _disposed = true; ToAssistant = null; ClearRuntime(); _root.Children.Clear(); _root.Tag = null; }
}
