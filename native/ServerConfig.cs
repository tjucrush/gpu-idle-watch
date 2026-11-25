using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
        public string credential { get; set; }
        public string host { get; set; }
        public string user { get; set; }
        public int port { get; set; }

        public ServerDefinition() { port = 22; }

        internal string CredentialAlias { get { return credential ?? ssh; } }

        internal bool Matches(ServerDefinition other)
        {
            return other != null && ssh == other.ssh && name == other.name && auth == other.auth &&
                host == other.host && user == other.user && port == other.port && mount == other.mount &&
                credential == other.credential;
        }
    }

    internal static class ServerConfig
    {
        public const string FileName = "servers.local.json";

        public static List<ServerDefinition> Load(string directory)
        {
            Dictionary<string, object> document;
            return Read(directory, out document);
        }

        internal static List<ServerDefinition> Read(string directory, out Dictionary<string, object> document)
        {
            var servers = new List<ServerDefinition>();
            string json;
            try { json = File.ReadAllText(Path.Combine(directory, FileName)); }
            catch (IOException error)
            {
                if (!(error is FileNotFoundException || error is DirectoryNotFoundException)) throw;
                document = new Dictionary<string, object> { { "servers", new object[0] } };
                return servers;
            }
            var serializer = new JavaScriptSerializer();
            try
            {
                document = serializer.DeserializeObject(json) as Dictionary<string, object>;
                object raw;
                if (document == null || !document.TryGetValue("servers", out raw) || !(raw is IList))
                    throw new InvalidDataException(FileName + " 需要包含 servers 数组。");
                foreach (var entry in (IList)raw)
                {
                    var server = serializer.ConvertToType<ServerDefinition>(entry);
                    ValidateEntry(server, servers);
                    servers.Add(server);
                }
            }
            catch (Exception error)
            {
                if (!(error is ArgumentException || error is InvalidOperationException || error is FormatException || error is OverflowException))
                    throw;
                throw new InvalidDataException(FileName + " 的 JSON 格式或字段类型无效。", error);
            }
            return servers;
        }

        internal static void ValidateEntry(ServerDefinition value, List<ServerDefinition> existing)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.name) || value.name.Any(char.IsControl))
                throw new InvalidDataException("请填写有效的服务器名称。");
            value.name = value.name.Trim();
            if (existing.Any(s => string.Equals(s.name, value.name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("服务器名称重复，请更换名称。");
            if (value.ssh == null || !Regex.IsMatch(value.ssh, @"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z"))
                throw new InvalidDataException("ssh 字段需要有效的 SSH 别名。");
            if (existing.Any(s => string.Equals(s.ssh, value.ssh, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("SSH 别名重复。");
            if (string.IsNullOrEmpty(value.mount) || value.mount[0] != '/' || value.mount.Any(char.IsControl))
                throw new InvalidDataException("请填写磁盘的绝对挂载路径，例如 /data。");
            value.mount = value.mount.TrimEnd('/');
            if (value.mount.Length == 0) value.mount = "/";
            value.auth = value.auth ?? "key";
            if (value.auth != "key" && value.auth != "password")
                throw new InvalidDataException("SSH 认证方式应为 key 或 password。");
            if (value.credential != null && !IsManagedId(value.credential))
                throw new InvalidDataException("服务器使用了无效的密码凭据编号。");
            if (value.host == null && value.user == null) return; // Existing SSH alias.
            value.host = NormalizeHost(value.host);
            if (value.port < 1 || value.port > 65535)
                throw new InvalidDataException("SSH 端口应为 1 到 65535 的整数。");
            value.user = (value.user ?? "").Trim();
            if (!Regex.IsMatch(value.user, @"\A[A-Za-z0-9_][A-Za-z0-9_.@\\-]*\$?\z"))
                throw new InvalidDataException("请输入有效的 SSH 登录账号。");
            if (existing.Any(s => string.Equals(s.host, value.host, StringComparison.OrdinalIgnoreCase) &&
                                  s.port == value.port && s.user == value.user))
                throw new InvalidDataException("这个地址、端口和账号已经添加过了。");
        }

        internal static bool IsManagedId(string id)
        {
            return id != null && Regex.IsMatch(id, @"\Agpu-[a-f0-9]{32}\z");
        }

        internal static string NormalizeHost(string input)
        {
            var host = (input ?? "").Trim();
            const string invalid = "请填写有效的 IP 地址或主机名。";
            if (host.StartsWith("[") && host.EndsWith("]") && host.Contains(":"))
                host = host.Substring(1, host.Length - 2);
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip))
            {
                if (host.Contains("%") || (ip.AddressFamily == AddressFamily.InterNetwork && ip.ToString() != host))
                    throw new InvalidDataException(invalid);
                return ip.ToString();
            }
            if (host.Length > 253 || Regex.IsMatch(host, @"\A[0-9.]*\z") ||
                !Regex.IsMatch(host, @"\A(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)*[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.?\z"))
                throw new InvalidDataException(invalid);
            return host.ToLowerInvariant();
        }
    }
}
