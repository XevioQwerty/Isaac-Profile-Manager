using System.Text.Json;

namespace IsaacProfileManager.Core.Services;

public sealed record PreviewCacheResult(int Fetched, int AlreadyCached, int Unavailable, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Fetches Workshop preview images and caches them beside the library.
///
/// The store preview is server-side item metadata, not a file inside the item —
/// only about a third of Isaac mods ship a local <c>thumb.png</c>. So the image
/// has to be fetched while the subscription still exists; afterwards Steam has
/// deleted the content store and the endpoint no longer resolves the item for
/// this account. Import captures it once and it survives unsubscribing.
///
/// Uses the public <c>GetPublishedFileDetails</c> endpoint, which needs no API
/// key. Network failure is reported, never thrown: a missing picture must not
/// stop mods being imported.
/// </summary>
public sealed class WorkshopPreviewService
{
    private const string Endpoint = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

    private readonly HttpClient _http;

    public WorkshopPreviewService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Cache a preview for each (library entry, workshop id) pair into
    /// <paramref name="metadataRoot"/>, skipping entries already cached.
    /// </summary>
    public async Task<PreviewCacheResult> CacheAsync(
        IReadOnlyList<(string Entry, string Id)> items,
        string metadataRoot,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var pending = items.Where(i => FindCached(metadataRoot, i.Entry) is null).ToList();
        var alreadyCached = items.Count - pending.Count;

        if (pending.Count == 0)
            return new PreviewCacheResult(0, alreadyCached, 0, null);

        Directory.CreateDirectory(metadataRoot);

        Dictionary<string, string> urls;
        try
        {
            progress?.Report($"Asking Steam about {pending.Count} item(s)");
            urls = await GetPreviewUrlsAsync(pending.Select(p => p.Id).ToList(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new PreviewCacheResult(0, alreadyCached, pending.Count, ex.Message);
        }

        var fetched = 0;
        var unavailable = 0;

        foreach (var (entry, id) in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!urls.TryGetValue(id, out var url) || string.IsNullOrWhiteSpace(url))
            {
                unavailable++;
                continue;
            }

            try
            {
                progress?.Report($"Preview: {entry}");
                var bytes = await _http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);

                var extension = Path.GetExtension(new Uri(url).AbsolutePath);
                if (string.IsNullOrWhiteSpace(extension) || extension.Length > 5) extension = ".png";

                await File.WriteAllBytesAsync(Path.Combine(metadataRoot, entry + extension), bytes, cancellationToken)
                          .ConfigureAwait(false);
                fetched++;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException)
            {
                unavailable++;
            }
        }

        return new PreviewCacheResult(fetched, alreadyCached, unavailable, null);
    }

    private async Task<Dictionary<string, string>> GetPreviewUrlsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("itemcount", ids.Count.ToString()),
        };
        for (var i = 0; i < ids.Count; i++)
            fields.Add(new KeyValuePair<string, string>($"publishedfileids[{i}]", ids[i]));

        using var content = new FormUrlEncodedContent(fields);
        using var response = await _http.PostAsync(Endpoint, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParsePreviewUrls(json);
    }

    /// <summary>Pull publishedfileid -> preview_url out of the API response.</summary>
    public static Dictionary<string, string> ParsePreviewUrls(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("response", out var response)) return result;
        if (!response.TryGetProperty("publishedfiledetails", out var details)) return result;

        foreach (var item in details.EnumerateArray())
        {
            if (!item.TryGetProperty("publishedfileid", out var idElement)) continue;
            var id = idElement.GetString();
            if (id is null) continue;

            if (item.TryGetProperty("preview_url", out var urlElement) &&
                urlElement.GetString() is { Length: > 0 } url)
            {
                result[id] = url;
            }
        }

        return result;
    }

    /// <summary>The cached image for a library entry, if one was captured.</summary>
    public static string? FindCached(string metadataRoot, string entry)
    {
        if (!Directory.Exists(metadataRoot)) return null;
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".gif" })
        {
            var path = Path.Combine(metadataRoot, entry + extension);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
