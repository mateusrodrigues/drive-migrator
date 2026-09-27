using Avalonia.Data.Converters;

namespace DriveMigrator.App.ViewModels;

public static class Converters
{
    /// <summary>Whether an index equals the converter parameter, e.g. to show the content of the selected tab.</summary>
    public static readonly IValueConverter IndexEquals =
        new FuncValueConverter<int, string, bool>((index, parameter) => index.ToString(System.Globalization.CultureInfo.InvariantCulture) == parameter);
}
