using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DriveMigrator.App.Views;

public partial class ConfirmDialog : Window
{
    // Required by the XAML loader and designer.
    public ConfirmDialog()
        : this(string.Empty, string.Empty, "OK")
    {
    }

    public ConfirmDialog(string title, string message, string confirmText)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
