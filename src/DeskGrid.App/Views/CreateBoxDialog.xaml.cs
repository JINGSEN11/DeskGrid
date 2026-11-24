using System.IO;
using System.Windows;
using DeskGrid.Core.Models;
using Microsoft.Win32;

namespace DeskGrid.App.Views;

public partial class CreateBoxDialog : Window
{
    public string BoxTitle { get; private set; } = "常用工具";
    public BoxMode SelectedMode { get; private set; } = BoxMode.Virtual;
    public string? SelectedFolderPath { get; private set; }

    public CreateBoxDialog()
    {
        InitializeComponent();
        TitleTextBox.SelectAll();
        TitleTextBox.Focus();
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (FolderPickerPanel is null) return;
        var isFolder = FolderModeRadio.IsChecked == true;
        FolderPickerPanel.Visibility = isFolder ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要映射到桌面的本地文件夹"
        };

        if (dialog.ShowDialog(this) == true)
        {
            FolderPathTextBox.Text = dialog.FolderName;
            if (string.IsNullOrWhiteSpace(TitleTextBox.Text) || TitleTextBox.Text == "常用工具")
            {
                var dirName = Path.GetFileName(dialog.FolderName.TrimEnd('\\', '/'));
                if (!string.IsNullOrWhiteSpace(dirName))
                    TitleTextBox.Text = dirName;
            }
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        var title = TitleTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            MessageBox.Show(this, "请输入盒子名称。", "DeskGrid", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (FolderModeRadio.IsChecked == true)
        {
            var folder = FolderPathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                MessageBox.Show(this, "请选择一个有效的本地文件夹。", "DeskGrid", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SelectedMode = BoxMode.FolderPortal;
            SelectedFolderPath = folder;
        }
        else
        {
            SelectedMode = BoxMode.Virtual;
            SelectedFolderPath = null;
        }

        BoxTitle = title;
        DialogResult = true;
    }
}
