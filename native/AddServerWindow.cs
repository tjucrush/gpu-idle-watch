using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace GpuWatch
{
    internal static class AddServerWindow
    {
        public static ServerDefinition Prompt(Window owner, string directory)
        {
            Window dialog;
            using (var resource = typeof(AddServerWindow).Assembly.GetManifestResourceStream("AddServerWindow.xaml"))
                dialog = (Window)XamlReader.Load(resource);
            dialog.Owner = owner;
            dialog.Icon = owner.Icon;
            dialog.Height = Math.Min(dialog.Height, SystemParameters.WorkArea.Height);

            var hostBox = (TextBox)dialog.FindName("Host");
            var portBox = (TextBox)dialog.FindName("Port");
            var userBox = (TextBox)dialog.FindName("User");
            var passwordBox = (PasswordBox)dialog.FindName("Password");
            var mountBox = (TextBox)dialog.FindName("Mount");
            var nameBox = (TextBox)dialog.FindName("DisplayName");
            var editor = (StackPanel)dialog.FindName("Editor");
            var save = (Button)dialog.FindName("Save");
            var feedback = (TextBlock)dialog.FindName("Feedback");
            var feedbackBox = (Border)dialog.FindName("FeedbackBox");

            ServerDefinition saved = null;
            bool busy = false;
            bool closed = false;
            dialog.ContentRendered += (sender, args) => hostBox.Focus();
            dialog.Closed += (sender, args) => { closed = true; passwordBox.Clear(); };
            save.Click += async (sender, args) =>
            {
                if (busy) return;
                try
                {
                    int port;
                    if (!int.TryParse(portBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
                        || port < 1 || port > 65535)
                    {
                        portBox.Focus();
                        portBox.SelectAll();
                        throw new ArgumentException("SSH 端口需要填写 1 到 65535 之间的整数。");
                    }
                    using (var password = passwordBox.SecurePassword)
                    {
                        string host = hostBox.Text.Trim(), user = userBox.Text.Trim();
                        string mount = mountBox.Text.Trim(), name = nameBox.Text.Trim();
                        var definition = ServerStore.Validate(directory, host, port, user, password, mount, name);
                        busy = true;
                        editor.IsEnabled = save.IsEnabled = false;
                        feedbackBox.Visibility = Visibility.Collapsed;
                        save.Content = "正在核验主机…";
                        bool trusted = await HostTrust.EnsureAsync(dialog, definition.host, definition.port, definition.user);
                        if (closed) return;
                        if (!trusted)
                        {
                            feedback.Text = "已取消主机核验。核对服务器指纹后可以重新添加。";
                            feedbackBox.Visibility = Visibility.Visible;
                            return;
                        }
                        save.Content = "正在验证登录…";
                        await ServerStore.WithProbeCredentialAsync(definition, password, SshMonitor.VerifyLoginAsync);
                        if (closed) return;
                        saved = ServerStore.Add(directory, host, port, user, password, mount, name);
                    }
                }
                catch (Exception ex)
                {
                    if (!closed)
                    {
                        feedback.Text = ex.Message;
                        feedbackBox.Visibility = Visibility.Visible;
                    }
                }
                finally
                {
                    busy = false;
                    if (!closed)
                    {
                        editor.IsEnabled = save.IsEnabled = true;
                        save.Content = "验证并添加";
                    }
                }
                if (saved != null) dialog.DialogResult = true;
            };
            dialog.ShowDialog();
            return saved;
        }
    }
}
