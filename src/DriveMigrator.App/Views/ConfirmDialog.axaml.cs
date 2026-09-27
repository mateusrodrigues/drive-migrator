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

    public ConfirmDialog(string title, string message, string confirmText, bool showCancel = true, bool destructive = false)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        ConfirmButton.Classes.Set("primary", !destructive);
        ConfirmButton.Classes.Set("danger", destructive);

        // Enter shouldn't remove something.
        ConfirmButton.IsDefault = !destructive;
        CancelButton.IsVisible = showCancel;
        if (!showCancel)
        {
            Width = 400;
        }
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
