using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.Configuration;

/// <summary>
/// 凭据存储：每个凭据独立一份 Windows DPAPI（CurrentUser 作用域）密文文件。
/// </summary>
/// <remarks>
/// 安全边界（重要）：
/// <list type="bullet">
/// <item><description>明文仅在内存中短暂存在；文件（<c>secrets\{credentialId}.bin</c>）只含 DPAPI 密文，
/// 只能由同一 Windows 用户的同一凭据上下文解密。</description></item>
/// <item><description>本类不写日志。存储失败仅暴露操作、阶段、异常类型及 HRESULT，不暴露明文、路径或底层异常消息。</description></item>
/// <item><description><see cref="GetAsync"/>：文件不存在返回 null；密文存在但无法解密
/// （其他用户/文件损坏）抛 <see cref="SecretStoreException"/>，绝不当作“无凭据”静默处理。</description></item>
/// </list>
/// 并发：同一数据目录内的所有服务实例共享信号量；跨进程并发不在保障范围。
/// </remarks>
public sealed class DpapiSecretStore
{
    private readonly SemaphoreSlim _gate;
    private readonly string _dataDirectory;

    /// <summary>创建存储。</summary>
    /// <param name="dataDirectory">数据根目录；null 使用默认（<c>%LOCALAPPDATA%\AIFrontend</c>）。</param>
    public DpapiSecretStore(string? dataDirectory = null)
    {
        _dataDirectory = AppDataPaths.GetRootDirectory(dataDirectory);
        _gate = AtomicFile.GateForDirectory(_dataDirectory);
    }

    /// <summary>数据根目录（绝对路径）。</summary>
    public string DataDirectory => _dataDirectory;

    /// <summary>写入（覆盖）一个凭据的密文。空/空白密钥非法（如需清除请用 <see cref="RemoveAsync"/>）。</summary>
    /// <param name="credentialId">凭据 ID（1-64 字符，仅字母/数字/'.'/'_'/'-'）。</param>
    /// <param name="secret">明文密钥（不落盘、不进日志）。</param>
    public async Task SetAsync(string credentialId, string secret, CancellationToken cancellationToken = default)
    {
        AppDataPaths.ValidateStorageId(credentialId, nameof(credentialId));
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("密钥不能为空白；如需清除凭据请使用 RemoveAsync。", nameof(secret));
        }

        cancellationToken.ThrowIfCancellationRequested();

        byte[] plaintext = Encoding.UTF8.GetBytes(secret);
        byte[] ciphertext;
        try
        {
            ciphertext = ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            throw StorageError("set", "encrypt", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        string filePath = AppDataPaths.GetSecretFilePath(_dataDirectory, credentialId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ValidateExistingAsync(filePath, "set", cancellationToken).ConfigureAwait(false);
            try
            {
                await AtomicFile.WriteAllBytesAsync(filePath, ciphertext, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                throw StorageError("set", "write", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>读取并解密凭据；不存在返回 null；密文损坏/不可解密抛 <see cref="SecretStoreException"/>。</summary>
    /// <param name="credentialId">凭据 ID（1-64 字符，仅字母/数字/'.'/'_'/'-'）。</param>
    public async Task<string?> GetAsync(string credentialId, CancellationToken cancellationToken = default)
    {
        AppDataPaths.ValidateStorageId(credentialId, nameof(credentialId));
        string filePath = AppDataPaths.GetSecretFilePath(_dataDirectory, credentialId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string stage = "read";
        try
        {
            byte[]? ciphertext = await AtomicFile.ReadAllBytesOrNullAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (ciphertext is null)
            {
                return null;
            }

            stage = "decrypt";
            byte[] plaintext = ProtectedData.Unprotect(ciphertext, optionalEntropy: null, DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            throw StorageError("get", stage, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsStorageFailure(Exception error) =>
        error is CryptographicException or IOException or UnauthorizedAccessException
            or System.Security.SecurityException or NotSupportedException;

    private static SecretStoreException StorageError(string operation, string stage, Exception error) =>
        new(operation, stage, error.GetType().FullName ?? error.GetType().Name, error.HResult);

    private static async Task<bool> ValidateExistingAsync(string filePath, string operation, CancellationToken cancellationToken)
    {
        string stage = "read-existing";
        try
        {
            byte[]? ciphertext = await AtomicFile.ReadAllBytesOrNullAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (ciphertext is null)
            {
                return false;
            }
            stage = "decrypt-existing";
            byte[] plaintext = ProtectedData.Unprotect(ciphertext, optionalEntropy: null, DataProtectionScope.CurrentUser);
            CryptographicOperations.ZeroMemory(plaintext);
            return true;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            throw StorageError(operation, stage, ex);
        }
    }

    /// <summary>删除凭据密文；返回是否实际删除了文件（文件不存在返回 false，不算错误）。</summary>
    /// <param name="credentialId">凭据 ID（1-64 字符，仅字母/数字/'.'/'_'/'-'）。</param>
    public async Task<bool> RemoveAsync(string credentialId, CancellationToken cancellationToken = default)
    {
        AppDataPaths.ValidateStorageId(credentialId, nameof(credentialId));
        string filePath = AppDataPaths.GetSecretFilePath(_dataDirectory, credentialId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await ValidateExistingAsync(filePath, "remove", cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(filePath);
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                throw StorageError("remove", "delete", ex);
            }
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
