using Avalonia.Platform;

using DriveMigrator.App.Controls;

namespace DriveMigrator.App.Services;

/// <summary>A provider's setup guide: the Markdown of <c>docs/setup-{provider}.md</c> and the title it opens with.</summary>
internal sealed record SetupGuide(string Title, string Markdown);

/// <summary>
/// The guides for creating each provider's OAuth app, packaged with the app from <c>docs/</c> as
/// <c>Guides/setup-{providerId}.md</c> so they are readable without the repository.
/// </summary>
internal static class SetupGuides
{
    private static Uri UriFor(string providerId) => new($"avares://DriveMigrator/Guides/setup-{providerId}.md");

    public static bool Exists(string providerId) => AssetLoader.Exists(UriFor(providerId));

    public static SetupGuide? Load(string providerId)
    {
        if (!Exists(providerId))
        {
            return null;
        }

        using var reader = new StreamReader(AssetLoader.Open(UriFor(providerId)));
        var markdown = reader.ReadToEnd();
        return new SetupGuide(MarkdownView.FirstHeading(markdown) ?? "Setup guide", markdown);
    }
}
