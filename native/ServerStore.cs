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
                Save(directory, document, server.CredentialAlias, password, null);
                return server;
            }
        }

        public static ServerDefinition ValidateEdit(string directory, ServerDefinition original,
            string host, int port, string user, SecureString password, string mount, string name, string auth)
        {
            var existing = ServerConfig.Load(directory);
            var index = FindOriginal(existing, original);
            return PrepareEdit(existing, index, host, port, user, password, mount, name, auth);
        }

        public static ServerDefinition Update(string directory, ServerDefinition original, string host, int port,
            string user, SecureString password, string mount, string name, string auth)
        {
            using (AcquireConfigLock(directory))
            {
                Dictionary<string, object> document;
                var existing = ServerConfig.Read(directory, out document);
                var index = FindOriginal(existing, original);
                var updated = PrepareEdit(existing, index, host, port, user, password, mount, name, auth);
                var entry = (Dictionary<string, object>)((IList)document["servers"])[index];
                // Edit the original JSON object: unrelated and future properties survive a save.
                SetProperty(entry, "name", updated.name);
                SetProperty(entry, "mount", updated.mount);
                SetProperty(entry, "auth", updated.auth);
                SetProperty(entry, "credential", updated.credential);
                if (updated.host != null)
                {
                    SetProperty(entry, "host", updated.host);
                    SetProperty(entry, "port", updated.port);
                    SetProperty(entry, "user", updated.user);
                }
                existing[index] = updated;
                var retire = RetiredCredential(directory, original, existing);
                Save(directory, document, updated.CredentialAlias, password, retire);
                return updated;
            }
        }

        public static void Remove(string directory, ServerDefinition original)
        {
            using (AcquireConfigLock(directory))
            {
                Dictionary<string, object> document;
                var existing = ServerConfig.Read(directory, out document);
                var index = FindOriginal(existing, original);
                var entries = ((IList)document["servers"]).Cast<object>().ToList();
                entries.RemoveAt(index);
                document["servers"] = entries;
                existing.RemoveAt(index);
                Save(directory, document, null, null, RetiredCredential(directory, original, existing));
            }
        }

        private static int FindOriginal(List<ServerDefinition> existing, ServerDefinition original)
        {
            var index = existing.FindIndex(s => String.Equals(s.ssh, original.ssh, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || !existing[index].Matches(original))
                throw new InvalidDataException("服务器配置已被修改或删除。请重新打开监控后重试。");
            return index;
        }

        private static ServerDefinition PrepareEdit(List<ServerDefinition> existing, int index, string host,
            int port, string user, SecureString password, string mount, string name, string auth)
        {
            var original = existing[index];
            bool replacing = password != null && password.Length != 0;
            var candidate = new ServerDefinition
            {
                name = String.IsNullOrWhiteSpace(name) ? original.name : name, mount = mount,
                ssh = original.ssh, host = host, port = port, user = user, auth = auth ?? original.auth,
                credential = original.credential
            };
            if (original.host != null && host == null)
                throw new InvalidDataException("请填写服务器的 IP 或主机名。");
            ServerConfig.ValidateEntry(candidate, existing.Where((s, i) => i != index).ToList());
            if (candidate.auth == "key")
            {
                if (replacing) throw new InvalidDataException("使用 SSH 密钥时，请留空密码栏。");
                candidate.credential = null;
            }
            else if (replacing) candidate.credential = NewCredentialId();
            else if (original.auth != "password" || candidate.host != original.host ||
                     candidate.port != original.port || candidate.user != original.user)
                throw new InvalidDataException("登录目标或认证方式已变更，请输入此服务器的 SSH 密码。");
            return candidate;
        }

        private static void SetProperty(Dictionary<string, object> entry, string key, object value)
        {
            foreach (var old in entry.Keys.Where(k => String.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToArray())
                entry.Remove(old);
            if (value != null) entry[key] = value;
        }

        private static string RetiredCredential(string directory, ServerDefinition old, List<ServerDefinition> remaining)
        {
            // Only reclaim storage created by this GUI. A named SSH alias may be used
            // by another portable installation or by Set-GpuWatchPassword.ps1.
            var id = old.CredentialAlias;
            if (old.auth != "password" || !ServerConfig.IsManagedId(id) || (old.credential == null && old.host == null))
                return null;
            Func<ServerDefinition, bool> references = s => s.auth == "password" &&
                String.Equals(s.CredentialAlias, id, StringComparison.OrdinalIgnoreCase);
            if (remaining.Any(references)) return null;
            var candidates = new[] { AppDomain.CurrentDomain.BaseDirectory, Path.Combine(AppData(), "GpuWatch") };
            var current = Path.GetFullPath(Path.Combine(directory, ServerConfig.FileName));
            foreach (var other in candidates)
            {
                var otherPath = Path.GetFullPath(Path.Combine(other, ServerConfig.FileName));
                if (String.Equals(otherPath, current, StringComparison.OrdinalIgnoreCase)) continue;
                try { if (ServerConfig.Load(other).Any(references)) return null; }
                catch (Exception error)
                {
                    if (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException) return null;
                    throw;
                }
            }
            var path = Path.Combine(AppData(), "GpuWatch", "credentials", "ssh-" + id + ".dpapi");
            return File.Exists(path) ? path : null;
        }

        private static void Save(string directory, Dictionary<string, object> document,
            string id, SecureString password, string retired)
        {
            var json = new JavaScriptSerializer().Serialize(document);
            var config = Path.Combine(directory, ServerConfig.FileName);
            var temporary = Path.Combine(directory, "servers." + Guid.NewGuid().ToString("N") + ".local.json");
            var backup = retired == null ? null : temporary + ".backup.local.json";
            string added = null;
            bool committed = false;
            try
            {
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                if (password != null && password.Length > 0) added = WriteNewCredential(id, password);
                if (File.Exists(config)) File.Replace(temporary, config, backup);
                else File.Move(temporary, config);
                committed = true;
                if (retired != null)
                {
                    try { File.Delete(retired); }
                    catch (Exception error)
                    {
                        if (!(error is IOException || error is UnauthorizedAccessException)) throw;
                        // A locked credential must not leave the UI claiming removal while it still exists.
                        File.Replace(backup, config, null);
                        committed = false;
                        throw new IOException("无法清除旧密码，已保留原配置，请稍后重试。", error);
                    }
                }
            }
            finally
            {
                if (!committed && added != null) File.Delete(added);
                CleanupTemporary(temporary);
                if (backup != null && (!committed || !File.Exists(retired))) CleanupTemporary(backup);
            }
        }

        private static void CleanupTemporary(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public static async Task WithProbeCredentialAsync(ServerDefinition validated, SecureString password,
            Func<ServerDefinition, Task> probe)
        {
            var temporary = new ServerDefinition
            {
                name = validated.name, ssh = validated.ssh, credential = NewCredentialId(), auth = "password",
                host = validated.host, port = validated.port, user = validated.user, mount = validated.mount
            };
            var path = WriteNewCredential(temporary.CredentialAlias, password);
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

        private static string AppData()
        {
            var appData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrWhiteSpace(appData) || !Path.IsPathRooted(appData))
                throw new IOException("无法定位当前 Windows 用户的本地数据目录。");
            return appData;
        }

        private static string WriteNewCredential(string id, SecureString password)
        {
            var folder = Directory.CreateDirectory(Path.Combine(AppData(), "GpuWatch", "credentials"));
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
