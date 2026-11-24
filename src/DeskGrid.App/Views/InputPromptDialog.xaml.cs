using System.Windows;

namespace DeskGrid.App.Views;

public partial class InputPromptDialog : Window
{
    public string ResultText { get; private set; } = string.Empty;

    public InputPromptDialog(string title, string prompt, string initialValue)
    {
        InitializeComponent();
        Title = title;
        PromptLabel.Text = prompt;
        InputBox.Text = initialValue;
        InputBox.SelectAll();
        InputBox.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var val = InputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(val))
            return;

        ResultText = val;
        DialogResult = true;
    }
}
