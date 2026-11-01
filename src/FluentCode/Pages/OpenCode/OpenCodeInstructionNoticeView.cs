using System;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.OpenCode;

/// <summary>Safe filename projection; file contents are fetched only by an explicit native button/menu action.</summary>
internal sealed class OpenCodeInstructionNoticeView
{
    private readonly Button _button = OpenCodeTurnChipView.CreateButton("OpenCodeInstructionNotice");
    private readonly OpenCodeInstructionFileDialog _viewer = new();
    private OpenCodeInstructionNotice _notice = null!;

    internal OpenCodeInstructionNoticeView(OpenCodeInstructionNotice notice)
    {
        _button.Click += (_, _) =>
        {
            if (_notice.SourceKeys.Length <= 1) _viewer.Open(_button, _notice.SourceKeys.IsDefaultOrEmpty ? "指令正文" : _notice.SourceKeys[0], _notice.Body);
        };
        Update(notice);
    }

    internal FrameworkElement Element => _button;
    internal void Dispose() => _viewer.Dispose();

    internal void Update(OpenCodeInstructionNotice notice)
    {
        _notice = notice;
        var count = notice.SourceKeys.IsDefaultOrEmpty ? 0 : notice.SourceKeys.Length;
        var name = count == 0 ? "来源未提供" : OpenCodeStepRunPresentation.ShortSourceName(notice.SourceKeys[0]);
        var label = "指令 " + name + (count > 1 ? " 等 " + count + " 个 ▾" : string.Empty);
        _button.Content = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        _button.IsEnabled = count > 0 || notice.Body is not null;
        _button.Flyout = null;
        if (count > 1)
        {
            var flyout = new MenuFlyout();
            foreach (var path in notice.SourceKeys)
            {
                var item = new MenuFlyoutItem { Text = OpenCodeStepRunPresentation.ShortSourceName(path) };
                ToolTipService.SetToolTip(item, path);
                // Multi-source system notices contain one combined body, not one file per source key.
                item.Click += (_, _) => _viewer.Open(_button, path, notice.Body);
                flyout.Items.Add(item);
            }
            _button.Flyout = flyout;
        }
        AutomationProperties.SetName(_button, label);
        ToolTipService.SetToolTip(_button, count == 0 ? notice.Summary : string.Join("\n", notice.SourceKeys));
    }
}
