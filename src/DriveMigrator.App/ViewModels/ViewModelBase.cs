using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DriveMigrator.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>Runs <paramref name="action"/> on the UI thread; services raise events from background threads.</summary>
    protected static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
