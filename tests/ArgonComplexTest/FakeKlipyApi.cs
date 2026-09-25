namespace ArgonComplexTest;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Argon.Features.Integrations.Klipy;

/// <summary>
/// The Klipy API, answered in process.
/// </summary>
/// <remarks>
/// <para>Installed as the primary handler of the <c>IKlipyService</c> typed client, so
/// <c>KlipyService</c>, its resilience pipeline and everything after the response — the media
/// download, the S3 put, the <c>Files</c> row — run as shipped. Without it the first GIF a test saves
/// that is not already cached goes out to the real API, which a test run must never do.</para>
///
/// <para>A slug nobody published answers the way Klipy does for an unknown slug: an empty page.</para>
/// </remarks>
public sealed class FakeKlipyApi
{
    public const string MediaHost = "media.klipy.test";

    private readonly ConcurrentDictionary<string, byte[]> media   = new();
    private readonly ConcurrentDictionary<string, int>    lookups = new();

    /// <summary>Makes <paramref name="slug"/> resolvable, with <paramref name="webp"/> as its md/webp rendition.</summary>
    public void Publish(string slug, byte[] webp) => media[slug] = webp;

    /// <summary>How many times the item endpoint was asked for <paramref name="slug"/>.</summary>
    public int LookupsOf(string slug) => lookups.GetValueOrDefault(slug);

    public HttpMessageHandler CreateHandler() => new Handler(this);

    private sealed class Handler(FakeKlipyApi api) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;

            if (uri.Host == MediaHost)
            {
                var slug = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
                return Task.FromResult(api.media.TryGetValue(slug, out var bytes)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            if (uri.AbsolutePath.EndsWith("/gifs/items", StringComparison.Ordinal))
            {
                var slugs = QueryValue(uri, "slugs")?.Split(',') ?? [];
                var items = new List<KlipyMediaItem>();

                foreach (var slug in slugs)
                {
                    api.lookups.AddOrUpdate(slug, 1, (_, n) => n + 1);

                    if (api.media.ContainsKey(slug))
                        items.Add(ItemFor(slug));
                }

                return Json(new KlipyResponse<KlipyPagedData<KlipyMediaItem>>
                {
                    Result = true,
                    Data   = new KlipyPagedData<KlipyMediaItem> { Data = items, HasNext = false }
                });
            }

            if (uri.AbsolutePath.EndsWith("/gifs/categories", StringComparison.Ordinal))
                return Json(new KlipyResponse<KlipyCategoriesData> { Result = true, Data = new KlipyCategoriesData { Categories = [] } });

            // Search finds every published slug containing the query; trending is always empty,
            // because its cache key is shared by the whole run.
            if (uri.AbsolutePath.EndsWith("/gifs/search", StringComparison.Ordinal))
            {
                var query = QueryValue(uri, "q") ?? "";
                return Json(new KlipyResponse<KlipyPagedData<KlipyMediaItem>>
                {
                    Result = true,
                    Data = new KlipyPagedData<KlipyMediaItem>
                    {
                        Data    = api.media.Keys.Where(s => s.Contains(query, StringComparison.OrdinalIgnoreCase)).Select(ItemFor).ToList(),
                        HasNext = false
                    }
                });
            }

            if (uri.AbsolutePath.EndsWith("/gifs/trending", StringComparison.Ordinal))
                return Json(new KlipyResponse<KlipyPagedData<KlipyMediaItem>>
                {
                    Result = true,
                    Data   = new KlipyPagedData<KlipyMediaItem> { Data = [], HasNext = false }
                });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static KlipyMediaItem ItemFor(string slug)
            => new()
            {
                Slug  = slug,
                Title = slug,
                File = new KlipyDimensions
                {
                    Md = new KlipyFileTypes
                    {
                        Webp = new KlipyFileMetadata
                        {
                            Url    = $"https://{MediaHost}/{Uri.EscapeDataString(slug)}",
                            Width  = 320,
                            Height = 240
                        }
                    }
                }
            };

        private static Task<HttpResponseMessage> Json<T>(T body)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });

        private static string? QueryValue(Uri uri, string name)
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var at = pair.IndexOf('=');
                if (at > 0 && pair[..at] == name)
                    return Uri.UnescapeDataString(pair[(at + 1)..]);
            }

            return null;
        }
    }
}
