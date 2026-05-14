using CommonClasses;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace CredentialsEditor;

/// <summary>
/// Редактор generic-записей Windows Credential Manager.
/// Список фильтруется по UserName, а выбранный ключ можно открыть,
/// изменить, удалить или сохранить обратно через CommonClasses.WindowsCredentialManager.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<WindowsCredentialManager.WindowsCredentialInfo> credentials = new();

    public MainWindow()
    {
        InitializeComponent();
        credentialsGrid.ItemsSource = credentials;
        RefreshCredentials();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshCredentials();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        ClearEditor();
        userNameTextBox.Text = "username";
        keyEditorPanel.Visibility = Visibility.Visible;
        statusTextBlock.Text = "Новый ключ";
        targetNameTextBox.Focus();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var targetName = targetNameTextBox.Text.Trim();
        var userName = userNameTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(targetName))
        {
            statusTextBlock.Text = "TargetName обязателен";
            return;
        }

        if (WindowsCredentialManager.WriteSecret(targetName, userName, secretTextBox.Text))
        {
            statusTextBlock.Text = "Ключ сохранен";
            RefreshCredentials(targetName);
        }
        else
        {
            statusTextBlock.Text = "Не удалось сохранить ключ";
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (credentialsGrid.SelectedItem is not WindowsCredentialManager.WindowsCredentialInfo selected)
        {
            statusTextBlock.Text = "Выберите ключ для удаления";
            return;
        }

        var confirmation = MessageBox.Show(
            $"Удалить ключ \"{selected.TargetName}\"?",
            "Подтверждение удаления",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.Yes)
            return;

        if (WindowsCredentialManager.DeleteSecret(selected.TargetName))
        {
            ClearEditor();
            RefreshCredentials();
            statusTextBlock.Text = "Ключ удален";
        }
        else
        {
            statusTextBlock.Text = "Не удалось удалить ключ";
        }
    }

    private void CredentialsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (credentialsGrid.SelectedItem is not WindowsCredentialManager.WindowsCredentialInfo selected)
        {
            deleteButton.IsEnabled = false;
            keyEditorPanel.Visibility = Visibility.Collapsed;
            return;
        }

        keyEditorPanel.Visibility = Visibility.Visible;
        targetNameTextBox.Text = selected.TargetName;
        targetNameTextBox.IsReadOnly = true;
        userNameTextBox.Text = selected.UserName;
        secretTextBox.Text = WindowsCredentialManager.ReadSecret(selected.TargetName) ?? "";
        deleteButton.IsEnabled = true;
        statusTextBlock.Text = $"Открыт {selected.TargetName}";
    }

    private void RefreshCredentials(string? selectTargetName = null)
    {
        credentials.Clear();
        foreach (var credential in WindowsCredentialManager.ListCredentialsByUserName(filterTextBox.Text))
            credentials.Add(credential);

        statusTextBlock.Text = $"Найдено: {credentials.Count}";

        if (!string.IsNullOrWhiteSpace(selectTargetName))
        {
            var item = credentials.FirstOrDefault(u => u.TargetName.Equals(selectTargetName, StringComparison.OrdinalIgnoreCase));
            if (item != null)
                credentialsGrid.SelectedItem = item;
        }
    }

    private void ClearEditor()
    {
        credentialsGrid.SelectedItem = null;
        targetNameTextBox.Text = "";
        targetNameTextBox.IsReadOnly = false;
        userNameTextBox.Text = "";
        secretTextBox.Text = "";
        deleteButton.IsEnabled = false;
        keyEditorPanel.Visibility = Visibility.Collapsed;
    }
}
