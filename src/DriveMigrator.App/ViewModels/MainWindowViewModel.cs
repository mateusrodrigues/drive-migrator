using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveMigrator.App.Services;
using DriveMigrator.Core;
using DriveMigrator.Core.Accounts;
using DriveMigrator.Core.Transfers;
using DriveMigrator.Engine;

namespace DriveMigrator.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly AccountManager _accounts;
    private readonly IProviderRegistry _providers;
    private readonly IDialogService _dialogs;
    private readonly TransferManager _transferManager;

    public MainWindowViewModel(AccountManager accounts, IProviderRegistry providers, IDialogService dialogs, TransferManager transfers)
    {
        _accounts = accounts;
        _providers = providers;
        _dialogs = dialogs;
        _transferManager = transfers;
        Transfers = new TransfersViewModel(transfers, dialogs);
        _accounts.AccountsChanged += OnAccountsChanged;
    }

    public TransfersViewModel Transfers { get; }

    public PaneViewModel Left { get; } = new("left");

    public PaneViewModel Right { get; } = new("right");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInfo))]
    public partial string Status { get; set; } = "Loading accounts…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInfo))]
    public partial bool NeedsAttention { get; set; }

    /// <summary>Whether to show <see cref="Status"/> as plain text (warnings use the banner instead).</summary>
    public bool ShowInfo => !NeedsAttention && Status.Length > 0;

    public async Task InitializeAsync()
    {
        try
        {
            await _accounts.LoadAsync();
        }
#pragma warning disable CA1031 // Start-up must not crash on a broken account list; show the problem instead.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Status = $"Could not load accounts: {ex.Message}";
            NeedsAttention = true;
            return;
        }

        // AccountsChanged is raised off the UI thread and its refresh may not have run yet.
        RefreshAccounts();

        // Jobs interrupted last time come back paused, ready to resume.
        _transferManager.Load();
        Transfers.Sync();

        // Start with two different accounts side by side when there are at least two.
        if (Left.SelectedAccount is null && Right.SelectedAccount is null)
        {
            Left.SelectedAccount = Left.Accounts.ElementAtOrDefault(0);
            Right.SelectedAccount = Right.Accounts.ElementAtOrDefault(1);
        }
    }

    public void Dispose()
    {
        _accounts.AccountsChanged -= OnAccountsChanged;
        Transfers.Dispose();
        Left.Dispose();
        Right.Dispose();
    }

    /// <summary>
    /// Validates the panes and builds the request for copying from <paramref name="source"/> to
    /// <paramref name="destination"/>. Returns null with a user-facing <paramref name="error"/> when it can't.
    /// </summary>
    internal static TransferRequest? BuildRequest(PaneViewModel source, PaneViewModel destination, out string? error)
    {
        var sourceSide = source.Name;
        var destinationSide = destination.Name;
        if (source.Session is not { } sourceSession)
        {
            error = $"Choose an account in the {sourceSide} pane to copy from.";
            return null;
        }

        if (destination.Session is not { } destinationSession)
        {
            error = $"Choose an account in the {destinationSide} pane to copy to.";
            return null;
        }

        var selected = source.GetCheckedNodes();
        if (selected.Count == 0)
        {
            error = $"Check the items you want to copy in the {sourceSide} pane.";
            return null;
        }

        var sameAccount = sourceSession == destinationSession;
        var targets = new List<TransferTarget>();
        foreach (var kind in selected.Select(n => n.Capability.Kind).Distinct())
        {
            if (destinationSession.GetCapability(kind) is null)
            {
                error = $"{destinationSession.Account.DisplayName} has no {kind.ToString().ToLowerInvariant()} to copy into.";
                return null;
            }

            var target = destination.ResolveDestination(kind);
            if (sameAccount && selected.Any(n => n.Capability.Kind == kind && (n.IsCapabilityRoot || (target is not null && target.IsSelfOrDescendantOf(n.Node!)))))
            {
                error = "Items can't be copied into themselves. Choose a destination outside the selected folders.";
                return null;
            }

            targets.Add(new TransferTarget(kind, target?.Node));
        }

        error = null;
        return new TransferRequest(sourceSession, destinationSession, PaneViewModel.ToTransferItems(selected), targets);
    }

    /// <summary>Plain-language summary of what a request would copy, per data category.</summary>
    internal static string Describe(PaneViewModel source, PaneViewModel destination, TransferRequest request)
    {
        var text = new StringBuilder();
        text.Append("From ").Append(source.SelectedAccount!.Label).Append(" to ").AppendLine(destination.SelectedAccount!.Label).AppendLine();

        foreach (var group in source.GetCheckedNodes().GroupBy(n => n.Capability))
        {
            var target = destination.ResolveDestination(group.Key.Kind);
            var where = target?.Path ?? destination.Session!.GetCapability(group.Key.Kind)!.DisplayName;
            text.Append("• ").Append(group.Key.DisplayName).Append(": ").Append(CountItems(group)).Append(" → ").AppendLine(where);
        }

        return text.ToString().TrimEnd();

        static string CountItems(IEnumerable<NodeViewModel> nodes)
        {
            var list = nodes.ToList();
            if (list.Any(n => n.IsCapabilityRoot))
            {
                return "everything";
            }

            var parts = new List<string>();
            AddCount(list.Count(n => n.IsContainer), "folder");
            AddCount(list.Count(n => !n.IsContainer && n.Node!.RequiresExport), "native document");
            AddCount(list.Count(n => n.Node?.Kind == NodeKind.MailMessage), "message");
            AddCount(list.Count(n => n.Node?.Kind == NodeKind.Contact), "contact");
            AddCount(list.Count(n => n.Node?.Kind == NodeKind.File && !n.Node.RequiresExport), "file");
            return string.Join(", ", parts);

            void AddCount(int count, string noun)
            {
                if (count > 0)
                {
                    parts.Add($"{count} {noun}{(count == 1 ? string.Empty : "s")}");
                }
            }
        }
    }

    [RelayCommand]
    private Task OpenSettingsAsync() => _dialogs.ShowSettingsAsync();

    [RelayCommand]
    private Task CopyLeftToRightAsync() => CopyAsync(Left, Right);

    [RelayCommand]
    private Task CopyRightToLeftAsync() => CopyAsync(Right, Left);

    /// <summary>"Google Drive (ada@gmail.com) → OneDrive / Documents (ada@outlook.com)".</summary>
    internal static string TitleFor(PaneViewModel source, PaneViewModel destination, TransferRequest request)
    {
        var from = string.Join(", ", request.Items.Select(i => i.Kind).Distinct().Select(k => request.Source.GetCapability(k)!.DisplayName));
        var to = string.Join(", ", request.Targets.Select(t => destination.ResolveDestination(t.Kind)?.Path ?? request.Destination.GetCapability(t.Kind)!.DisplayName));
        return $"{from} ({source.SelectedAccount!.Email ?? source.SelectedAccount.Label}) → {to} ({destination.SelectedAccount!.Email ?? destination.SelectedAccount.Label})";
    }

    private async Task CopyAsync(PaneViewModel source, PaneViewModel destination)
    {
        var request = BuildRequest(source, destination, out var error);
        if (request is null)
        {
            await _dialogs.ShowMessageAsync("Can't copy yet", error!);
            return;
        }

        var options = new TransferOptionsViewModel(Describe(source, destination, request), request);
        if (!await _dialogs.ShowTransferOptionsAsync(options))
        {
            return;
        }

        _transferManager.Start(request with { Options = options.ToOptions() }, TitleFor(source, destination, request));
        Transfers.Sync();
    }

    private void OnAccountsChanged(object? sender, EventArgs e) => OnUiThread(RefreshAccounts);

    private void RefreshAccounts()
    {
        var accounts = _accounts.Accounts;
        var connected = accounts.Where(a => a.Session is not null).ToList();
        Left.SetAccounts([.. connected.Select(CreateOption)]);
        Right.SetAccounts([.. connected.Select(CreateOption)]);

        var broken = accounts.Count - connected.Count;
        NeedsAttention = broken > 0;
        Status = accounts.Count switch
        {
            0 => "No accounts connected. Open Settings to connect Google or Microsoft accounts.",
            _ when broken > 0 => $"{broken} account(s) need attention in Settings.",
            _ => string.Empty,
        };
    }

    private AccountOptionViewModel CreateOption(ConnectedAccount account)
        => new(account, _providers.Providers.FirstOrDefault(p => p.Id == account.Info.ProviderId)?.DisplayName ?? account.Info.ProviderId);
}
