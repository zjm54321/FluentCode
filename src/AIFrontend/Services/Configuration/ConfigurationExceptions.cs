using System;
using System.Collections.Generic;

namespace AIFrontend.Services.Configuration;

/// <summary>
/// 设置文件存在但内容无法解析或校验失败（损坏/版本不支持）。
/// 抛出时原文件保持不变，不会被静默覆盖。
/// </summary>
public sealed class SettingsCorruptedException : Exception
{
    /// <summary>创建异常。</summary>
    /// <param name="message">说明（含文件路径与具体问题；不含任何密钥）。</param>
    /// <param name="filePath">相关文件路径（可空）。</param>
    /// <param name="innerException">底层解析异常（可空）。</param>
    public SettingsCorruptedException(string message, string? filePath = null, Exception? innerException = null)
        : base(message, innerException)
    {
        FilePath = filePath;
    }

    /// <summary>相关文件路径（可空）。</summary>
    public string? FilePath { get; }
}

/// <summary>
/// 保存前的设置校验失败（<see cref="SettingsService.SaveAsync"/>）；不会写入任何文件。
/// 加载时的校验失败以 <see cref="SettingsCorruptedException"/> 抛出。
/// </summary>
public sealed class SettingsValidationException : Exception
{
    /// <summary>创建异常。</summary>
    /// <param name="message">说明（汇总全部问题）。</param>
    /// <param name="errors">全部校验问题（可空，默认空列表）。</param>
    /// <param name="innerException">底层异常（可空）。</param>
    public SettingsValidationException(string message, IReadOnlyList<string>? errors = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Errors = errors ?? Array.Empty<string>();
    }

    /// <summary>全部校验问题（可能为空列表）。</summary>
    public IReadOnlyList<string> Errors { get; }
}

/// <summary>
/// 设置已经成功写入并更新 <see cref="SettingsService.Current"/>，但至少一个 <see cref="SettingsService.Changed"/> 订阅者抛出异常。
/// 调用方不得把它当作保存失败回滚；其余订阅者仍已收到通知。
/// </summary>
public sealed class SettingsChangeNotificationException : AggregateException
{
    /// <summary>创建异常。</summary>
    public SettingsChangeNotificationException(IEnumerable<Exception> failures)
        : base("设置已保存，但部分订阅者处理变更通知失败。", failures)
    {
    }
}

/// <summary>
/// 凭据密文存取失败（DPAPI 加密/解密错误等）。密文原文件保留。
/// </summary>
public sealed class SecretStoreException : Exception
{
    /// <summary>
    /// 创建可安全呈现的存储诊断；不保留底层异常消息、路径或 InnerException。
    /// 旧构造函数保留兼容性；UI 应仅展示本异常 Message 或以下结构化字段。
    /// </summary>
    internal SecretStoreException(string operation, string stage, string errorType, int errorHResult)
        : base($"凭据存储失败（operation={operation}; stage={stage}; type={errorType}; HRESULT=0x{errorHResult:X8}）。")
    {
        Operation = operation;
        Stage = stage;
        ErrorType = errorType;
        ErrorHResult = errorHResult;
        HResult = errorHResult;
    }

    /// <summary>操作：set、get 或 remove；旧构造函数创建的异常可能为 null。</summary>
    public string? Operation { get; }

    /// <summary>阶段：encrypt、read、decrypt、read-existing、decrypt-existing、write 或 delete。</summary>
    public string? Stage { get; }

    /// <summary>底层异常类型名，不含其 Message 或堆栈。</summary>
    public string? ErrorType { get; }

    /// <summary>底层 HRESULT；用于分类诊断，而不是猜测或覆盖旧密文。</summary>
    public int? ErrorHResult { get; }

    /// <summary>创建异常。</summary>
    /// <param name="message">说明（只含 credentialId 与路径，不含明文密钥）。</param>
    /// <param name="filePath">相关密文文件路径（可空）。</param>
    /// <param name="innerException">底层异常（可空）。</param>
    public SecretStoreException(string message, string? filePath = null, Exception? innerException = null)
        : base(message, innerException)
    {
        FilePath = filePath;
    }

    /// <summary>相关密文文件路径（可空）。</summary>
    public string? FilePath { get; }
}

/// <summary>
/// 会话文件内容损坏或校验失败。抛出时原文件保持不变，不会被静默覆盖或删除。
/// </summary>
public sealed class LocalSessionStoreException : Exception
{
    /// <summary>创建异常。</summary>
    /// <param name="message">说明（含文件路径与具体问题）。</param>
    /// <param name="filePath">相关文件路径（可空）。</param>
    /// <param name="innerException">底层解析异常（可空）。</param>
    public LocalSessionStoreException(string message, string? filePath = null, Exception? innerException = null)
        : base(message, innerException)
    {
        FilePath = filePath;
    }

    /// <summary>相关文件路径（可空）。</summary>
    public string? FilePath { get; }
}
