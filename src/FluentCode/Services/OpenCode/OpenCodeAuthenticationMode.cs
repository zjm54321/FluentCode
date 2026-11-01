namespace FluentCode.Services.OpenCode;

/// <summary>显式选择的后端认证方式；不会根据 401 自动尝试其他方式。</summary>
public enum OpenCodeAuthenticationMode
{
    None = 0,
    Bearer = 1,
    Basic = 2,
}
