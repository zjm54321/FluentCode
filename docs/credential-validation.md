# Windows 凭据存储实测与安全诊断（2026-10-06）

## 结论与边界

**未复现用户的正常 token 保存失败，不能声称该用户问题已修复。** 在 Windows x64 / .NET 10.0.12，直接加载上次成功构建的真实 `AIFrontend.dll` 及其同目录 `System.Security.Cryptography.ProtectedData.dll`，独立临时目录、固定假凭据的 set/get/update/delete 均通过。未读取、解密、覆盖或删除用户真实凭据；未操作 GUI、真实 HTTP、配置、证书或 git；未重建主应用。

因此本次仅补充后端可诊断异常语义，没有更换 DPAPI、改用明文、绕过旧密文校验或自动重置凭据。

## 源码改动与 UI 契约

- `src/AIFrontend/Services/Configuration/DpapiSecretStore.cs`：对预期加密/文件系统/权限/平台支持异常统一转换为 `SecretStoreException`；已取消的 Set 在加密前退出。保留 CurrentUser、原子写入、旧密文保护与缺失返回 null/false 语义。
- `src/AIFrontend/Services/Configuration/ConfigurationExceptions.cs`：仅扩展 `SecretStoreException`，保留旧公开构造函数；新增可空只读属性 `Operation`、`Stage`、`ErrorType`、`ErrorHResult`。其他异常类型未改。
- 本存储产生的新异常 `Message` 只包含固定操作/阶段、底层异常类型和十六进制 HRESULT；`HResult` 同步底层值，`InnerException` 与 `FilePath` 为 null，避免调用方展开原始异常而泄露路径。旧构造函数仍可能保留原有元数据；不可将新异常的安全保证推广到任意异常。
- 操作：`set` / `get` / `remove`。
- 阶段：`encrypt` / `read` / `decrypt` / `read-existing` / `decrypt-existing` / `write` / `delete`。`write` 是 AtomicFile 写入阶段的整体标签，包括创建目录/临时文件、写入、flush 与 move，不声称已进一步区分其中子步骤。
- **UI 后续**：捕获 `SecretStoreException` 可展示其安全 Message，或展示以上字段；不得依赖 `InnerException`/`FilePath`。参数错误仍为 ArgumentException，取消仍为 OperationCanceledException；取消应表示取消，不当作 token 无效。读取失败不能静默退化为无凭据请求。解密旧文件失败应保留旧文件并明确提示，不能自动删除/覆盖。此轮未修改 Settings UI。

示例（本轮人工制造的隔离故障，不是用户实际故障）：

| 场景 | operation / stage | 原始类型 | HRESULT |
|---|---|---|---|
| 旧密文文件被独占锁定，尝试保存 | set / read-existing | System.IO.IOException | 0x80070020 |
| 假损坏密文读取 | get / decrypt | System.Security.Cryptography.CryptographicException | 0x80070057 |
| 假损坏密文覆盖/删除 | set 或 remove / decrypt-existing | System.Security.Cryptography.CryptographicException | 0x80070057 |
| secrets 路径被测试普通文件占据 | set / write | System.IO.IOException | 0x800700B7 |

这些错误码只解释对应实测故障，不据此推断用户的 ACL、用户上下文或实际文件状态。

## 实测证据

目录：`artifacts/validation/credentials-20261006/`。

1. `before/result.json`：**21 项断言通过**。通过反射调用真实部署应用中的存储类，不是 stub；依赖从应用输出目录解析。验证不存在读取/删除、保存读取、更新、跨实例读取、实际 DPAPI CurrentUser 解密、密文不含假明文、无临时文件残留、取消保留旧数据、独占锁失败保留旧数据、删除、旧损坏密文 get/set/remove 拒绝且保持字节不变、目录冲突。
2. `after/result.json`：**46 项断言通过**。对修改后的四个真实 headless 源文件快照进行独立编译，仍使用同一个应用部署 ProtectedData DLL，然后重跑上述验证及操作/阶段/HResult/无原始异常链、路径和凭据文本的断言。不是修改后完整主工程构建或 UI 验收。
3. `probe-build.log`、`probe-build-after.log`、`patched-build.log`：独立工程编译均成功，0 警告、0 错误。NuGet.Config 清空外部源，探针无包依赖；未安装工具或修改主项目依赖。
4. `source-consistency.json`：DpapiSecretStore、ConfigurationExceptions、AtomicFile、AppDataPaths 与独立编译快照逐文件 SHA-256 核对一致。
5. `Program.cs`、`Probe.csproj`、`patched-service/`：可复现探针、源码快照和独立项目。`before/after` 内 `fake-data-<guid>` 全部是本次新建的测试专用数据；不接触默认 `%LOCALAPPDATA%\AIFrontend`。

部署应用 DLL SHA-256：`5CB53430557BB5668F8A2DFD5BEFBD3C42BFEB2ECEAF34FD93E21A53ECABC32A`。

实际加载 ProtectedData DLL SHA-256：`B61A1445B99A1486A5D9ABBD45D766497F136270F5B04862DEFEE1FC054DED14`，前后相同，10.0.12 包的 net10.0 程序集。

## 执行命令（Windows 路径）

`$root = 'C:\Users\Zhang\source\repos\AIFrontend'`，`$e = "$root\artifacts\validation\credentials-20261006"`，`$bin = "$root\src\AIFrontend\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64"`。

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build "$e\Probe.csproj" --configfile "$e\NuGet.Config"
& 'C:\Program Files\dotnet\dotnet.exe' "$e\bin\Debug\net10.0\Probe.dll" "$bin\AIFrontend.dll" $bin "$e\before"
& 'C:\Program Files\dotnet\dotnet.exe' build "$e\patched-service\CredentialBackend.csproj" --configfile "$e\NuGet.Config"
# 添加诊断断言后重新编译 Probe，再运行：
& 'C:\Program Files\dotnet\dotnet.exe' build "$e\Probe.csproj" --configfile "$e\NuGet.Config"
& 'C:\Program Files\dotnet\dotnet.exe' "$e\bin\Debug\net10.0\Probe.dll" "$e\patched-service\bin\Debug\net10.0\CredentialBackend.dll" $bin "$e\after"
```

尚未验证：用户真实报错的 HRESULT/阶段、默认数据目录 ACL/占用、不同 Windows 用户或损坏用户 profile、真实跨进程竞争/磁盘故障/断电原子性。父会话需整合主工程构建并让 UI 显示新诊断后，才能闭环实际用户故障。
