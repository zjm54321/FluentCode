# SSH 连接 OpenCode

在“连接 OpenCode”中选择 **SSH 自动发现**，填写 Windows SSH 配置别名或 `user@host[:port]`，即可通过本机隧道使用远程 OpenCode；会话中的文件也可交给 Zed / VS Code 打开。

## 准备

- Windows 已安装 **OpenSSH 客户端**，可运行 `C:\Windows\System32\OpenSSH\ssh.exe` 与 `ssh-keygen.exe`。
- 远端已安装提供 `opencode service status`、`service start` 与 `service get password` 的 OpenCode CLI（opencode-qol 配套版本）。FluentCode 不会安装或升级远端软件。
- SSH 公钥认证已经配置。FluentCode 不弹出 SSH 密码或密钥密码短语输入框，所有连接均使用 `BatchMode=yes`。
- 带密码短语的私钥请使用 **Windows** 的 OpenSSH Authentication Agent（`ssh-agent`）服务，并在 Windows 终端运行 `ssh-add` 加载密钥；WSL 的 agent 不等于 Windows 的 agent。启用系统服务可能需要管理员权限。
- 远程服务需通过 HTTP 监听 `127.0.0.1` 或 `0.0.0.0`，SSH 服务器需允许本地端口转发。服务端口由 CLI 发现，不固定为 4096。

Windows OpenSSH 仍负责读取 `%USERPROFILE%\.ssh\config`、身份文件、agent、`ProxyJump` 和 `known_hosts`。主机选择框会展开 `Include`（例如 `conf.d/*.conf`），只列出具体 `Host` 别名，忽略 `*`、`?` 和 `!` 等模式；也可直接输入主机。

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
    # ProxyJump gateway
```

## 连接

1. 选择 **SSH 自动发现**，填写 `dev` 或 `ming@dev.example.com:22`。
2. 可选填写“远程 OpenCode 命令或路径”，例如 `opencode`、`~/bin/opencode` 或 `/etc/profiles/per-user/ming/bin/opencode`。这是一个可执行文件名或路径，不是 shell 脚本；不要填写密码、环境变量或命令参数。
3. 点击“连接 OpenCode”。首次遇到未知主机密钥会展示 SHA256 指纹，请通过独立渠道向管理员核对，再选择“指纹一致，信任并连接”。取消不会保存密钥或替换当前连接。
4. 连接成功后安全保存并替换当前唯一 OpenCode 连接。服务密码存入当前 Windows 用户的 DPAPI 存储，不写入配置、命令行或日志。

发现命令会使用登录 shell：先 `$SHELL -lc`，再 `bash -lc`；随后尝试 `~/.nix-profile/bin`、`/etc/profiles/per-user/$USER/bin` 与 `/run/current-system/sw/bin`。这用于 NixOS / home-manager 等登录环境；路径仍不正确时请明确填写可执行文件路径。远程登录脚本不要在非交互会话中读取终端输入。

## 自动启动与恢复

- `opencode service status` 返回运行地址时直接复用服务，仅执行 `service get password` 取得 HTTP 认证；不读取完整服务配置。
- **只有**状态明确返回 `stopped` 时，才执行 `opencode service start`，随后限时轮询状态。它不会调用 `restart` / `stop`，也不修改服务端口、环境变量或配置。
- 隧道仅绑定 Windows 本机 `127.0.0.1`，将端口转发到远端 `127.0.0.1:<服务端口>`。HTTP / SSE 客户端保持不变。
- SSH 进程放入 kill-on-close Windows Job；退出或终止 FluentCode 会结束本地隧道，**不会停止远端 OpenCode**。
- 隧道断开后会进行最多三次有退避的恢复（总等待上限 90 秒）。恢复会重新发现服务、获取凭据、替换连接来源；不会自动重放发送消息、权限确认等写操作。未知结果仍需用户确认。

## 在编辑器中打开

已连接会话提供绝对工作目录后，快捷入口支持：

- Zed：`zed ssh://[user@]host[:port]/abs/path`。
- VS Code：`code --remote ssh-remote+<host> /abs/path`，请先安装 VS Code 的 Remote - SSH 扩展。

编辑器使用各自的 SSH 实现与配置。SSH v1 **不支持在 Windows 资源管理器中打开远程目录**，不创建 UNC 映射，也不安装 SFTP 挂载组件。

## 安全与排错

- 不会静默接受未知主机密钥。先由 `ssh.exe` 使用 `StrictHostKeyChecking=yes` 检查已有信任；已变化或已撤销的密钥立即拒绝。
- 仅对未知密钥创建应用 LocalAppData 下的一次性探测目录。探测使用 `accept-new`，但只写临时 `known_hosts`，禁用目标端的公钥、密码、键盘交互、GSSAPI、host-based 与 agent 认证，禁用转发、复用连接与终端；预期在公钥写入后认证失败，不执行远程命令。`ProxyJump` 跳板机仍按用户配置正常认证。
- 用 `ssh-keygen -lf` 展示临时文件中的密钥类型与 SHA256 指纹，并显示 `ssh -G` 解析后的主机和端口。确认后将**这些原始公钥行**追加到 `%USERPROFILE%\.ssh\known_hosts`，不覆盖旧条目、不重复追加同一行；Zed / VS Code 因此可复用相同信任。取消不触碰用户的 `known_hosts`，所有路径都会清理一次性探测目录。
- 真正的连接与后续恢复始终使用 `StrictHostKeyChecking=yes`，只接受用户看到并确认的密钥；不存在展示后再用 `accept-new` 信任另一把密钥的窗口。不使用 `ssh-keyscan`，也不修改服务端算法。
- 出现“主机密钥已变化”时停止连接；先向管理员核对指纹，再手动处理 Windows `known_hosts`，不要直接删除旧条目来绕过警告。
- `ProxyJump` 的跳板机需已核对并信任。最终目标的解析、密钥别名、IP 校验与跳转均由同一个 Windows `ssh.exe` 处理，不额外拼接扫描路线。
- “公钥认证失败”时检查 Windows `ssh-agent` / `ssh-add`、SSH 用户名和 `IdentityFile`。FluentCode 不转发 agent（`ForwardAgent=no`），不用密码认证兜底。
- “未找到 opencode”时填写远端实际路径；“启动超时”时在远端自行查看 `opencode service status`。远端的服务配置可能包含秘密环境变量，请勿将完整配置粘贴到日志或截图中。

## 开发验证

`tests/opencode-ssh/run.ps1 -EvidenceRoot <目录>` 只运行离线单元测试。`bash tests/opencode-ssh/run-fixture.sh` 创建临时用户级 sshd，使用服务器默认 KEX：显式指定 throwaway key / config / known_hosts，不读取或写入用户真实 `.ssh` 或 agent，仅连接 `127.0.0.1:2222`。集成保持现有 WSL OpenCode 服务运行，自动启动分支用独立 shim 模拟；覆盖未知密钥确认、HTTP 隧道、Job 生命周期、断线恢复与更换主机密钥后的严格拒绝，最后清理临时 sshd、密钥与目录。
