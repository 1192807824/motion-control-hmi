using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ControlHub.Views.Dialogs;

public partial class LoginDialog : Window
{
    private static readonly LoginAccount[] TestAccounts =
    [
        new("operator", "operator123", "操作员"),
        new("engineer", "engineer123", "工程师"),
        new("admin", "admin123", "管理员")
    ];

    public LoginDialog(string? userName = null, string? role = null)
    {
        InitializeComponent();
        UserNameTextBox.Text = userName ?? "";
        SelectRole(role);
        Loaded += (_, _) => UserNameTextBox.Focus();
    }

    public string LoginUserName { get; private set; } = "";
    public string LoginRole { get; private set; } = "";

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        TryLogin();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void LoginInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TryLogin();
        }
    }

    private void UserNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UserNamePlaceholder.Visibility = string.IsNullOrEmpty(UserNameTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        HideLoginError();
    }

    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        PasswordPlaceholder.Visibility = string.IsNullOrEmpty(PasswordInput.Password)
            ? Visibility.Visible
            : Visibility.Collapsed;
        HideLoginError();
    }

    private void RoleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        HideLoginError();
    }

    private void TryLogin()
    {
        var selectedRole = (RoleComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var account = TestAccounts.FirstOrDefault(item =>
            item.UserName == UserNameTextBox.Text.Trim()
            && item.Password == PasswordInput.Password
            && item.Role == selectedRole);

        if (account is null)
        {
            LoginErrorText.Text = "用户名、密码或权限级别不匹配。";
            LoginErrorText.Visibility = Visibility.Visible;
            PasswordInput.SelectAll();
            PasswordInput.Focus();
            return;
        }

        LoginUserName = account.UserName;
        LoginRole = account.Role;
        DialogResult = true;
    }

    private void SelectRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return;
        }

        foreach (var item in RoleComboBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Content?.ToString() == role)
            {
                RoleComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void HideLoginError()
    {
        if (LoginErrorText is not null)
        {
            LoginErrorText.Visibility = Visibility.Collapsed;
        }
    }

    private sealed record LoginAccount(string UserName, string Password, string Role);
}
