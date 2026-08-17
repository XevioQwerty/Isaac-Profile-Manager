using System.Net;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

public class WorkshopPreviewServiceTests
{
    /// <summary>Serves canned responses so the tests never touch the network.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<string> RequestedUrls { get; } = new();
        public string? PostedBody { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri!.ToString());
            if (request.Content is not null)
                PostedBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return _respond(request);
        }
    }

    private const string ApiResponse = """
        {"response":{"result":1,"resultcount":2,"publishedfiledetails":[
          {"publishedfileid":"835236871","preview_url":"https://images.example/aaa.jpg","title":"Better Character Menu"},
          {"publishedfileid":"3127536138","preview_url":"https://images.example/bbb.png","title":"REPENTOGON"}
        ]}}
        """;

    private static HttpClient ClientFor(StubHandler handler) => new(handler);

    [Fact]
    public void ParsePreviewUrls_PullsIdToUrlPairs()
    {
        var urls = WorkshopPreviewService.ParsePreviewUrls(ApiResponse);

        Assert.Equal(2, urls.Count);
        Assert.Equal("https://images.example/aaa.jpg", urls["835236871"]);
    }

    [Fact]
    public void ParsePreviewUrls_ToleratesItemsWithoutAPreview()
    {
        var urls = WorkshopPreviewService.ParsePreviewUrls("""
            {"response":{"publishedfiledetails":[
              {"publishedfileid":"1"},
              {"publishedfileid":"2","preview_url":""},
              {"publishedfileid":"3","preview_url":"https://x/y.png"}
            ]}}
            """);

        Assert.Single(urls);
        Assert.True(urls.ContainsKey("3"));
    }

    [Fact]
    public void ParsePreviewUrls_ToleratesAnEmptyOrShapelessResponse()
    {
        Assert.Empty(WorkshopPreviewService.ParsePreviewUrls("""{"response":{"result":9,"resultcount":0}}"""));
        Assert.Empty(WorkshopPreviewService.ParsePreviewUrls("{}"));
    }

    [Fact]
    public async Task CacheAsync_FetchesAndWritesOneImagePerEntry()
    {
        using var temp = new TempDir();
        var meta = temp.Dir("meta");
        var handler = new StubHandler(request =>
            request.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ApiResponse) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 }) });

        var service = new WorkshopPreviewService(ClientFor(handler));
        var result = await service.CacheAsync(
            new[] { ("bcm", "835236871"), ("repentogon", "3127536138") }, meta);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Fetched);
        // Extension follows the URL, so a jpg preview is not written as a .png.
        Assert.True(File.Exists(Path.Combine(meta, "bcm.jpg")));
        Assert.True(File.Exists(Path.Combine(meta, "repentogon.png")));
        Assert.Contains("publishedfileids", handler.PostedBody);
        Assert.Contains("itemcount=2", handler.PostedBody);
    }

    [Fact]
    public async Task CacheAsync_SkipsEntriesAlreadyCached()
    {
        using var temp = new TempDir();
        var meta = temp.Dir("meta");
        File.WriteAllBytes(Path.Combine(meta, "bcm.png"), new byte[] { 9 });

        var handler = new StubHandler(request =>
            request.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ApiResponse) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1 }) });

        var result = await new WorkshopPreviewService(ClientFor(handler))
            .CacheAsync(new[] { ("bcm", "835236871") }, meta);

        Assert.Equal(1, result.AlreadyCached);
        Assert.Equal(0, result.Fetched);
        Assert.Empty(handler.RequestedUrls);   // nothing to ask about, so no call at all
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(meta, "bcm.png")));
    }

    [Fact]
    public async Task CacheAsync_ReportsNetworkFailureInsteadOfThrowing()
    {
        using var temp = new TempDir();
        var meta = temp.Dir("meta");
        var handler = new StubHandler(_ => throw new HttpRequestException("no route to host"));

        var result = await new WorkshopPreviewService(ClientFor(handler))
            .CacheAsync(new[] { ("bcm", "835236871") }, meta);

        // A missing picture must never stop mods being imported.
        Assert.False(result.Succeeded);
        Assert.Equal("no route to host", result.Error);
        Assert.Equal(1, result.Unavailable);
    }

    [Fact]
    public async Task CacheAsync_CountsItemsSteamHasNoPreviewFor()
    {
        using var temp = new TempDir();
        var meta = temp.Dir("meta");
        var handler = new StubHandler(request =>
            request.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.OK)
                  { Content = new StringContent("""{"response":{"publishedfiledetails":[{"publishedfileid":"999"}]}}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1 }) });

        var result = await new WorkshopPreviewService(ClientFor(handler))
            .CacheAsync(new[] { ("thing", "999") }, meta);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Fetched);
        Assert.Equal(1, result.Unavailable);
    }

    [Fact]
    public async Task CacheAsync_SurvivesAFailedImageDownload()
    {
        using var temp = new TempDir();
        var meta = temp.Dir("meta");
        var handler = new StubHandler(request =>
            request.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ApiResponse) }
                : request.RequestUri!.AbsolutePath.EndsWith("aaa.jpg")
                    ? throw new HttpRequestException("404")
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 7 }) });

        var result = await new WorkshopPreviewService(ClientFor(handler))
            .CacheAsync(new[] { ("bcm", "835236871"), ("repentogon", "3127536138") }, meta);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Fetched);
        Assert.Equal(1, result.Unavailable);
        Assert.True(File.Exists(Path.Combine(meta, "repentogon.png")));
    }

    [Fact]
    public void FindCached_LocatesWhicheverExtensionWasWritten()
    {
        using var temp = new TempDir();
        var meta = temp.Dir("meta");
        File.WriteAllBytes(Path.Combine(meta, "thing.jpg"), new byte[] { 1 });

        Assert.EndsWith("thing.jpg", WorkshopPreviewService.FindCached(meta, "thing"));
        Assert.Null(WorkshopPreviewService.FindCached(meta, "absent"));
    }
}
