using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace GpuWatch
{
    internal static class HostTrust
    {
        private static string PinPath(string host, int port)
        {
            var target = host + "\n" + port.ToString(CultureInfo.InvariantCulture);
            using (var hash = SHA256.Create())
            {
                var id = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(target))).Replace("-", "").ToLowerInvariant();
                return Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "GpuWatch", "known_hosts", id + ".hosts");
            }
        }

        private static string FileOption(string path)
        {
            return "UserKnownHostsFile=\"" + path.Replace('\\', '/').Replace("%", "%%") + "\"";
        }

        internal static string KnownHostsOption(string host, int port)
        {
            return FileOption(PinPath(host, port));
        }

        internal static async Task<bool> EnsureAsync(Window owner, string host, int port, string user, CancellationToken token = default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();
            var destination = PinPath(host, port);
            if (File.Exists(destination)) return true; // The login probe still requires a matching key.
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await Task.Run(() => ReadNegotiatedKey(host, port, user, temporary, token), token);
                token.ThrowIfCancellationRequested();
                if (!owner.IsVisible) return false;
                var line = File.ReadLines(temporary).FirstOrDefault(s => s.Length > 0 && s[0] != '#');
                var fields = (line ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 3) throw new IOException("未能读取有效的 SSH 服务器指纹。");
                string fingerprint;
                using (var hash = SHA256.Create())
                    fingerprint = "SHA256:" + Convert.ToBase64String(hash.ComputeHash(Convert.FromBase64String(fields[2]))).TrimEnd('=');
                var endpoint = (host.Contains(":") ? "[" + host + "]" : host) + ":" + port;
                var answer = MessageBox.Show(owner, "首次连接 " + endpoint + "\n\n" + fields[1] + "\n" + fingerprint +
                    "\n\n请与服务器管理员提供的指纹核对。确认一致后，是否信任并继续登录？",
                    "确认 SSH 服务器指纹", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return false;
                try { File.Move(temporary, destination); }
                catch (IOException) { if (!File.Exists(destination) || File.ReadAllText(temporary) != File.ReadAllText(destination)) throw; }
                return true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        // OpenSSH negotiates the same host key as the real connection. Only its temporary
        // file accepts a new key; no password, agent identity or private key is offered.
        private static void ReadNegotiatedKey(string host, int port, string user, string temporary, CancellationToken token)
        {
            var args = new[] { "-F", "none", "-nT", "-o", "BatchMode=yes", "-o", "PreferredAuthentications=none",
                "-o", "StrictHostKeyChecking=accept-new", "-o", FileOption(temporary), "-o", "GlobalKnownHostsFile=none",
                "-o", "HashKnownHosts=no", "-o", "UpdateHostKeys=no", "-o", "ClearAllForwardings=yes",
                "-o", "ConnectionAttempts=1", "-o", "ConnectTimeout=6", "-p", port.ToString(CultureInfo.InvariantCulture),
                "-l", user, "--", host, "true" };
            var info = new ProcessStartInfo(SshMonitor.SshExecutable, string.Join(" ", args.Select(SshMonitor.QuoteArgument)))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
            };
            try
            {
                using (var ssh = Process.Start(info))
                using (token.Register(() => SshMonitor.Stop(ssh)))
                {
                    ssh.BeginErrorReadLine();
                    ssh.BeginOutputReadLine();
                    if (!ssh.WaitForExit(10000))
                    {
                        try { ssh.Kill(); } catch (InvalidOperationException) { }
                        ssh.WaitForExit();
                        throw new IOException("获取 SSH 指纹超时，请检查地址、端口和网络连接。");
                    }
                    token.ThrowIfCancellationRequested();
                }
            }
            catch (Win32Exception) { throw new IOException("无法启动 ssh.exe，请在 Windows 可选功能中安装 OpenSSH 客户端。"); }
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                throw new IOException("获取 SSH 指纹失败，请检查服务器地址、SSH 端口及网络连接。");
        }
    }
}
