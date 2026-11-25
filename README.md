# GPU Idle Watch · GPU 空闲卡监控

在 Windows 托盘里看多台 Linux 服务器，快速找到能开工的 NVIDIA GPU。主窗口把空闲卡和剩余显存排在前面，卡片上可以查看进程、PID 和推断出的项目名。每台服务器还可以指定一个要监控的磁盘挂载点。

**[下载 GPU-Idle-Watch.exe](https://github.com/tjucrush/gpu-idle-watch/releases/latest/download/GPU-Idle-Watch.exe)** · [所有发布版本](https://github.com/tjucrush/gpu-idle-watch/releases)

## 第一次使用

1. 下载 **GPU-Idle-Watch.exe** 后双击运行。首次启动会自动打开「添加服务器」，之后也可以从主窗口添加更多服务器。
2. 填写服务器 IP 或主机名、SSH 账号、密码和要监控的磁盘挂载路径（例如 `/data`）。如果 SSH 端口不是 22，在「SSH 端口」栏修改端口。
3. 点击「验证并添加」。首次连接时，核对服务器管理员提供的 SSH 指纹并确认。软件会验证登录，再保存连接并开始监控。密码输错可以在当前窗口修改后重试。

单个 EXE 内含中文界面、托盘、密码助手和采集脚本。服务器配置和已信任的主机指纹默认位于 `%LOCALAPPDATA%\GpuWatch`，密码由 Windows DPAPI 为当前 Windows 用户加密。移动或更新 EXE 后可沿用同一用户的连接。

客户端需要 64 位 Windows 10/11、.NET Framework 4.8，以及支持 `SSH_ASKPASS_REQUIRE=force` 的 Windows OpenSSH 客户端。被监控的 Linux 服务器需要允许该账号使用 SSH 密码登录，同时能运行 `python3`（3.6 或更新版本）和 `nvidia-smi`。

如已有旧版 SSH 别名配置，可将新版 EXE 放到原有 `servers.local.json` 旁继续使用；自带密钥的 SSH 别名仍使用本机 OpenSSH 配置。

## 怎么判断哪张卡可用

| 界面状态 | 含义 |
| --- | --- |
| 空闲 | 最新采样中 GPU 利用率 ≤ 5%、已用显存 ≤ 1 GiB，且已成功读取计算进程列表，列表中该卡的进程数为零。进入空闲状态时可触发右下角提醒。 |
| 低显存 | 利用率低，显存占用在可调上限内，但还未满足空闲条件。只列为候选。 |
| 磁盘余量 | 显示指定挂载点对当前用户可用的空间。填写独立挂载点本身；挂载丢失或不可读取时显示不可用。 |

GPU 约每 2 秒采样一次，磁盘约每 30 秒更新一次。项目名从进程工作目录或启动参数推断，能看到的细节取决于 SSH 账号的 Linux 权限。主窗口关闭后会收起到托盘；右键托盘图标可以打开主窗口、切换空闲提醒或退出程序。

## 从源码运行

在 Windows PowerShell 中从仓库根目录执行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\native\build.ps1`，然后打开生成的 `GPU-Idle-Watch.exe`。

可选网页版需要 Node.js 20 或更新版本。在仓库根目录运行 `node server.mjs`，访问 [本机看板](http://127.0.0.1:8787/)，按 Ctrl+C 停止。在 Windows 上可复用桌面版保存的连接；使用密码认证时，将 `GPU-Idle-Watch.exe` 放在仓库根目录。网页版显示 GPU 和进程，桌面版另提供磁盘余量、托盘和空闲提醒。

## 许可证

[MIT](LICENSE)
