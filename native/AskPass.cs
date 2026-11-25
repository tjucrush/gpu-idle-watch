using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GpuWatch
{
    // OpenSSH starts a second copy of the desktop EXE with its prompt as the only argument.
    internal static class AskPass
    {
        public static bool IsRequested
        {
            get { return Environment.GetEnvironmentVariable("GPUWATCH_MODE") == "askpass"; }
        }

        public static int Reply(string[] args)
        {
            var alias = Environment.GetEnvironmentVariable("GPUWATCH_SSH_ALIAS");
            var promptType = Environment.GetEnvironmentVariable("SSH_ASKPASS_PROMPT");
            if (args.Length != 1 || !String.IsNullOrEmpty(promptType) || !Console.IsOutputRedirected ||
                !Regex.IsMatch(args[0], @"\A(?:[^@\s]+@[^\s]+'s password|[Pp]assword):[ \t]*\z") ||
                alias == null || !Regex.IsMatch(alias, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z"))
                return 1;

            var appData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (String.IsNullOrEmpty(appData) || !Path.IsPathRooted(appData)) return 1;
            alias = alias.ToLowerInvariant();
            var path = Path.Combine(appData, "GpuWatch", "credentials", "ssh-" + alias + ".dpapi");
            var entropy = Encoding.UTF8.GetBytes("GpuWatch SSH password v1:" + alias);
            byte[] password = null;
            try
            {
                password = ProtectedData.Unprotect(File.ReadAllBytes(path), entropy, DataProtectionScope.CurrentUser);
                if (password.Length == 0 || Array.IndexOf(password, (byte)0) >= 0 ||
                    Array.IndexOf(password, (byte)'\r') >= 0 || Array.IndexOf(password, (byte)'\n') >= 0)
                    return 1;
                using (var pipe = Console.OpenStandardOutput())
                {
                    pipe.Write(password, 0, password.Length);
                    pipe.WriteByte((byte)'\n');
                    pipe.Flush();
                }
                return 0;
            }
            catch (IOException) { return 1; }
            catch (UnauthorizedAccessException) { return 1; }
            catch (CryptographicException) { return 1; }
            finally { if (password != null) Array.Clear(password, 0, password.Length); }
        }
    }
}
