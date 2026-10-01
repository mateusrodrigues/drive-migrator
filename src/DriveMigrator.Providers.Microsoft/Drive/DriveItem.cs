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

internal sealed record FileFacet(string? MimeType, HashesFacet? Hashes);

/// <summary>
/// Content hashes. QuickXorHash is reported everywhere; SHA-1 and SHA-256 (uppercase hex) only by some account
/// types. Any of them can be missing right after an upload.
/// </summary>
internal sealed record HashesFacet(string? QuickXorHash, string? Sha1Hash, string? Sha256Hash);

internal sealed record PackageFacet(string? Type);

internal sealed record ItemReference(string? DriveId, string? Id);

/// <summary>An item that lives in another drive, e.g. a shared folder added to "My files".</summary>
internal sealed record RemoteItemFacet(string Id, long? Size, FolderFacet? Folder, FileFacet? File, ItemReference? ParentReference);
