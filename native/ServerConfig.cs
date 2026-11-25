using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace GpuWatch
{
    internal sealed class ServerDefinition
    {
        public string name { get; set; }
        public string ssh { get; set; }
        public string mount { get; set; }
        public string auth { get; set; }
    }

    internal sealed class ServerConfig
    {
        public List<ServerDefinition> servers { get; set; }

        public static List<ServerDefinition> Load(string directory)
        {
            var path = Path.Combine(directory, "servers.local.json");
            if (!File.Exists(path))
                throw new FileNotFoundException("请在程序所在目录复制 servers.example.json 为 servers.local.json，填写自己的 SSH 别名和挂载点，再打开监控。");

            ServerConfig config;
            var json = File.ReadAllText(path);
            try { config = new JavaScriptSerializer().Deserialize<ServerConfig>(json); }
            catch (ArgumentException) { throw new InvalidDataException("servers.local.json 的 JSON 格式无效，请对照 servers.example.json 检查。"); }
            catch (InvalidOperationException) { throw new InvalidDataException("servers.local.json 的 JSON 格式无效，请对照 servers.example.json 检查。"); }
            if (config == null || config.servers == null || config.servers.Count == 0)
                throw new InvalidDataException("请在 servers.local.json 的 servers 数组中配置至少一台服务器。");

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < config.servers.Count; i++)
            {
                var host = config.servers[i];
                var prefix = "servers.local.json 中第 " + (i + 1) + " 台服务器";
                if (host == null || string.IsNullOrWhiteSpace(host.name) || host.name.Any(char.IsControl))
                    throw new InvalidDataException(prefix + " 需要有效的显示名称 name。");
                host.name = host.name.Trim();
                if (!names.Add(host.name)) throw new InvalidDataException(prefix + " 的 name 重复。");
                if (host.ssh == null || !Regex.IsMatch(host.ssh, @"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z"))
                    throw new InvalidDataException(prefix + " 的 ssh 需要填写 OpenSSH Host 别名。");
                if (!aliases.Add(host.ssh)) throw new InvalidDataException(prefix + " 的 SSH 别名重复。");
                if (string.IsNullOrEmpty(host.mount) || !host.mount.StartsWith("/", StringComparison.Ordinal) || host.mount.Any(char.IsControl))
                    throw new InvalidDataException(prefix + " 的 mount 需要填写绝对挂载路径，例如 /data。");
                host.mount = host.mount.TrimEnd('/');
                if (host.mount.Length == 0) host.mount = "/";
                host.auth = host.auth ?? "key";
                if (host.auth != "key" && host.auth != "password")
                    throw new InvalidDataException(prefix + " 的 auth 需要填写 key 或 password。");
            }
            return config.servers;
        }
    }
}
