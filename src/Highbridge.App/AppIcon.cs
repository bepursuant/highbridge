namespace Highbridge.App;

/// <summary>The highbridge logo (branding/highbridge.ico), embedded in the exe.</summary>
internal static class AppIcon
{
    /// <summary>All sizes, shared by every window; WinForms picks the small and large frames.</summary>
    public static Icon Window { get; } = Load(null);

    /// <summary>
    /// One frame at the tray size for the current DPI (16 px at 100%, 24 px at 150%), so Windows
    /// doesn't scale a larger frame down. The caller disposes it.
    /// </summary>
    public static Icon CreateTrayIcon() => Load(SystemInformation.SmallIconSize);

    private static Icon Load(Size? size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("highbridge.ico")
            ?? throw new InvalidOperationException("highbridge.ico is not embedded.");
        return size is { } s ? new Icon(stream, s) : new Icon(stream);
    }
}
