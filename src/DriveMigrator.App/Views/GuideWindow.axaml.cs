using Avalonia.Controls;
using Avalonia.Interactivity;

using DriveMigrator.App.Services;

namespace DriveMigrator.App.Views;

public partial class GuideWindow : Window
{
    // Required by the XAML loader and designer.
    public GuideWindow()
        : this(new SetupGuide(string.Empty, string.Empty))
    {
    }

    internal GuideWindow(SetupGuide guide)
    {
        InitializeComponent();
        Title = guide.Title;
        Guide.Markdown = guide.Markdown;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
