using Avalonia.Headless.XUnit;
using DriveMigrator.App.ViewModels;
using DriveMigrator.Core;
using DriveMigrator.Core.Transfers;
using DriveMigrator.Testing;

namespace DriveMigrator.App.Tests;

public class MailTreeTests
{
    private readonly FakeAccountSession _account = new FakeCloudProvider().AddAccount();

    private FakeMailCapability Mail => _account.Mail;

    [AvaloniaFact]
    public async Task LargeFolder_ShowsFirstPage_AndCheckingVisibleMessagesIsNotTheWholeFolder()
    {
        var inbox = Mail.AddSpecialFolder(ContainerRole.Inbox, "Inbox");
        for (var i = 0; i < NodeViewModel.MailBrowseLimit + 5; i++)
        {
            Mail.AddMessage(inbox, $"m{i}", [1]);
        }

        Mail.AddContainer(null, "Other");

        var root = NodeViewModel.CreateRoot(Mail, () => { }, CancellationToken.None);
        await root.LoadChildrenAsync();
        var folder = root.Children[0];
        await folder.LoadChildrenAsync();

        var messages = folder.Children.Where(c => !c.IsPlaceholder).ToList();
        Assert.Equal(NodeViewModel.MailBrowseLimit, messages.Count);
        Assert.Contains("first 200", folder.Children[^1].Name, StringComparison.Ordinal);

        foreach (var message in messages)
        {
            message.IsChecked = true;
        }

        Assert.Null(folder.IsChecked);
        Assert.Equal(NodeViewModel.MailBrowseLimit, root.GetCheckedRoots().Count());

        // Checking the folder itself selects everything, including what isn't shown.
        folder.IsChecked = true;
        Assert.Equal([folder], root.GetCheckedRoots());
    }

    [AvaloniaFact]
    public async Task MailKeepsServiceOrderAndShowsSender()
    {
        Mail.AddSpecialFolder(ContainerRole.Inbox, "Inbox");
        Mail.AddSpecialFolder(ContainerRole.Sent, "Sent");
        Mail.AddContainer(null, "Archive 2019");

        var root = NodeViewModel.CreateRoot(Mail, () => { }, CancellationToken.None);
        await root.LoadChildrenAsync();

        Assert.Equal(["Inbox", "Sent", "Archive 2019"], root.Children.Select(c => c.Name));
    }

    [AvaloniaFact]
    public void OptionsDialog_ShowsMailSectionOnlyForMail()
    {
        var target = new FakeCloudProvider().AddAccount();
        var mailOnly = new TransferRequest(_account, target, [new(CapabilityKind.Mail, null)], [new(CapabilityKind.Mail, null)]);

        var vm = new TransferOptionsViewModel("summary", mailOnly) { SkipDuplicates = false };

        Assert.True(vm.HasMail && vm.ShowDuplicateOption);
        Assert.False(vm.HasContacts);
        Assert.False(vm.HasFiles);
        Assert.False(vm.HasNativeDocuments);
        Assert.False(vm.ToOptions().SkipDuplicates);
    }

    [AvaloniaFact]
    public void OptionsDialog_ContactsGetDuplicateOptionAndNote()
    {
        var target = new FakeCloudProvider().AddAccount();
        var contacts = new TransferRequest(_account, target, [new(CapabilityKind.Contacts, null)], [new(CapabilityKind.Contacts, null)]);

        var vm = new TransferOptionsViewModel("summary", contacts);

        Assert.True(vm.HasContacts && vm.ShowDuplicateOption);
        Assert.Contains("email address", vm.DuplicateOptionLabel, StringComparison.Ordinal);
        Assert.True(vm.ToOptions().SkipDuplicates);
    }
}
