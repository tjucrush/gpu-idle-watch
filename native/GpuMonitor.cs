using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace GpuWatch
{
    internal sealed class Gpu
    {
        public int index { get; set; }
        public string name { get; set; }
        public double? memoryUsed { get; set; }
        public double? memoryTotal { get; set; }
        public double? utilization { get; set; }
        public double? temperature { get; set; }
        public double? power { get; set; }
    }

    internal sealed class GpuProcess
    {
        public int gpu { get; set; }
        public int pid { get; set; }
        public string user { get; set; }
        public string program { get; set; }
        public double? memoryUsed { get; set; }
        public double? runningSeconds { get; set; }
        public string project { get; set; }
        public string projectSource { get; set; }
    }

    internal sealed class DiskVolume
    {
        public string mount { get; set; }
        public long availableBytes { get; set; }
        public long totalBytes { get; set; }
    }

    internal sealed class Sample
    {
        public List<Gpu> gpus { get; set; }
        public List<GpuProcess> processes { get; set; }
        public List<DiskVolume> disks { get; set; }
        public string diskStatus { get; set; }
        public long? diskSampledAt { get; set; }
        public long? diskAgeSeconds { get; set; }
        public string processStatus { get; set; }
        public string error { get; set; }
    }

    internal enum ConnectionState { Connecting, Online, Offline }
    internal enum CardState { Idle, LowMemory, Active, Occupied, Unavailable }

    internal static class Availability
    {
        public const int IdleMiB = 1024;
        public const int MaxUtilization = 5;

        public static CardState Classify(Gpu gpu, bool live, bool processListKnown, int processCount, int lowMemoryGiB)
        {
            if (!live || !gpu.memoryUsed.HasValue || !gpu.memoryTotal.HasValue ||
                gpu.memoryTotal <= 0 || !gpu.utilization.HasValue)
            {
                return CardState.Unavailable;
            }
            if (gpu.utilization > MaxUtilization) return CardState.Active;
            if (gpu.memoryUsed >= gpu.memoryTotal) return CardState.Occupied;
            if (gpu.memoryUsed <= IdleMiB && processListKnown && processCount == 0) return CardState.Idle;
            return gpu.memoryUsed <= lowMemoryGiB * 1024 ? CardState.LowMemory : CardState.Occupied;
        }
    }

    internal sealed class HostReading
    {
        public string Id;
        public ConnectionState State;
        public Sample Data;
        public DateTime? SampledAt;
        public string Issue;
    }

    internal sealed class SshMonitor
    {
        public const int IntervalMs = 2000;
        private const string PacketPrefix = "GPU_SAMPLE ";
        private readonly ServerDefinition host;
        private readonly string command;

        private static string QuoteRemote(string value)
        {
            return "'" + value.Replace("'", "'\"'\"'") + "'";
        }

        public SshMonitor(ServerDefinition host, string collector)
        {
            this.host = host;
            command = "env LC_ALL=C python3 -B -u -c " + QuoteRemote(collector.Replace("\r\n", "\n")) + " " + QuoteRemote(host.mount);
        }

        public async Task RunAsync(Action<HostReading> publish, CancellationToken token)
        {
            var parser = new JavaScriptSerializer { MaxJsonLength = 1000000 };
            Sample previous = null;
            DateTime? sampledAt = null;
            var retryDelay = 3500;
            Action<ConnectionState, string> emit = (state, issue) => publish(new HostReading
            {
                Id = host.name, State = state, Data = previous, SampledAt = sampledAt, Issue = issue,
            });

            while (!token.IsCancellationRequested)
            {
                emit(ConnectionState.Connecting, null);
                using (var ssh = new Process { StartInfo = BuildStartInfo() })
                {
                    var started = false;
                    Task<string> errors = null;
                    try
                    {
                        if (host.auth == "password" && !File.Exists(ssh.StartInfo.EnvironmentVariables["SSH_ASKPASS"]))
                            throw new FileNotFoundException("缺少 GpuWatch.AskPass.exe");
                        started = ssh.Start();
                        if (!started) throw new IOException("无法启动 SSH 客户端");
                        errors = ssh.StandardError.ReadToEndAsync();
                        ssh.StandardInput.NewLine = "\n";
                        using (token.Register(() => Stop(ssh)))
                        {
                            var first = true;
                            while (!token.IsCancellationRequested)
                            {
                                var elapsed = Stopwatch.StartNew();
                                await ssh.StandardInput.WriteLineAsync("sample").ConfigureAwait(false);
                                await ssh.StandardInput.FlushAsync().ConfigureAwait(false);
                                var packet = await ReadPacket(ssh.StandardOutput, first ? 16000 : 12000, token).ConfigureAwait(false);
                                var sample = parser.Deserialize<Sample>(packet);
                                string gpuIssue = null;
                                if (sample != null && sample.disks != null && (sample.error == "no_gpus" || sample.error == "gpu_query"))
                                {
                                    gpuIssue = sample.error == "no_gpus" ? "未发现显卡" : "显卡查询失败";
                                    sample.gpus = new List<Gpu>();
                                    sample.processes = new List<GpuProcess>();
                                }
                                if (sample == null || sample.gpus == null || sample.processes == null)
                                {
                                    throw new IOException(sample != null && sample.error == "no_gpus" ?
                                        "服务器没有可读的 NVIDIA 显卡" : "nvidia-smi 查询失败");
                                }
                                // Use the sample age so host clock skew cannot make fresh disks look stale.
                                if (sample.diskAgeSeconds.HasValue)
                                    sample.diskSampledAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - sample.diskAgeSeconds.Value;
                                previous = sample;
                                sampledAt = DateTime.Now;
                                retryDelay = 3500;
                                emit(ConnectionState.Online, gpuIssue);
                                first = false;
                                await Task.Delay(Math.Max(100, IntervalMs - (int)elapsed.ElapsedMilliseconds), token).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        if (!token.IsCancellationRequested)
                        {
                            var diagnostic = errors != null && errors.Status == TaskStatus.RanToCompletion ? errors.Result : "";
                            emit(ConnectionState.Offline, Explain(error, diagnostic));
                        }
                    }
                    finally { if (started) Stop(ssh); }
                }
                if (token.IsCancellationRequested) break;
                try { await Task.Delay(retryDelay, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                retryDelay = Math.Min(retryDelay * 2, 30000);
            }
        }

        private ProcessStartInfo BuildStartInfo()
        {
            var args = new List<string>
            {
                "-T", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=6",
                "-o", "ConnectionAttempts=1", "-o", "ClearAllForwardings=yes", "-o", "ServerAliveInterval=8",
                "-o", "ServerAliveCountMax=2", "-o", "LogLevel=ERROR"
            };
            if (host.auth == "password")
                args.AddRange(new[] { "-o", "BatchMode=no", "-o", "PreferredAuthentications=password",
                    "-o", "PasswordAuthentication=yes", "-o", "NumberOfPasswordPrompts=1" });
            else args.AddRange(new[] { "-o", "BatchMode=yes" });
            args.Add(host.ssh);
            args.Add(command);
            var start = new ProcessStartInfo("ssh.exe", string.Join(" ", args.Select(QuoteArgument)))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            if (host.auth == "password")
            {
                start.EnvironmentVariables["SSH_ASKPASS"] = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GpuWatch.AskPass.exe");
                start.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "force";
                start.EnvironmentVariables["DISPLAY"] = "gpu-watch:0";
                start.EnvironmentVariables["GPUWATCH_SSH_ALIAS"] = host.ssh;
            }
            return start;
        }

        private static async Task<string> ReadPacket(StreamReader stream, int timeout, CancellationToken token)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var expiry = Task.Delay(timeout, deadline.Token);
                while (true)
                {
                    var read = stream.ReadLineAsync();
                    if (await Task.WhenAny(read, expiry).ConfigureAwait(false) != read)
                    {
                        token.ThrowIfCancellationRequested();
                        throw new TimeoutException();
                    }
                    var line = await read.ConfigureAwait(false);
                    if (line == null) throw new EndOfStreamException();
                    if (line.StartsWith(PacketPrefix, StringComparison.Ordinal))
                    {
                        deadline.Cancel();
                        return line.Substring(PacketPrefix.Length);
                    }
                }
            }
        }

        private static string QuoteArgument(string argument)
        {
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (char character in argument)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        private static void Stop(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private static string Explain(Exception error, string diagnostic)
        {
            if (error is System.ComponentModel.Win32Exception) return "本机 OpenSSH 客户端不可用";
            if (error is TimeoutException) return "读取显卡超时";
            var message = diagnostic.ToLowerInvariant();
            if (message.Contains("host key") || message.Contains("identification has changed")) return "SSH 主机密钥校验失败";
            if (error is FileNotFoundException) return error.Message;
            if (message.Contains("permission denied")) return "SSH 认证失败，请检查连接配置和凭据";
            if (message.Contains("python3") && message.Contains("not found")) return "服务器缺少 Python 3";
            if (message.Contains("timed out") || message.Contains("unreachable") || message.Contains("refused")) return "无法访问 SSH 端口";
            if (error is IOException && (error.Message.Contains("nvidia-smi") || error.Message.Contains("NVIDIA"))) return error.Message;
            return "采样连接中断";
        }
    }
}
