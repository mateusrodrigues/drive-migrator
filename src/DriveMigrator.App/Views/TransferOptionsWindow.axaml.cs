using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DriveMigrator.App.Views;

public partial class TransferOptionsWindow : Window
{
    public TransferOptionsWindow() => InitializeComponent();

    private void OnStart(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
