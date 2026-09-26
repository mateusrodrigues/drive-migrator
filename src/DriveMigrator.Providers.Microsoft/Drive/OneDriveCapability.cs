using System.Runtime.CompilerServices;
using DriveMigrator.Core;
using DriveMigrator.Core.Drive;
using DriveMigrator.Providers.Microsoft.Graph;

namespace DriveMigrator.Providers.Microsoft.Drive;

/// <summary>
/// OneDrive through Microsoft Graph. Node ids are Graph paths ("drives/{driveId}/items/{itemId}") so items in
/// other drives (shared folders added to "My files") browse the same way as the user's own.
/// </summary>
internal sealed class OneDriveCapability(GraphClient graph) : IDriveCapability
{
    private const string Select = "$select=id,name,size,lastModifiedDateTime,folder,file,package,remoteItem,parentReference";

    public CapabilityKind Kind => CapabilityKind.Drive;

    public string DisplayName => "OneDrive";

    public bool SupportsNestedContainers => true;

    public async IAsyncEnumerable<MigrationNode> GetChildrenAsync(
        MigrationNode? parent,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var path = parent?.Id ?? "me/drive/root";
        await foreach (var item in graph.GetPagedAsync<DriveItem>($"{path}/children?{Select}", cancellationToken).ConfigureAwait(false))
        {
            if (ToNode(item) is { } node)
            {
                yield return node;
            }
        }
    }

    public Task<MigrationNode> CreateContainerAsync(MigrationNode? parent, string name, CancellationToken cancellationToken = default)
        => throw TransfersNotAvailable();

    public Task<DriveFileContent> OpenReadAsync(MigrationNode file, ExportFormat? exportAs, CancellationToken cancellationToken = default)
        => throw TransfersNotAvailable();

    public Task<MigrationNode> UploadAsync(
        MigrationNode? parent,
        DriveFileContent content,
        DriveUploadOptions options,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
        => throw TransfersNotAvailable();

    internal static MigrationNode? ToNode(DriveItem item)
    {
        // OneNote notebooks are "packages": folders of sections that can't be copied as files. Not supported yet.
        if (item.Package is not null)
        {
            return null;
        }

        var remote = item.RemoteItem;
        var driveId = remote?.ParentReference?.DriveId ?? item.ParentReference?.DriveId;
        var itemId = remote?.Id ?? item.Id;
        var id = driveId is null ? $"me/drive/items/{itemId}" : $"drives/{driveId}/items/{itemId}";

        var isFolder = (remote?.Folder ?? item.Folder) is not null;
        return new MigrationNode(id, item.Name, isFolder ? NodeKind.Folder : NodeKind.File)
        {
            Size = isFolder ? null : remote?.Size ?? item.Size,
            ModifiedAt = item.LastModifiedDateTime,
            MimeType = (remote?.File ?? item.File)?.MimeType,
        };
    }

    // Reading and writing file content arrive with the transfer engine.
    private static NotSupportedException TransfersNotAvailable() => new("Copying OneDrive content is not available yet.");
}
