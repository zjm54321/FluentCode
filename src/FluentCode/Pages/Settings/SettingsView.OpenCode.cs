using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.Settings;

public sealed partial class SettingsView
{
    private UIElement BuildOpenCodeSection()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(CreateSubtleText("OpenCode 连接已移至左侧导航底部的“用户”入口 →“连接 OpenCode”。支持 WSL 自动发现、远程用户名/密码和远程配对链接。"));
        panel.Children.Add(CreateSubtleText("只连接一个账号 / 实例。原有连接配置与 Windows DPAPI 凭据保持不变；无需删除配置或重新配对。连接配置不再在此编辑。"));
        return panel;
    }
}
