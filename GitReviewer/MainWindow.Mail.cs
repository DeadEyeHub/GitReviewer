using System.Windows;
using System.Windows.Controls;
using GitReviewer.Services;
using Controls = System.Windows.Controls;
using Localization = GitReviewer.Services.Localization;

namespace GitReviewer;

public partial class MainWindow
{
    private Controls.TextBlock? _mailCounts;
    private void BuildMailPanel()
    {
        if (_mail is null) return;
        MailTab.Header = Localization.Text("Mail", "Почта");
        MailPanel.Children.Clear();
        var settings = _mail.Settings;
        var enabled = new Controls.CheckBox { Content = Localization.Text("Enable email notifications", "Включить почтовые уведомления"),
            IsChecked = settings.Enabled, Margin = new Thickness(0, 0, 0, 12) };
        MailPanel.Children.Add(enabled);
        var toAuthor = new Controls.CheckBox { Content = Localization.Text("Also send to the commit author", "Отправлять также автору коммита"), IsChecked = settings.SendToAuthor, Margin = new Thickness(0,0,0,8),
            ToolTip = Localization.Text("Uses the email from Git commit metadata. Enable only for trusted repositories: reports may be sent outside your organization.", "Адрес берётся из Git-коммита. Включайте только для доверенных репозиториев: отчёт может уйти за пределы организации.") };
        var noBugs = new Controls.CheckBox { Content = Localization.Text("Send even when no bugs are found", "Отправлять и при отсутствии ошибок"), IsChecked = settings.SendWithoutBugs, Margin = new Thickness(0,0,0,8),
            ToolTip = Localization.Text("Successful reviews only, including empty diffs. Failed reviews and timeouts do not send reports.", "Только успешные проверки, включая пустой diff. Ошибка проверки или таймаут не приводят к отправке.") };
        MailPanel.Children.Add(toAuthor); MailPanel.Children.Add(noBugs);
        Controls.TextBox Field(string en, string ru, string value)
        {
            MailPanel.Children.Add(new Controls.TextBlock { Text = Localization.Text(en, ru), Margin = new Thickness(0, 6, 0, 3) });
            var box = new Controls.TextBox { Text = value, MaxWidth = 600, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Width = 420 };
            MailPanel.Children.Add(box); return box;
        }
        var host = Field("SMTP server", "SMTP-сервер", settings.Host);
        var port = Field("Port", "Порт", settings.Port.ToString());
        MailPanel.Children.Add(new Controls.TextBlock { Text = Localization.Text("Connection security", "Защита соединения"), Margin = new Thickness(0, 6, 0, 3) });
        var security = new Controls.ComboBox { ItemsSource = new[] { "None", "StartTls", "SslOnConnect" }, SelectedItem = settings.Security,
            Width = 220, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        MailPanel.Children.Add(security);
        MailPanel.Children.Add(new Controls.TextBlock { Text = Localization.Text(
            "None sends credentials and reports without encryption. Use only on a trusted test network. StartTls requires TLS; SslOnConnect starts with TLS.",
            "None передаёт пароль и отчёты без шифрования. Только для доверенной тестовой сети. StartTls требует TLS; SslOnConnect использует TLS сразу."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) });
        var user = Field("Username (empty = no authentication)", "Логин (пусто = без авторизации)", settings.Username);
        MailPanel.Children.Add(new Controls.TextBlock { Text = Localization.Text("Password (encrypted for this Windows user)", "Пароль (шифруется для текущего пользователя Windows)"), Margin = new Thickness(0, 6, 0, 3) });
        var password = new Controls.PasswordBox { Width = 420, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        try { password.Password = settings.GetPassword(); } catch { AppendLog("Mail password cannot be decrypted. Enter it again."); }
        MailPanel.Children.Add(password);
        var from = Field("From (Name <email@example.com>)", "Отправитель (Имя <email@example.com>)", settings.From);
        var recipients = Field("Recipients (separate with ;)", "Получатели (через ;)", settings.Recipients);
        var buttons = new Controls.WrapPanel { Margin = new Thickness(0, 12, 0, 8) };
        var save = new Controls.Button { Content = Localization.Text("Save", "Сохранить"), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        var test = new Controls.Button { Content = Localization.Text("Send test email", "Отправить тестовое письмо"), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        var retry = new Controls.Button { Content = Localization.Text("Retry failed mail", "Повторить неотправленные"), Padding = new Thickness(12, 6, 12, 6) };
        buttons.Children.Add(save); buttons.Children.Add(test); buttons.Children.Add(retry);
        MailPanel.Children.Add(buttons);
        var status = new Controls.TextBlock { TextWrapping = TextWrapping.Wrap, Text = _mail.LoadError ?? "" };
        MailPanel.Children.Add(status);
        _mailCounts = new Controls.TextBlock { Margin = new Thickness(0, 8, 0, 0) };
        MailPanel.Children.Add(_mailCounts);
        MailSettings Read()
        {
            if (!int.TryParse(port.Text, out var number)) throw new InvalidOperationException("Invalid SMTP port.");
            var value = new MailSettings { Enabled = enabled.IsChecked == true, SendToAuthor = toAuthor.IsChecked == true, SendWithoutBugs = noBugs.IsChecked == true, Host = host.Text.Trim(), Port = number,
                Security = security.SelectedItem as string ?? "StartTls", Username = user.Text.Trim(), From = from.Text.Trim(), Recipients = recipients.Text.Trim() };
            value.SetPassword(password.Password);
            return value;
        }
        bool ConfirmInsecure(MailSettings value) => value.Security != "None" || System.Windows.MessageBox.Show(
            Localization.Text("This connection sends credentials and reports without encryption. Continue?", "Соединение передаёт пароль и отчёты без шифрования. Продолжить?"),
            "SMTP", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        save.Click += (_, _) =>
        {
            try
            {
                var value = Read();
                if (value.Enabled && !ConfirmInsecure(value)) return;
                _mail.SaveSettings(value);
                status.Text = Localization.Text("Mail settings saved.", "Настройки почты сохранены.");
            }
            catch (Exception e) { status.Text = "Mail settings error (" + e.GetType().Name + "). Check host, port, addresses and credentials."; }
        };
        test.Click += async (_, _) =>
        {
            try
            {
                var value = Read();
                if (!ConfirmInsecure(value)) return;
                test.IsEnabled = false;
                status.Text = Localization.Text("Sending test email…", "Отправляется тестовое письмо…");
                await _mail.TestAsync(value, CancellationToken.None);
                status.Text = Localization.Text("Settings saved; test email accepted by SMTP server.", "Настройки сохранены; тестовое письмо принято SMTP-сервером.");
                AppendLog(status.Text);
            }
            catch (Exception e) { status.Text = "SMTP test failed (" + e.GetType().Name + ")."; AppendLog(status.Text); }
            finally { test.IsEnabled = true; }
        };
        retry.Click += (_, _) =>
        {
            try { _mail.RetryFailed(); status.Text = Localization.Text("Failed mail queued again. Enable mail to send.", "Неотправленные письма возвращены в очередь. Включите почту для отправки."); }
            catch (Exception e) { status.Text = "Mail queue error (" + e.GetType().Name + ")."; }
        };
    }
}
