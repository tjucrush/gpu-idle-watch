# GPU Idle Watch · GPU 空闲卡监控

在 Windows 托盘里看多台 Linux 服务器，快速找到能开工的 NVIDIA GPU。主窗口把空闲卡和剩余显存排在前面，卡片上可以查看进程、PID 和推断出的项目名。每台服务器还可以指定一个要监控的磁盘挂载点。

**[下载 Windows 便携版](https://github.com/tjucrush/gpu-idle-watch/raw/refs/heads/main/downloads/gpu-idle-watch-windows.zip)** · [查看源代码](https://github.com/tjucrush/gpu-idle-watch)

## 第一次使用

1. 将 Windows 便携版 ZIP 完整解压到一个文件夹。客户端需要 64 位 Windows 10/11、.NET Framework 4.8 和 Windows OpenSSH 客户端。被监控的 Linux 服务器需要能通过 SSH 登录，并能运行 `python3`（3.6 或更新版本）和 `nvidia-smi`。
2. 在 Windows 的 `%USERPROFILE%\.ssh\config` 添加自己的 SSH 连接。下面的域名和用户名是示例，请换成自己的。

   ```sshconfig
   Host gpu-lab
       HostName gpu.example.org
       User your-user
       Port 22
   ```

3. 在 PowerShell 运行 `ssh gpu-lab`，和服务器管理员核对首次连接显示的主机指纹，登录成功后输入 `exit`。后台监控会校验已信任的主机密钥。
4. 在解压目录复制 `servers.example.json` 为 `servers.local.json`。填写显示名称、SSH 别名、Linux 上实际挂载的目录，以及认证方式。多台服务器就在 `servers` 数组中追加条目，为每台使用不同的名称和别名。

   ```json
   {
     "servers": [
       { "name": "实验室 GPU", "ssh": "gpu-lab", "mount": "/data", "auth": "key" }
     ]
   }
   ```

5. **密钥登录**时保留 `"auth": "key"`，先确认 `ssh -o BatchMode=yes gpu-lab` 能登录。如果私钥有口令，可先将它载入本机的 ssh-agent。

   **密码登录**时改成 `"auth": "password"`。在解压目录运行下面的命令，在隐藏输入提示处输入自己的服务器登录密码。此方式需要支持 `SSH_ASKPASS_REQUIRE=force` 的 OpenSSH 客户端，且服务器允许 SSH 密码认证。

   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Set-GpuWatchPassword.ps1 -SshAlias gpu-lab
   ```

6. 双击 **GPU空闲卡监控.exe**。`GpuWatch.AskPass.exe` 与主程序放在同一目录。修改服务器配置后，从托盘退出并重新打开程序。

密码用 Windows DPAPI 按当前 Windows 用户加密，保存在 `%LOCALAPPDATA%\GpuWatch\credentials`。换密码时重新运行设置命令即可。要清除某个别名的密码，在命令末尾加 `-Remove`。服务器地址、用户名和私钥位置由本机 SSH 配置管理。

## 怎么判断哪张卡可用

| 界面状态 | 含义 |
| --- | --- |
| 空闲 | 最新采样中 GPU 利用率 ≤ 5%、已用显存 ≤ 1 GiB，且已成功读取计算进程列表，列表中该卡的进程数为零。进入空闲状态时可触发右下角提醒。 |
| 低显存 | 利用率低，显存占用在可调上限内，但还未满足空闲条件。只列为候选。 |
| 磁盘余量 | 显示指定挂载点对当前用户可用的空间。填写独立挂载点本身；挂载丢失或不可读取时显示不可用。 |

GPU 约每 2 秒采样一次，磁盘约每 30 秒更新一次。项目名从进程工作目录或启动参数推断，能看到的细节取决于 SSH 账号的 Linux 权限。主窗口关闭后会收起到托盘；右键托盘图标可以打开主窗口、切换空闲提醒或退出程序。

## 从源码运行

Windows 上在仓库根目录执行以下命令，程序会生成在仓库根目录。按上面的步骤配置连接后即可启动。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\native\build.ps1
```

如需使用可选的本地网页版，安装 Node.js 20 或更新版本，配置好根目录的 `servers.local.json` 后运行 `node server.mjs`，打开 [本机看板](http://127.0.0.1:8787/)，按 Ctrl+C 停止。网页版使用相同的 SSH 别名配置；Windows 上使用密码模式时，先构建密码助手。网页版显示 GPU 和进程。指定挂载点的磁盘余量、托盘和空闲弹窗由 Windows 桌面版提供。

## 许可证

[MIT](LICENSE)
