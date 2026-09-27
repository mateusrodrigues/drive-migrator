using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DriveMigrator.App.Views;

public partial class TransferOptionsWindow : Window
{
    public TransferOptionsWindow() => InitializeComponent();

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // Only sections relevant to this copy are visible; the first of those has no rule above it.
        var first = true;
        foreach (var section in Sections.Children)
        {
            section.Classes.Set("first", first && section.IsVisible);
            first &= !section.IsVisible;
        }
    }

    private void OnStart(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
