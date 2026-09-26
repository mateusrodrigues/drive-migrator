using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DriveMigrator.App.ViewModels;

public static class Converters
{
    public static readonly IValueConverter ItalicIfTrue =
        new FuncValueConverter<bool, FontStyle>(value => value ? FontStyle.Italic : FontStyle.Normal);
}
