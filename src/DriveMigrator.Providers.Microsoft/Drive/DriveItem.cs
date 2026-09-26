namespace DriveMigrator.Providers.Microsoft.Drive;

// Subset of the Graph driveItem resource used for browsing.
internal sealed record DriveItem(
    string Id,
    string Name,
    long? Size,
    DateTimeOffset? LastModifiedDateTime,
    FolderFacet? Folder,
    FileFacet? File,
    PackageFacet? Package,
    RemoteItemFacet? RemoteItem,
    ItemReference? ParentReference);

internal sealed record FolderFacet(int? ChildCount);

internal sealed record FileFacet(string? MimeType);

internal sealed record PackageFacet(string? Type);

internal sealed record ItemReference(string? DriveId, string? Id);

/// <summary>An item that lives in another drive, e.g. a shared folder added to "My files".</summary>
internal sealed record RemoteItemFacet(string Id, long? Size, FolderFacet? Folder, FileFacet? File, ItemReference? ParentReference);
