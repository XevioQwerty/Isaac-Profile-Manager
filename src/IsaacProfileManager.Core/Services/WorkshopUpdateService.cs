using System.Text.Json;

namespace IsaacProfileManager.Core.Services;

/// <summary>What the Workshop currently says about one published file.</summary>
public sealed record WorkshopFileDetails(
    string Id,
    bool Available,
    string Title,
    long TimeUpdated,
    long FileSize)
{
    public DateTimeOffset? UpdatedUtc =>
        TimeUpdated > 0 ? DateTimeOffset.FromUnixTimeSeconds(TimeUpdated) : null;
}

public interface IWorkshopUpdateChecker
{
    Task<IReadOnlyDictionary<string, WorkshopFileDetails>> FetchAsync(
        IReadOnlyList<string> ids, CancellationToken cancellation = default);
}

/// <summary>
/// Asks Steam when each Workshop item was last changed.
///
/// This is the one part of the update story that needs nothing from the Steam
/// client: <c>ISteamRemoteStorage/GetPublishedFileDetails/v1</c> is a keyless
/// POST that answers for items you are not subscribed to. Verified 2026-08-27
/// against all 40 ids in the reference library — 9 had changed since import.
///
/// That matters because resubscribing is the expensive, disruptive step: while
/// an item is subscribed Steam re-materialises it into whichever profile is
/// junctioned. Knowing which handful actually changed keeps that window small.
/// </summary>
public sealed class WorkshopUpdateService : IWorkshopUpdateChecker, IDisposable
{
    public const string Endpoint = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

    /// <summary>
    /// Ids per request. Steam accepts large batches, but a failed batch costs
    /// everything in it, so keep them small enough to retry cheaply.
    /// </summary>
    private const int BatchSize = 50;

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public WorkshopUpdateService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsClient = http is null;
    }

    public async Task<IReadOnlyDictionary<string, WorkshopFileDetails>> FetchAsync(
        IReadOnlyList<string> ids, CancellationToken cancellation = default)
    {
        var results = new Dictionary<string, WorkshopFileDetails>(StringComparer.Ordinal);

        foreach (var batch in Batch(ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)))
        {
            foreach (var detail in await FetchBatchAsync(batch, cancellation).ConfigureAwait(false))
                results[detail.Id] = detail;
        }

        return results;
    }

    private async Task<IReadOnlyList<WorkshopFileDetails>> FetchBatchAsync(
        IReadOnlyList<string> ids, CancellationToken cancellation)
    {
        var form = new List<KeyValuePair<string, string>>(ids.Count + 1)
        {
            new("itemcount", ids.Count.ToString()),
        };
        for (var i = 0; i < ids.Count; i++)
            form.Add(new KeyValuePair<string, string>($"publishedfileids[{i}]", ids[i]));

        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(Endpoint, content, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        return Parse(json);
    }

    /// <summary>
    /// Read the details array. Numeric fields arrive inconsistently — Steam
    /// sends <c>file_size</c> as a string and <c>time_updated</c> as a number —
    /// so both are read leniently rather than bound to a type.
    /// </summary>
    public static IReadOnlyList<WorkshopFileDetails> Parse(string json)
    {
        var details = new List<WorkshopFileDetails>();

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("response", out var response)) return details;
        if (!response.TryGetProperty("publishedfiledetails", out var array)) return details;
        if (array.ValueKind != JsonValueKind.Array) return details;

        foreach (var item in array.EnumerateArray())
        {
            var id = Text(item, "publishedfileid");
            if (id is null) continue;

            // result 1 is success. Anything else means the item is gone, hidden
            // or was never ours to see — not that it is unchanged.
            var available = Number(item, "result") == 1;

            details.Add(new WorkshopFileDetails(
                Id: id,
                Available: available,
                Title: Text(item, "title") ?? id,
                TimeUpdated: Number(item, "time_updated") ?? 0,
                FileSize: Number(item, "file_size") ?? 0));
        }

        return details;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out var n) ? n : null,
            JsonValueKind.String => long.TryParse(value.GetString(), out var n) ? n : null,
            _ => null,
        };
    }

    private static IEnumerable<IReadOnlyList<string>> Batch(IEnumerable<string> ids)
    {
        var batch = new List<string>(BatchSize);
        foreach (var id in ids)
        {
            batch.Add(id);
            if (batch.Count < BatchSize) continue;
            yield return batch;
            batch = new List<string>(BatchSize);
        }
        if (batch.Count > 0) yield return batch;
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
