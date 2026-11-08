# SSH 安全复查修复记录

工作区：`/mnt/c/src/fluentcode-lanes/ssh-security`，分支 `lane/ssh-security`，基线 `d15d290`。未修改设计师工作的 ssh-remote 克隆或 Pages/Account UI；仅修改获准的 OpenCode 服务、App.OpenCode.cs、测试和文档。未推送、未提交 artifacts。

## 逐项修复与证据

下表路径相对本工作区。

| 项目 | 修复 | 文件:行 |
|---|---|---|
| B1 | 每个新 HTTP/SSE/WS socket 在 TCP connect 后检查已建立对端四元组及拥有者 PID；要求与仍持有句柄、未退出的 ssh.exe 相同。验证前无应用字节。连接池按传输实例隔离，readiness 同样验证；退出事件同步关闭传输与全部连接。 | `src/FluentCode/Services/OpenCode/Connection/SshTcpOwnership.cs:22-49,51-97`; `src/FluentCode/Services/OpenCode/OpenCodeSocketTransport.cs:35-72`; `src/FluentCode/Services/OpenCode/Connection/SshOpenCodeTunnel.cs:51,102,140-144` |
| B2 | SSH handler 显式 `UseProxy=false`，WebSocket `Options.Proxy=null` 且使用相同校验 callback；WSL/直接远程默认策略不变。 | `src/FluentCode/Services/OpenCode/OpenCodeSocketTransport.cs:35-39`; `src/FluentCode/Services/OpenCode/OpenCodeClient.cs:92-102`; `src/FluentCode/Services/OpenCode/OpenCodeClient.PersistentPty.cs:132-136` |
| B3 | 已确认当前 CLI start 无 hostname 选项。Stopped 时必须先读取明确的回环 hostname、remote=false，且保存 env 不更换配置命名空间/运行时，否则拒绝 start。已运行通配监听只复用并返回警告；连接验证匿名 GET 必须得到 401。不重配或重启。 | `src/FluentCode/Services/OpenCode/Connection/SshOpenCodeServiceLocator.cs:60-117`; `src/FluentCode/Services/OpenCode/Connection/SshOpenCodeConnector.cs:29-42` |
| S1 | 强制禁止 localhost/DNS 信任绕过、本地命令、X11/agent、自动密钥更新和外部 key command；命令 SessionType=default，探测/隧道=none。实际非隧道命令清除转发；-G 只读检查保留转发声明以便在网络操作前拒绝隧道的额外转发。 | `src/FluentCode/Services/OpenCode/Connection/SshCommandBuilder.cs:23-68,112-128`; `src/FluentCode/Services/OpenCode/Connection/SshHostKeyVerifier.cs:72-82` |
| S2 | 探测 `-N -T`、无远程 command，认证方法均关闭。取得完整公钥行后限时停止 Job，不再把 Permission denied 当作无会话证明；none 认证即使成功也无 shell/session。 | `src/FluentCode/Services/OpenCode/Connection/SshCommandBuilder.cs:43-53`; `src/FluentCode/Services/OpenCode/Connection/WindowsSshProcess.cs:53-99` |
| S3 | 在已知/未知主机第一次连接前检查有效配置，拒绝 ProxyJump；不引入未经双跳验证的 hop 管理器。ProxyCommand/Match exec 作为可信用户本地代码记录，最终 host-key 强制策略仍生效。 | `src/FluentCode/Services/OpenCode/Connection/SshHostKeyVerifier.cs:19,72-82`; `src/FluentCode/Services/OpenCode/Connection/SshConnectionTarget.cs:122-124`; `docs/ssh.md` |
| S4 | 在 Windows argv 转义之外，对 -o 文件名再按 OpenSSH 双引号语法转义；支持空格和单引号。 | `src/FluentCode/Services/OpenCode/Connection/SshCommandBuilder.cs:126-132`; `tests/opencode-ssh/run-fixture.sh:7`; `tests/opencode-ssh/prepare-fixture.ps1:20` |
| S5 | 使用第一个有效用户文件，而非硬编码默认目录。-G 丢失文件边界时，采用原生 token/tilde 展开记录；字面路径采用仅排除全局文件的严格只读 lookup 记录。无法可靠解析/none/不可写，在展示确认之前拒绝。最终连接不覆盖用户全局配置。 | `src/FluentCode/Services/OpenCode/Connection/SshHostKeyVerifier.cs:26-37,121-181` |
| S6 | CreateProcessW 使用 CREATE_SUSPENDED，只有指定三个管道句柄继承；AssignProcessToJobObject 成功后 ResumeThread。新建独立 Job，不改变应用 Job。所有清理只针对创建的进程/Job。 | `src/FluentCode/Services/OpenCode/Connection/SshOwnedProcess.cs:42-109,130-149` |
| S7 | stdout/stderr 共用 64 KiB 命令预算；隧道每流保留 16 KiB 滚动尾部并对 64 KiB/s 洪泛拒绝。溢出结束所属 Job、固定错误，不日志输出正文。 | `src/FluentCode/Services/OpenCode/Connection/SshOutputLimit.cs:9-51`; `src/FluentCode/Services/OpenCode/Connection/WindowsSshProcess.cs:21-49`; `src/FluentCode/Services/OpenCode/Connection/SshOpenCodeTunnel.cs:115-129` |
| F1 | 共享冲突最多 4 次、总 500ms 退避。取消只检查完整记录之间，record write/flush 使用 CancellationToken.None。保留旧字节及原始换行，不替换共享文件；完全相同行去重，盐值不同不承诺语义去重。 | `src/FluentCode/Services/OpenCode/Connection/SshHostKeyVerifier.cs:184-220`; `tests/opencode-ssh/SecurityTests.cs` |
| F2 | SSH 不写 DPAPI 密码、不保留 CredentialId；提交时清除旧 SSH 密文，下一次任何设置保存后也原子移除旧 SSH 引用，再删除未引用密文。Memory-only 进度属实。 | `src/FluentCode/Services/OpenCode/Connection/OpenCodeConnectionCommitter.cs:85-107,128-160`; `src/FluentCode/App.OpenCode.cs:83-100` |
| F3 | IPv6 scope 仅数值、UInt32 范围并规范化地址/索引；显示使用括号。保留服务 loopback 地址族，::1 转发目标为 [::1]，并有实际 Windows IPv6 peer-owner 测试。 | `src/FluentCode/Services/OpenCode/Connection/SshConnectionTarget.cs:10-12,68-82`; `src/FluentCode/Services/OpenCode/Connection/SshOpenCodeServiceLocator.cs:12-29`; `src/FluentCode/Services/OpenCode/Connection/SshOpenCodeConnector.cs:24` |

### CLI 与路径诊断依据

- 只读 `opencode service start --help` 返回 0，只有 global flags，没有 hostname/bind 参数。`/home/ming/opencode-qol` 锁定上游的 start handler 调用 `Service.ensure(ServiceConfig.options())`；ServerProcess 使用 `options.hostname ?? config.hostname ?? "127.0.0.1"`。另一版本未知默认不据此猜测，要求明确配置值。
- ServiceConfig.options 会把保存 env 传给服务子进程，因此额外拒绝 HOME/XDG_CONFIG_HOME/OPENCODE_CONFIG_DIR/NODE_OPTIONS 等改变配置根或加载运行时的保存项；该 env 输出在有限缓冲内读取，不记录其值。
- 只读 Windows `ssh.exe -vvv -G -F NUL -o "UserKnownHostsFile=\"C:/Users/Test Folder's/known_hosts\" \"C:/other\"" fixture` 显示未加引号的文件列表，不能凭空格重构边界。字面路径没有 expanded 记录，live 验证了 `load_hostkeys: fopen ...` 的原生完整文件名。没有可信原生证据时失败关闭。

## 验证

均从本 lane 执行；PowerShell 7 子进程 PATH 前置 `C:\Program Files\dotnet`，日志在 `/tmp/opencode/sshsec-*.log`。

| 命令 | 退出 | 结果 |
|---|---:|---|
| `pwsh.exe -NoProfile -NonInteractive -Command '$env:Path="C:\Program Files\dotnet;"+$env:Path; & .\tests\opencode-ssh\run.ps1 -EvidenceRoot .\artifacts\sshsec-unit-20261107T1658; if (!$?) { exit 1 }; exit 0'` | 0 | **211 assertions**；包括真实 Windows IPv4/IPv6 owner 表、HTTP/SSE/WS 冒充零字节、代理零流量、输出溢出、自有立即 grandchild 取消、共享冲突、记录边界取消、旧密码清除。 |
| `pwsh.exe -NoProfile -NonInteractive -Command '$env:Path="C:\Program Files\dotnet;"+$env:Path; & .\tests\opencode-connection\run.ps1 -EvidenceRoot .\artifacts\sshsec-connection-20261107T1652; if (!$?) { exit 1 }; exit 0'` | 0 | **96 assertions**，既有 WSL/Remote/DPAPI 原子提交/失败保留/共享 HTTP 池。 |
| `bash ./tests/opencode-ssh/run-fixture.sh` | 0 | **29 assertions**；`artifacts/sshsec-live-20261107T165728Z/live-tunnel.json` 和 results.txt。默认 KEX；未知/取消/变更密钥；自定义空格/单引号信任路径；预启动与退出后 squatter 均零字节；proxy 零流量；匿名401/认证200；通配警告；shim-only start；Job/恢复。 |
| `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-debug.ps1` | 0 | 无 warning/error 输出，Debug build/publish/runtime hashes，`artifacts/native-debug-20261107-165206-0b311e2f`。 |
| `git diff --check` | 0 | 在提交前/后检查，新增文件也以 cached diff 检查。 |

未运行 full suite、native composer/RichEdit harness、应用窗口或截图；没有桌面输入。native 测试进程均为无窗口创建。只根据已创建的 sshd PidFile/确切配置路径清理 listener，Windows 进程由自有 Job/句柄管理；每个显式信号/Job 关闭目标均有 PID 日志。现有 WSL OpenCode 不启动、停止或重启。

## 限制与剩余风险

- ProxyJump 暂不支持；需要受控 hop 调用及双跳 fixture 才能启用。ProxyCommand/Match exec 是用户可信可执行策略，不宣称沙箱。
- 无会话探测已用 argv/模拟与实际拒绝认证 fixture 覆盖。未新建或修改真实 Linux 账号来实现 passwordless `none` 认证；`-N -T` 的无 session 行为不依赖认证失败。
- 当前 start API 没有一次性 bind flag；独立配置查询与启动不能防止远端用户在二者之间主动更改配置。启动后的地址再验证，变化则拒绝本次连接，不偷偷重配/停止。可读配置不足以证明安全时不自动启动。
- shared known_hosts 的取消完整记录保证不等于 crash/disk 事务；盐值不同的 hashed 行不承诺语义去重。原生诊断格式不支持的客户端会在确认前拒绝，不猜路径。
- 真实服务器、自定义登录脚本和实际编辑器未测试。全局 crash handler 的旧有异常序列化未扩展审查；本次进程输出不进入异常消息或日志。

## 给设计师的 UI hooks

- `SshOpenCodeService.Warning`、`OpenCodeClientOptions.ConnectionWarning` → `OpenCodeCommitOutcome.Warning`。连接页成功文案请保留此警告，不用固定成功文本覆盖；这是非阻断提示，不需新增强制 opt-in。
- `App.OpenCode.cs:224` 将现有 running-wildcard 警告送入现有 `OpenCodeRecoveryStatus` 路径；请以已有状态样式展示，而非误标为正在恢复。
- B3 拒绝自动启动、S3 拒绝 ProxyJump、S5 不可用 trust file 已通过固定中文 exception/progress 文案传递，使用已有 error/status 区域即可。
- SSH 的保存成功消息应为“已保存 SSH 连接设置；服务密码仅在内存中使用，未持久化”，不要显示 DPAPI 密码已保存或把 HasCredential=false 当作 SSH 未配置。
