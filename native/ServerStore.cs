using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace GpuWatch
{
    internal static class ServerStore
    {
        public static ServerDefinition Validate(string directory, string host, int port, string user,
            SecureString password, string mount, string name)
        {
            return Prepare(ServerConfig.Load(directory), host, port, user, password, mount, name);
        }

        public static ServerDefinition Add(string directory, string host, int port, string user,
            SecureString password, string mount, string name)
        {
            using (AcquireConfigLock(directory))
            {
                Dictionary<string, object> document;
                var existing = ServerConfig.Read(directory, out document);
                var server = Prepare(existing, host, port, user, password, mount, name);
                // Keep extension fields on existing records and at the top level.
                var entries = ((IList)document["servers"]).Cast<object>().ToList();
                entries.Add(server);
                document["servers"] = entries;
                var json = new JavaScriptSerializer().Serialize(document);
                var configPath = Path.Combine(directory, ServerConfig.FileName);
                var temporary = Path.Combine(directory, "servers." + Guid.NewGuid().ToString("N") + ".local.json");
                var credentialPath = WriteNewCredential(server.ssh, password);
                try
                {
                    File.WriteAllText(temporary, json, new UTF8Encoding(false));
                    if (File.Exists(configPath)) File.Replace(temporary, configPath, null);
                    else File.Move(temporary, configPath);
                }
                catch
                {
                    File.Delete(credentialPath);
                    throw;
                }
                finally { File.Delete(temporary); }
                return server;
            }
        }

        public static async Task WithProbeCredentialAsync(ServerDefinition validated, SecureString password,
            Func<ServerDefinition, Task> probe)
        {
            var temporary = new ServerDefinition
            {
                name = validated.name, ssh = NewCredentialId(), auth = "password",
                host = validated.host, port = validated.port, user = validated.user, mount = validated.mount
            };
            var path = WriteNewCredential(temporary.ssh, password);
            try { await probe(temporary).ConfigureAwait(false); }
            finally { File.Delete(path); }
        }

        private static ServerDefinition Prepare(List<ServerDefinition> existing, string host, int port,
            string user, SecureString password, string mount, string name)
        {
            if (password == null || password.Length == 0) throw new InvalidDataException("请输入 SSH 登录密码。");
            host = ServerConfig.NormalizeHost(host);
            if (string.IsNullOrWhiteSpace(name))
            {
                var label = port == 22 ? host :
                    (host.Contains(":") ? "[" + host + "]" : host) + ":" + port.ToString(CultureInfo.InvariantCulture);
                name = label;
                var suffix = 2;
                while (existing.Any(s => string.Equals(s.name, name, StringComparison.OrdinalIgnoreCase)))
                    name = label + " (" + (suffix++).ToString(CultureInfo.InvariantCulture) + ")";
            }
            var server = new ServerDefinition
            {
                name = name, ssh = NewCredentialId(), host = host,
                user = user, port = port, mount = mount, auth = "password"
            };
            ServerConfig.ValidateEntry(server, existing);
            return server;
        }

        private static FileStream AcquireConfigLock(string directory)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "servers.lock.local.json");
            var timer = Stopwatch.StartNew();
            while (true)
            {
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
                catch (IOException error)
                {
                    if ((error.HResult & 0xffff) != 32 || timer.ElapsedMilliseconds >= 3000) throw;
                    Thread.Sleep(30);
                }
            }
        }

        private static string NewCredentialId() { return "gpu-" + Guid.NewGuid().ToString("N"); }

        private static string WriteNewCredential(string id, SecureString password)
        {
            var appData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrWhiteSpace(appData) || !Path.IsPathRooted(appData))
                throw new IOException("无法定位当前 Windows 用户的本地数据目录。");
            var folder = Directory.CreateDirectory(Path.Combine(appData, "GpuWatch", "credentials"));
            var path = Path.Combine(folder.FullName, "ssh-" + id + ".dpapi");
            var chars = new char[password.Length];
            byte[] bytes = null;
            var pointer = IntPtr.Zero;
            byte[] encrypted;
            try
            {
                pointer = Marshal.SecureStringToGlobalAllocUnicode(password);
                Marshal.Copy(pointer, chars, 0, chars.Length);
                if (chars.Any(c => c == '\0' || c == '\r' || c == '\n'))
                    throw new InvalidDataException("SSH 密码不能包含换行或空字符。");
                bytes = new UTF8Encoding(false, true).GetBytes(chars);
                var entropy = Encoding.UTF8.GetBytes("GpuWatch SSH password v1:" + id);
                encrypted = ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser);
            }
            finally
            {
                if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
                Array.Clear(chars, 0, chars.Length);
                if (pointer != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(pointer);
            }
            // CreateNew ensures that rollback can never delete a pre-existing credential.
            var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            try { using (output) { output.Write(encrypted, 0, encrypted.Length); output.Flush(true); } }
            catch { File.Delete(path); throw; }
            return path;
        }
    }
}
