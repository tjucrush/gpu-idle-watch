using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace GpuWatch
{
    internal static class AddServerWindow
    {
        public static ServerDefinition Prompt(Window owner, string directory)
        {
            return Show(owner, directory, null);
        }

        public static ServerDefinition Edit(Window owner, string directory, ServerDefinition original)
        {
            return Show(owner, directory, original);
        }

        private static ServerDefinition Show(Window owner, string directory, ServerDefinition original)
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
            var keyMode = (RadioButton)dialog.FindName("KeyMode");
            var passwordMode = (RadioButton)dialog.FindName("PasswordMode");
            var passwordField = (StackPanel)dialog.FindName("PasswordField");
            var editor = (StackPanel)dialog.FindName("Editor");
            var save = (Button)dialog.FindName("Save");
            var feedback = (TextBlock)dialog.FindName("Feedback");
            var feedbackBox = (Border)dialog.FindName("FeedbackBox");
            var help = (TextBlock)dialog.FindName("Help");
            bool editing = original != null;
            bool alias = editing && string.IsNullOrEmpty(original.host);
            string saveLabel = editing ? "保存更改" : "验证并添加";
            save.Content = saveLabel;

            if (editing)
            {
                dialog.Title = "编辑服务器";
                ((TextBlock)dialog.FindName("Heading")).Text = dialog.Title;
                ((TextBlock)dialog.FindName("Description")).Text = original.name;
                nameBox.Text = original.name;
                mountBox.Text = original.mount;
                hostBox.Text = original.host ?? "";
                portBox.Text = original.port.ToString(CultureInfo.InvariantCulture);
                userBox.Text = original.user ?? "";
                if (alias)
                {
                    ((FrameworkElement)dialog.FindName("DirectAddress")).Visibility = Visibility.Collapsed;
                    ((FrameworkElement)dialog.FindName("UserField")).Visibility = Visibility.Collapsed;
                    ((FrameworkElement)dialog.FindName("AliasFields")).Visibility = Visibility.Visible;
                    ((TextBox)dialog.FindName("Alias")).Text = original.ssh;
                    Grid.SetColumn(passwordField, 0);
                    Grid.SetColumnSpan(passwordField, 2);
                    var loginFields = (Grid)dialog.FindName("LoginFields");
                    RoutedEventHandler showAuth = (sender, args) =>
                    {
                        bool wantsPassword = passwordMode.IsChecked == true;
                        loginFields.Visibility = wantsPassword ? Visibility.Visible : Visibility.Collapsed;
                        if (!wantsPassword) passwordBox.Clear();
                        help.Text = !wantsPassword ? "使用本机 OpenSSH 为此别名配置的密钥。"
                            : original.auth == "password" ? "密码留空即可保留当前密码。"
                            : "输入此账号的 SSH 密码，验证成功后切换登录方式。";
                    };
                    keyMode.Checked += showAuth;
                    passwordMode.Checked += showAuth;
                    (original.auth == "password" ? passwordMode : keyMode).IsChecked = true;
                }
                else if (original.auth == "password")
                    help.Text = "密码留空即可保留当前密码。更改服务器地址、端口或账号时，请重新输入密码。";
                else
                {
                    passwordField.Visibility = Visibility.Collapsed;
                    Grid.SetColumnSpan((StackPanel)dialog.FindName("UserField"), 2);
                    help.Text = "使用本机 OpenSSH 密钥登录。";
                }
            }

            ServerDefinition saved = null;
            bool busy = false;
            bool closed = false;
            var lifetime = new CancellationTokenSource();
            dialog.ContentRendered += (sender, args) => (editing ? mountBox : hostBox).Focus();
            dialog.Closed += (sender, args) =>
            {
                closed = true;
                lifetime.Cancel();
                passwordBox.Clear();
                if (!busy) lifetime.Dispose();
            };
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
                        string host = alias ? null : hostBox.Text.Trim(), user = alias ? null : userBox.Text.Trim();
                        string mount = mountBox.Text.Trim(), name = nameBox.Text.Trim();
                        string auth = alias ? (passwordMode.IsChecked == true ? "password" : "key")
                            : editing ? original.auth : "password";
                        var definition = editing
                            ? ServerStore.ValidateEdit(directory, original, host, port, user, password, mount, name, auth)
                            : ServerStore.Validate(directory, host, port, user, password, mount, name);
                        bool verify = !editing || password.Length > 0 || auth != original.auth ||
                            !string.Equals(definition.host, original.host, StringComparison.OrdinalIgnoreCase) ||
                            definition.port != original.port || definition.user != original.user;
                        if (!verify && definition.mount == original.mount && definition.name == original.name)
                        {
                            dialog.DialogResult = false;
                            return;
                        }
                        busy = true;
                        editor.IsEnabled = save.IsEnabled = false;
                        feedbackBox.Visibility = Visibility.Collapsed;
                        if (verify)
                        {
                            var token = lifetime.Token;
                            if (!alias)
                            {
                                save.Content = "正在核验主机…";
                                bool trusted = await HostTrust.EnsureAsync(dialog, definition.host, definition.port, definition.user, token);
                                if (closed) return;
                                if (!trusted) throw new InvalidOperationException("已取消主机指纹核验。核对指纹后可以重试。");
                            }
                            save.Content = "正在验证登录…";
                            if (auth == "password")
                                await ServerStore.WithProbeCredentialAsync(definition, password,
                                    candidate => SshMonitor.VerifyLoginAsync(candidate, token));
                            else
                                await SshMonitor.VerifyLoginAsync(definition, token);
                            if (closed) return;
                        }
                        saved = editing ? ServerStore.Update(directory, original, host, port, user, password, mount, name, auth)
                            : ServerStore.Add(directory, host, port, user, password, mount, name);
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
                    if (closed && busy) lifetime.Dispose();
                    busy = false;
                    if (!closed)
                    {
                        editor.IsEnabled = save.IsEnabled = true;
                        save.Content = saveLabel;
                    }
                }
                if (saved != null) dialog.DialogResult = true;
            };
            dialog.ShowDialog();
            return saved;
        }
    }
}
