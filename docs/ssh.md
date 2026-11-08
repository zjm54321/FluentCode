# SSH 连接 OpenCode

在“连接 OpenCode”中选择 **SSH 自动发现**，填写 Windows SSH 配置别名或 `user@host[:port]`，即可通过本机隧道使用远程 OpenCode；会话中的文件也可交给 Zed / VS Code 打开。

## 准备

- Windows 已安装 **OpenSSH 客户端**，可运行 `C:\Windows\System32\OpenSSH\ssh.exe` 与 `ssh-keygen.exe`。
- 远端已安装提供 `opencode service status`、`service start` 与 `service get password` 的 OpenCode CLI（opencode-qol 配套版本）。自动启动还需支持安全读取 `hostname`、`remote` 与 `env` 配置；未知版本无法证明安全绑定时会拒绝自动启动。FluentCode 不会安装或升级远端软件。
- SSH 公钥认证已经配置。FluentCode 不弹出 SSH 密码或密钥密码短语输入框，所有连接均使用 `BatchMode=yes`。
- 带密码短语的私钥请使用 **Windows** 的 OpenSSH Authentication Agent（`ssh-agent`）服务，并在 Windows 终端运行 `ssh-add` 加载密钥；WSL 的 agent 不等于 Windows 的 agent。启用系统服务可能需要管理员权限。
- 远程服务建议通过 HTTP 仅监听 `127.0.0.1` 或 `::1`，并必须拒绝未认证 API 请求（HTTP 401）；SSH 服务器需允许本地端口转发。服务端口由 CLI 发现，不固定为 4096。

Windows OpenSSH 仍负责读取 `%USERPROFILE%\.ssh\config`、身份文件、agent 和 `known_hosts`。主机选择框会展开 `Include`（例如 `conf.d/*.conf`），只列出具体 `Host` 别名，忽略 `*`、`?` 和 `!` 等模式；也可直接输入主机。**当前版本拒绝 `ProxyJump` 跳板配置与继承的额外端口转发**，以免自动子进程绕过主机密钥策略或开放其他监听。

FluentCode 的调用会明确禁用终端分配、配置中的自定义 `RemoteCommand` 与连接复用，并保留标准输入供服务发现脚本使用，使 SSH 别名的交互式登录设置不干扰后台连接。

```sshconfig
Include conf.d/*.conf
```

```sshconfig
# %USERPROFILE%\.ssh\conf.d\dev.conf
Host dev
    HostName dev.example.com
    User ming
    Port 22
    IdentityFile ~/.ssh/id_ed25519
    ProxyJump none
```

## 连接

1. 选择 **SSH 自动发现**，填写 `dev` 或 `ming@dev.example.com:22`。
2. 可选填写“远程 opencode 路径”，例如 `opencode`、`~/bin/opencode` 或 `/etc/profiles/per-user/ming/bin/opencode`。这是一个可执行文件名或路径，不是 shell 脚本；不要填写密码、环境变量或命令参数。
3. 点击“连接 OpenCode”。首次遇到未知主机密钥会分别展示算法与 SHA256 指纹，每个指纹旁可复制标准 `SHA256:…` 文本。请通过独立渠道向管理员核对全部指纹，再选择“一致，信任并连接”。取消为默认按钮，不会保存密钥或替换当前连接。
4. 连接成功后安全保存并替换当前唯一 OpenCode 连接。SSH 服务密码每次发现时重新取得，**只在内存中使用，不持久化**。旧版本留下的 SSH 密码密文将在下一次设置保存后清除；其他连接类型仍使用 DPAPI。

发现命令会使用登录 shell：先 `$SHELL -lc`，再 `bash -lc`；随后尝试 `~/.nix-profile/bin`、`/etc/profiles/per-user/$USER/bin` 与 `/run/current-system/sw/bin`。这用于 NixOS / home-manager 等登录环境；路径仍不正确时请明确填写可执行文件路径。远程登录脚本不要在非交互会话中读取终端输入。

## 自动启动与恢复

- `opencode service status` 返回运行地址时直接复用服务，仅执行 `service get password` 取得 HTTP 认证；不读取完整服务配置。
- **只有**状态明确返回 `stopped`，且配置的 `hostname` 是明确的回环 IP、`remote` 公开入口关闭、保存的环境不重定向配置目录或运行时，才执行 `opencode service start` 并限时轮询。当前 CLI 的 start 不支持一次性 hostname 参数；未设置 hostname、配置为 `0.0.0.0` / `::` 或无法核实时，会给出中文说明并拒绝自动启动，不会偷偷重配或重启服务。
- 已经运行的 `0.0.0.0` / `::` 服务仍可连接，但返回明确的安全警告：SSH 隧道不能保护远端已公开的 HTTP 监听。FluentCode 不改变该服务配置。
- 隧道仅绑定 Windows 本机 `127.0.0.1`；保留发现的远端地址族，`::1` 转发到 `[::1]:<服务端口>`。HTTP、SSE 和 WebSocket 每个新 socket 都检查 TCP 对端拥有者是本次仍存活的 ssh.exe；不匹配会在发送任何应用字节前关闭连接。
- SSH 传输明确禁用系统/环境 HTTP 代理，WebSocket 代理也为 null；WSL 与直接远程连接的既有策略不变。SSH 退出时同步撤销传输、关闭其连接，不能等 UI 恢复队列才失效。
- SSH 进程在 **CREATE_SUSPENDED → 加入 kill-on-close Job → ResumeThread** 后才执行，重定向管道只继承指定句柄；退出或终止 FluentCode 会结束本地进程及其 Job 内子进程，**不会停止远端 OpenCode**。
- 隧道断开后会进行最多三次有退避的恢复（总等待上限 90 秒）。恢复会重新发现服务、获取凭据、替换连接来源；不会自动重放发送消息、权限确认等写操作。未知结果仍需用户确认。
- 首次启动显示“正在连接 SSH…”，恢复时显示重连次数。`OpenCodeConnectionStatus(Label, Detail, Failed, FailureKind, Warning)` 分别提供顶栏短标签、提示详情和状态语义，不再按文本是否以省略号结尾推断失败；WSL 使用相同结构。
- 连接成功仅 SSH 显示成功条；公开监听的 `Warning` 会保留到保存结果和主页面非阻断提示条，而不误标为连接失败。恢复遇到主机密钥变化时，顶栏为危险状态，并在主页面显示可关闭的独立提示条。

## 在编辑器中打开

已连接会话提供绝对工作目录后，快捷入口支持：

- Zed：`zed ssh://[user@]host[:port]/abs/path`。
- VS Code：`code --remote ssh-remote+<host> /abs/path`，请先安装 VS Code 的 Remote - SSH 扩展。

编辑器使用各自的 SSH 实现与配置。SSH v1 **不支持在 Windows 资源管理器中打开远程目录**，不创建 UNC 映射，也不安装 SFTP 挂载组件。

## 安全与排错

- 不会静默接受未知主机密钥。先由 `ssh.exe` 使用 `StrictHostKeyChecking=yes` 检查已有信任；已变化或已撤销的密钥立即拒绝。
- 仅对未知密钥创建应用 LocalAppData 下的一次性探测目录。探测使用 `accept-new`，但只写临时 `known_hosts`，禁用目标端的公钥、密码、键盘交互、GSSAPI、host-based 与 agent 认证，并使用 **`-N -T` 无会话模式且不传远程命令**；即使服务器接受初始 `none` 认证，也不会打开 shell 或命令会话。取得完整公钥行后限时结束本次探测；取消即清理。
- 用 `ssh-keygen -lf` 展示临时文件中的密钥类型与 SHA256 指纹，并显示 `ssh -G` 解析后的主机和端口。确认后将**这些原始公钥行**追加到第一个有效 `UserKnownHostsFile`（默认仍是 `%USERPROFILE%\.ssh\known_hosts`），支持自定义文件、空格和单引号路径；为 none、不可写或无法可靠解析时，在确认前拒绝。
- `ssh -G` 会丢失文件列表的引号，因此文件边界与 token/tilde 展开由严格的只读原生诊断核实，不按空格猜测路径；捕获的诊断不写日志。必要时仅为解析用户文件，另做一次排除全局文件的严格只读检查，最终连接仍使用用户原本的信任配置。
- 追加操作保留旧字节及 CRLF/LF，不引入 BOM、覆盖或删除旧条目，并对完全相同的公钥行去重。共享冲突最多退避重试 4 次，取消只在完整记录之间生效；单个记录的写入和 flush 不受取消打断。崩溃/磁盘故障仍可能留下一条不完整记录；不同盐值的 HashKnownHosts 行不承诺语义去重，不以替换共享文件的方式实现事务。
- 真正的连接与后续恢复始终使用 `StrictHostKeyChecking=yes`，只接受用户看到并确认的密钥；不存在展示后再用 `accept-new` 信任另一把密钥的窗口。不使用 `ssh-keyscan`，也不修改服务端算法。
- 出现“主机密钥已变化”时停止连接；先向管理员核对指纹，再手动处理 Windows `known_hosts`，不要直接删除旧条目来绕过警告。
- 连接页可复制删除旧记录的 PowerShell 命令，但不会执行；命令使用解析后的主机 / 密钥别名和可核实的信任文件路径，无法核实路径时保留明确占位符供用户填写。缺少 Windows OpenSSH 时以 `MissingOpenSsh` 单独报告，可从提示内打开 Windows“可选功能”。
- `ProxyCommand` 与 `Match exec` 被视为**用户可信的本地可执行配置**，不是沙箱；它们可运行本地代码，但最终目标仍须经过强制的主机密钥验证。后续支持 ProxyJump 需要受控跳板调用与双跳测试。
- 强制禁用 `NoHostAuthenticationForLocalhost` 绕过、DNS 主机密钥自动信任、本地命令、X11、agent 转发与自动密钥更新；普通命令清除继承的转发，探测/隧道强制无会话类型。IPv6 scope 仅接受并规范化数值接口索引，不接受任意 scope 文本。
- 命令 stdout/stderr 共用 64 KiB 输出预算；长期隧道各保留最多 16 KiB 滚动诊断尾部，输出洪泛会结束所属 Job 并返回固定错误，不记录可能包含密码的输出。
- “公钥认证失败”时检查 Windows `ssh-agent` / `ssh-add`、SSH 用户名和 `IdentityFile`。FluentCode 不转发 agent（`ForwardAgent=no`），不用密码认证兜底。
- “未找到 opencode”时填写远端实际路径；“启动超时”时在远端自行查看 `opencode service status`。远端的服务配置可能包含秘密环境变量，请勿将完整配置粘贴到日志或截图中。

## 开发验证

`tests/opencode-ssh/run.ps1 -EvidenceRoot <目录>` 运行隔离文件、伪 CLI、回环 socket 与自有无窗口进程测试。`bash tests/opencode-ssh/run-fixture.sh` 创建默认 KEX 的临时用户级 sshd：显式指定 throwaway key / config / custom known_hosts，不使用用户真实 `.ssh` 或 agent，仅连接 `127.0.0.1:2222`；现有 WSL OpenCode 保持运行，自动启动用 shim 模拟。覆盖端口冒充零泄漏、代理零流量、未认证 401、未知/变化密钥、自定义带空格/单引号信任路径、Job 与恢复。只根据自有 PidFile 与已捕获进程句柄清理，并记录每个显式信号目标；不进行树、组或名称 kill，不发送桌面输入。
