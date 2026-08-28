using System.Text.Json;

namespace IsaacProfileManager.Core.Services;

/// <summary>
/// Resolves a Steam Workshop collection to the items inside it.
///
/// This is the short-code path. A collection id is ten digits because Steam
/// holds the list; a self-contained code has to carry it, and cannot be small.
/// Verified 2026-08-28 against a live Isaac collection: keyless, no client
/// needed, 29 children returned.
///
/// What it cannot do is carry hashes, notes or library entry names, so a
/// collection import can fetch a set but not prove it matches the sender's
/// bytes. For co-op that distinction is the whole point, so this is the
/// secondary path, not the primary one.
/// </summary>
public sealed class WorkshopCollectionService
{
    public const string Endpoint = "https://api.steampowered.com/ISteamRemoteStorage/GetCollectionDetails/v1/";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public WorkshopCollectionService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsClient = http is null;
    }

    /// <summary>
    /// Pull the id out of whatever the user pasted — the bare number, or a
    /// store URL, or a <c>steam://openurl/</c> wrapper.
    /// </summary>
    public static string? ParseId(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var trimmed = input.Trim();
        if (trimmed.All(char.IsDigit) && trimmed.Length >= 6) return trimmed;

        var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"[?&]id=(\d{6,})");
        return match.Success ? match.Groups[1].Value : null;
    }

    public async Task<IReadOnlyList<string>> GetChildIdsAsync(string collectionId, CancellationToken cancellation = default)
    {
        var form = new[]
        {
            new KeyValuePair<string, string>("collectioncount", "1"),
            new KeyValuePair<string, string>("publishedfileids[0]", collectionId),
        };

        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(Endpoint, content, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return ParseChildren(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
    }

    public static IReadOnlyList<string> ParseChildren(string json)
    {
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("collectiondetails", out var details) ||
            details.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var children = new List<string>();

        foreach (var collection in details.EnumerateArray())
        {
            // result 9 is what a non-collection id comes back as. Returning an
            // empty list for it would read as "an empty collection", which sends
            // the user looking for the wrong problem.
            if (collection.TryGetProperty("result", out var result) &&
                result.ValueKind == JsonValueKind.Number && result.GetInt32() != 1)
                throw new ShareCodeException(
                    "Steam does not recognise that as a collection. Check the id — a single mod's id will not work.");

            if (!collection.TryGetProperty("children", out var array) || array.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var child in array.EnumerateArray())
                if (child.TryGetProperty("publishedfileid", out var id) && id.ValueKind == JsonValueKind.String)
                    children.Add(id.GetString()!);
        }

        return children;
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
