using Microsoft.Extensions.FileProviders;

// Serves docs/ (the same files GitHub Pages publishes) plus clients/js/highbridge.js, which wins
// over the committed docs/lib copy so local edits show up immediately. Falls back to the copies
// in the build output when the repo isn't on disk.

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:47200");

var app = builder.Build();

string? repoRoot = FindRepoRoot(AppContext.BaseDirectory);
IFileProvider files = repoRoot is null
    ? new PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "site"))
    : new CompositeFileProvider(
        new PrefixedFileProvider("lib", new PhysicalFileProvider(Path.Combine(repoRoot, "clients", "js"))),
        new PhysicalFileProvider(Path.Combine(repoRoot, "docs")));

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.CacheControl = "no-cache";
    await next();
});
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions { FileProvider = files });

app.Run();

static string? FindRepoRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "highbridge.slnx")))
        {
            return dir.FullName;
        }
    }

    return null;
}

/// <summary>Exposes an inner provider under a sub-path, e.g. "lib/highbridge.js".</summary>
internal sealed class PrefixedFileProvider(string prefix, IFileProvider inner) : IFileProvider
{
    private readonly string _prefix = "/" + prefix.Trim('/');

    public IDirectoryContents GetDirectoryContents(string subpath) =>
        Strip(subpath) is { } rest ? inner.GetDirectoryContents(rest) : NotFoundDirectoryContents.Singleton;

    public IFileInfo GetFileInfo(string subpath) =>
        Strip(subpath) is { } rest ? inner.GetFileInfo(rest) : new NotFoundFileInfo(subpath);

    public Microsoft.Extensions.Primitives.IChangeToken Watch(string filter) => inner.Watch(filter);

    private string? Strip(string subpath)
    {
        string normalized = "/" + subpath.TrimStart('/');
        if (normalized.Equals(_prefix, StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        return normalized.StartsWith(_prefix + "/", StringComparison.OrdinalIgnoreCase) ? normalized[_prefix.Length..] : null;
    }
}
