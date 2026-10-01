using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
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
    /// <summary>Files up to this size are uploaded with one PUT; larger ones use a resumable upload session.</summary>
    internal const int SimpleUploadLimit = 4 * 1024 * 1024;

    /// <summary>Upload session chunk size; Graph requires a multiple of 320 KiB.</summary>
    internal const int ChunkSize = 32 * 320 * 1024;

    internal const int MaxChunkRetries = 5;

    private const string Select = "$select=id,name,size,lastModifiedDateTime,folder,file,package,remoteItem,parentReference";
    private const string RootPath = "me/drive/root";

    private static readonly char[] InvalidNameCharacters = ['"', '*', ':', '<', '>', '?', '/', '\\', '|'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", ".lock", "desktop.ini",
    };

    public CapabilityKind Kind => CapabilityKind.Drive;

    public string DisplayName => "OneDrive";

    public bool SupportsNestedContainers => true;

    public async IAsyncEnumerable<MigrationNode> GetChildrenAsync(
        MigrationNode? parent,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in graph.GetPagedAsync<DriveItem>($"{PathOf(parent)}/children?{Select}", cancellationToken).ConfigureAwait(false))
        {
            if (ToNode(item) is { } node)
            {
                yield return node;
            }
        }
    }

    /// <summary>Replaces characters OneDrive rejects, and avoids reserved and edge-whitespace names.</summary>
    public string ToValidName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(Array.IndexOf(InvalidNameCharacters, c) >= 0 || char.IsControl(c) ? '_' : c);
        }

        var valid = builder.ToString().Trim();
        if (valid.EndsWith('.'))
        {
            valid = valid[..^1] + "_";
        }

        if (valid.Length == 0 || ReservedNames.Contains(valid) || ReservedNames.Contains(Path.GetFileNameWithoutExtension(valid)) || valid.StartsWith("~$", StringComparison.Ordinal))
        {
            valid = "_" + valid;
        }

        return valid;
    }

    public async Task<MigrationNode?> FindChildAsync(MigrationNode? parent, string name, CancellationToken cancellationToken = default)
    {
        var item = await graph.GetOrDefaultAsync<DriveItem>($"{PathOf(parent)}:/{Uri.EscapeDataString(name)}?{Select}", cancellationToken).ConfigureAwait(false);
        return item is null ? null : ToNode(item);
    }

    public async Task<MigrationNode> CreateContainerAsync(MigrationNode? parent, string name, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object>
        {
            ["name"] = name,
            ["folder"] = new Dictionary<string, object>(),
            ["@microsoft.graph.conflictBehavior"] = "fail",
        };
        var created = await graph.SendJsonAsync<DriveItem>(HttpMethod.Post, $"{PathOf(parent)}/children", body, cancellationToken).ConfigureAwait(false);
        return ToNode(created) ?? throw new InvalidOperationException($"OneDrive returned an unexpected item for folder '{name}'.");
    }

    public async Task<DriveFileContent> OpenReadAsync(MigrationNode file, ExportFormat? exportAs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (exportAs is not null)
        {
            throw new ArgumentException("OneDrive files are copied as they are and cannot be exported.", nameof(exportAs));
        }

        // Graph redirects /content to a pre-authenticated download URL; HttpClient follows it and drops the token.
        var response = await graph.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{file.Id}/content"), cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new DriveFileContent(new HttpResponseStream(stream, response), file.Name, file.MimeType ?? "application/octet-stream")
            {
                Length = response.Content.Headers.ContentLength ?? file.Size,
                ModifiedAt = file.ModifiedAt,
            };
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task<MigrationNode> UploadAsync(
        MigrationNode? parent,
        DriveFileContent content,
        DriveUploadOptions options,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        var length = content.Length ?? throw new ArgumentException("OneDrive uploads need the content length.", nameof(content));

        var item = length <= SimpleUploadLimit
            ? await SimpleUploadAsync(parent, content, options, cancellationToken).ConfigureAwait(false)
            : await SessionUploadAsync(parent, content, length, options, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report(length);
        return ToNode(item) ?? throw new InvalidOperationException($"OneDrive returned an unexpected item for '{content.Name}'.");
    }

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
        var file = remote?.File ?? item.File;
        return new MigrationNode(id, item.Name, isFolder ? NodeKind.Folder : NodeKind.File)
        {
            Size = isFolder ? null : remote?.Size ?? item.Size,
            ModifiedAt = item.LastModifiedDateTime,
            MimeType = file?.MimeType,
            Hashes = HashesOf(file?.Hashes),
        };
    }

    private static FileHashes? HashesOf(HashesFacet? facet)
    {
        if (facet is null)
        {
            return null;
        }

        var hashes = new FileHashes(
            Sha1: NullIfEmpty(facet.Sha1Hash)?.ToLowerInvariant(),
            Sha256: NullIfEmpty(facet.Sha256Hash)?.ToLowerInvariant(),
            QuickXor: NullIfEmpty(facet.QuickXorHash));
        return hashes.IsEmpty ? null : hashes;

        static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    }

    private static string PathOf(MigrationNode? node) => node?.Id ?? RootPath;

    private async Task<DriveItem> SimpleUploadAsync(MigrationNode? parent, DriveFileContent content, DriveUploadOptions options, CancellationToken cancellationToken)
    {
        // Buffer (at most 4 MiB) so throttled requests can be retried with the same body.
        using var buffer = new MemoryStream();
        await content.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        var url = options.Replace is { } replace
            ? $"{replace.Id}/content"
            : $"{PathOf(parent)}:/{Uri.EscapeDataString(content.Name)}:/content?@microsoft.graph.conflictBehavior=fail";

        DriveItem item;
        using (var response = await graph.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Put, url) { Content = ByteContent(bytes, content.MimeType) },
            cancellationToken).ConfigureAwait(false))
        {
            item = await response.Content.ReadFromJsonAsync<DriveItem>(GraphClient.JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("OneDrive returned an empty upload response.");
        }

        // A simple PUT can't carry timestamps; set them afterwards so copies keep their original dates.
        if (FileSystemInfo(content) is { } fileSystemInfo)
        {
            var path = ToNode(item)?.Id ?? $"me/drive/items/{item.Id}";
            item = await graph.SendJsonAsync<DriveItem>(HttpMethod.Patch, path, new Dictionary<string, object> { ["fileSystemInfo"] = fileSystemInfo }, cancellationToken).ConfigureAwait(false);
        }

        return item;
    }

    private async Task<DriveItem> SessionUploadAsync(
        MigrationNode? parent,
        DriveFileContent content,
        long length,
        DriveUploadOptions options,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        var itemProperties = new Dictionary<string, object>
        {
            ["@microsoft.graph.conflictBehavior"] = options.Replace is null ? "fail" : "replace",
        };
        if (FileSystemInfo(content) is { } fileSystemInfo)
        {
            itemProperties["fileSystemInfo"] = fileSystemInfo;
        }

        var url = options.Replace is { } replace
            ? $"{replace.Id}/createUploadSession"
            : $"{PathOf(parent)}:/{Uri.EscapeDataString(content.Name)}:/createUploadSession";
        var session = await graph.SendJsonAsync<UploadSession>(HttpMethod.Post, url, new Dictionary<string, object> { ["item"] = itemProperties }, cancellationToken).ConfigureAwait(false);

        try
        {
            var chunk = new byte[(int)Math.Min(ChunkSize, length)];
            long offset = 0;
            while (true)
            {
                var read = await content.Content.ReadAtLeastAsync(chunk, (int)Math.Min(chunk.Length, length - offset), throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                if (read == 0 || offset + read > length)
                {
                    throw new IOException($"'{content.Name}' was expected to be {length} bytes but its content ended at {offset + read}.");
                }

                var result = await PutChunkAsync(session.UploadUrl, chunk, read, offset, length, cancellationToken).ConfigureAwait(false);
                offset += read;
                progress?.Report(offset);
                if (result is not null)
                {
                    return result;
                }
            }
        }
        catch
        {
            // Best effort: free the session so no partial file lingers on the server.
            try
            {
                using var cancel = new HttpRequestMessage(HttpMethod.Delete, session.UploadUrl);
                using var _ = await graph.SendUnauthenticatedAsync(cancel, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Cleanup must not hide the original failure.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            throw;
        }
    }

    /// <summary>Uploads one chunk; returns the finished item after the last chunk, otherwise null.</summary>
    private async Task<DriveItem?> PutChunkAsync(Uri uploadUrl, byte[] chunk, int count, long offset, long length, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
            {
                Content = new ByteArrayContent(chunk, 0, count),
            };
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, length);

            HttpResponseMessage response;
            try
            {
                response = await graph.SendUnauthenticatedAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < MaxChunkRetries)
            {
                await graph.DelayAsync(GraphClient.BackOff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Accepted)
                {
                    return null;
                }

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<DriveItem>(GraphClient.JsonOptions, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("OneDrive returned an empty response for the last chunk.");
                }

                if (GraphClient.IsTransient(response.StatusCode) && attempt < MaxChunkRetries)
                {
                    await graph.DelayAsync(GraphClient.RetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw await GraphException.FromResponseAsync(response, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static Dictionary<string, object>? FileSystemInfo(DriveFileContent content)
    {
        var info = new Dictionary<string, object>();
        if (content.ModifiedAt is { } modified)
        {
            info["lastModifiedDateTime"] = modified.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        }

        if (content.CreatedAt is { } created)
        {
            info["createdDateTime"] = created.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        }

        return info.Count == 0 ? null : info;
    }

    private static ByteArrayContent ByteContent(byte[] bytes, string mimeType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.TryParse(mimeType, out var type) ? type : new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    private sealed record UploadSession(Uri UploadUrl);
}
