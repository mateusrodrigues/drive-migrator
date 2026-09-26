namespace DriveMigrator.Core;

/// <summary>A connected account. <see cref="AccountId"/> is unique within its provider.</summary>
public sealed record AccountInfo(string ProviderId, string AccountId, string DisplayName, string? Email);
