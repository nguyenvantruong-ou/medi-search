using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Playwright;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<PlaywrightSearchService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/", () => Results.Text(
    $"MedicineQuickSearch API is running. Version: {SearchRuntime.ExtractorVersion}. Try /health, /api/logs, or POST /api/search.",
    "text/plain; charset=utf-8"));
app.MapGet("/health", () => Results.Ok(new { status = "ok", extractorVersion = SearchRuntime.ExtractorVersion }));
app.MapGet("/api/logs", () => Results.Text(SearchLog.ReadTail(), "text/plain; charset=utf-8"));
app.MapPost("/api/backend/stop", (IHostApplicationLifetime lifetime) =>
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(250);
        lifetime.StopApplication();
    });

    return Results.Ok(new { status = "stopping" });
});

app.MapPost("/api/search", async (
    [FromBody] SearchRequest request,
    PlaywrightSearchService searchService,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Keyword))
    {
        return Results.BadRequest(new { message = "Vui lòng nhập từ khóa." });
    }

    var urls = request.Urls
        .Where(url => !string.IsNullOrWhiteSpace(url))
        .Select(url => url.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(8)
        .ToArray();

    if (urls.Length == 0)
    {
        return Results.BadRequest(new { message = "Vui lòng nhập ít nhất một URL." });
    }

    var response = await searchService.SearchAsync(request.Keyword.Trim(), urls, cancellationToken);
    return Results.Ok(response);
})
.WithName("SearchMedicines")
.WithOpenApi();

app.Run();

public sealed record SearchRequest(string Keyword, string[] Urls);

public sealed record SearchResponse(
    string Keyword,
    DateTimeOffset SearchedAt,
    IReadOnlyList<MedicineResult> Results,
    IReadOnlyList<ProviderStatus> Providers,
    bool FromCache);

public sealed record MedicineResult(
    string Provider,
    string Title,
    string? Price,
    string? Url,
    string? ImageUrl,
    string? ScreenshotUrl,
    string? Snippet,
    int MatchCount);

public sealed record ProviderStatus(
    string Provider,
    string Url,
    string Status,
    int ResultCount,
    long ElapsedMs,
    string? Message);

public static class SearchRuntime
{
    public const string ExtractorVersion = "image-price-screenshot-v5";
}

public sealed class PlaywrightSearchService(ILogger<PlaywrightSearchService> logger) : IAsyncDisposable
{
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ScreenshotTimeout = TimeSpan.FromSeconds(6);
    private static readonly HttpClient WebIndexClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All
    });
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _providerLocks = new(StringComparer.OrdinalIgnoreCase);
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task<SearchResponse> SearchAsync(string keyword, IReadOnlyList<string> urls, CancellationToken cancellationToken)
    {
        SearchLog.Write($"Bắt đầu tìm kiếm: từ khóa='{keyword}', số URL={urls.Count}");
        var tasks = urls.Select(url => SearchProviderAsync(keyword, url, cancellationToken));
        var providerResults = await Task.WhenAll(tasks);
        var results = providerResults
            .SelectMany(result => result.Results)
            .OrderByDescending(result => result.MatchCount)
            .ThenByDescending(result => !string.IsNullOrWhiteSpace(result.ImageUrl))
            .ThenByDescending(result => !string.IsNullOrWhiteSpace(result.Price))
            .ThenByDescending(result => result.Snippet?.Length ?? 0)
            .ToArray();
        var providers = providerResults.Select(result => result.Status).ToArray();

        var response = new SearchResponse(keyword, DateTimeOffset.UtcNow, results, providers, FromCache: false);
        SearchLog.Write($"Hoàn tất tìm kiếm: từ khóa='{keyword}', tổng kết quả={results.Length}");
        return response;
    }

    private async Task<ProviderSearchResult> SearchProviderAsync(string keyword, string urlTemplate, CancellationToken cancellationToken)
    {
        var searchUrl = BuildSearchUrl(urlTemplate, keyword);
        var provider = GetProviderName(searchUrl);
        var gate = _providerLocks.GetOrAdd(provider, _ => new SemaphoreSlim(1, 1));
        var timer = Stopwatch.StartNew();
        SearchLog.Write($"Mở nhà cung cấp: {provider} | {searchUrl}");

        await gate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProviderTimeout);

            var browser = await GetBrowserAsync();
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                Locale = "vi-VN",
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 MedicineQuickSearch/1.0"
            });

            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)ProviderTimeout.TotalMilliseconds);
            await page.GotoAsync(searchUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = (float)ProviderTimeout.TotalMilliseconds
            });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 3000 }).ContinueWith(_ => { });
            await page.WaitForTimeoutAsync(1500);

            var items = await ExtractResultsAsync(page, provider, searchUrl, keyword);
            if (items.Count == 0)
            {
                SearchLog.Write($"Thá»­ fallback web index: {provider}");
                items = await SearchWebIndexFallbackAsync(keyword, provider, searchUrl, timeout.Token);
            }

            items = await AddPageScreenshotsAsync(context, items, cancellationToken);

            timer.Stop();
            SearchLog.Write($"Thành công: {provider}, kết quả={items.Count}, thời gian={timer.ElapsedMilliseconds} ms");

            return new ProviderSearchResult(
                items,
                new ProviderStatus(provider, searchUrl, "thành công", items.Count, timer.ElapsedMilliseconds, null));
        }
        catch (OperationCanceledException)
        {
            timer.Stop();
            SearchLog.Write($"Quá thời gian: {provider}, thời gian={timer.ElapsedMilliseconds} ms");
            return Failed(provider, searchUrl, timer.ElapsedMilliseconds, "quá thời gian", "Trang nhà cung cấp phản hồi quá lâu.");
        }
        catch (PlaywrightException ex)
        {
            logger.LogWarning(ex, "Playwright search failed for {Url}", searchUrl);
            timer.Stop();
            var status = ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ? "quá thời gian" : "thất bại";
            SearchLog.Write($"Lỗi Playwright: {provider}, trạng thái={status}, thông điệp={OneLine(ex.Message)}");
            return Failed(provider, searchUrl, timer.ElapsedMilliseconds, status, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Search failed for {Url}", searchUrl);
            timer.Stop();
            SearchLog.Write($"Lỗi không mong muốn: {provider}, thông điệp={OneLine(ex.Message)}");
            return Failed(provider, searchUrl, timer.ElapsedMilliseconds, "thất bại", ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<MedicineResult>> ExtractResultsAsync(IPage page, string provider, string searchUrl, string keyword)
    {
        var keywordParts = keyword
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length > 1)
            .ToArray();

        var candidates = await page.Locator("a[href], article, li, [data-testid], [class*=product], [class*=Product], [class*=item], [class*=Item], [class*=card], [class*=Card]").EvaluateAllAsync<ResultCandidate[]>(
            @"(nodes) => nodes.map((node) => {
                const linkNode = node.matches && node.matches('a') ? node : node.querySelector && node.querySelector('a[href]');
                const href = linkNode ? linkNode.href : null;
                const imageCandidates = Array.from(node.querySelectorAll ? node.querySelectorAll('img') : (node.matches && node.matches('img') ? [node] : []));
                const text = [
                    node.innerText || node.textContent || '',
                    node.getAttribute && node.getAttribute('title'),
                    node.getAttribute && node.getAttribute('aria-label'),
                    linkNode && linkNode.getAttribute && linkNode.getAttribute('title'),
                    linkNode && linkNode.getAttribute && linkNode.getAttribute('aria-label')
                ].filter(Boolean).join(' ').replace(/\s+/g, ' ').trim();
                const normalizeSrc = (img) => img.currentSrc || img.src || img.getAttribute('data-src') || img.getAttribute('data-original') || img.getAttribute('srcset') || '';
                const imageNode = imageCandidates
                    .map((img, index) => {
                        const src = normalizeSrc(img);
                        const labelText = [img.alt, img.title, img.getAttribute('aria-label')].filter(Boolean).join(' ').toLowerCase();
                        const imageText = [labelText, img.className, img.id, src].filter(Boolean).join(' ').toLowerCase();
                        const rect = img.getBoundingClientRect ? img.getBoundingClientRect() : { width: 0, height: 0 };
                        const width = Math.max(rect.width || img.naturalWidth || Number(img.getAttribute('width')) || 0, 0);
                        const height = Math.max(rect.height || img.naturalHeight || Number(img.getAttribute('height')) || 0, 0);
                        const isBlockedImage = /flag|country|england|english|language|locale|logo|icon|badge|star|rating|sprite|placeholder|base64|data:image|\/smalls\/|united_states|usa_|(^|\s)(viet\s?nam|hoa\s?ky|anh|uk|usa|us|japan|china|france|germany)(\s|$)/i.test(imageText);
                        if (isBlockedImage) return null;
                        const area = width * height;
                        let score = area + Math.max(0, 20 - index);
                        if (/product|product-image|image|thumb|main|gallery|medicine|sku|cdn|upload|media|large|original|object-contain|aspect-square|size-full/i.test(imageText)) score += 2000;
                        if (width > 48 && height > 48) score += 1000;
                        return { img, score };
                    })
                    .filter(Boolean)
                    .sort((a, b) => b.score - a.score)[0]?.img || null;
                const imageSrc = imageNode ? normalizeSrc(imageNode) || null : null;
                const pricePattern = /(?:\d{1,3}(?:[.,]\d{3})+|\d+)\s*(?:₫|đ|vnd|VND|VNĐ)(?:\s*\/\s*[^\s]+)?/g;
                const findPrices = (value) => {
                    const matches = [];
                    let match;
                    pricePattern.lastIndex = 0;
                    while ((match = pricePattern.exec(value || '')) !== null) {
                        matches.push(match[0]);
                    }
                    return matches;
                };
                const priceScopes = Array.from(node.querySelectorAll ? node.querySelectorAll('[class*=price i], [data-testid*=price i], [aria-label*=giá i]') : []);
                const priceTexts = priceScopes
                    .map((item) => item.innerText || item.textContent || '')
                    .filter(Boolean)
                    .join(' ');
                const priceMatches = findPrices(priceTexts);
                const fallbackPriceMatches = findPrices(text);
                const price = (priceMatches[0] || fallbackPriceMatches[0] || null);
                return { Text: text, Href: href, Price: price, ImageSrc: imageSrc };
            }).filter(x => x.Text && x.Text.length > 8 && x.Text.length < 1200).slice(0, 1200)");
        SearchLog.Write($"Đã quét DOM: {provider}, ứng viên={candidates.Length}");

        var resultCandidates = new List<MedicineResult>();

        foreach (var candidate in candidates)
        {
            if (IsLikelyCategory(candidate.Href, candidate.Text) || IsUnavailableProduct(candidate.Text))
            {
                continue;
            }

            var score = keywordParts.Count(part => candidate.Text.Contains(part, StringComparison.OrdinalIgnoreCase));
            if (score == 0 && keywordParts.Length > 0)
            {
                continue;
            }

            var lines = candidate.Text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToArray();

            var title = lines.FirstOrDefault(line => keywordParts.Any(part => line.Contains(part, StringComparison.OrdinalIgnoreCase)))
                ?? lines.FirstOrDefault()
                ?? candidate.Text;

            title = StripCountryPrefix(title);
            title = Truncate(title, 130);

            var absoluteUrl = BuildAbsoluteUrl(candidate.Href, searchUrl) ?? searchUrl;
            var imageUrl = BuildAbsoluteUrl(candidate.ImageSrc, searchUrl);
            if (score == 0 &&
                string.IsNullOrWhiteSpace(candidate.Price) &&
                string.IsNullOrWhiteSpace(imageUrl) &&
                !IsLikelyProductUrl(absoluteUrl))
            {
                continue;
            }

            resultCandidates.Add(new MedicineResult(provider, title, NormalizePriceDisplay(candidate.Price), absoluteUrl, imageUrl, null, Truncate(candidate.Text, 220), score));
        }

        var results = resultCandidates
            .GroupBy(result => BuildResultKey(result), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(result => result.MatchCount)
                .ThenByDescending(result => !string.IsNullOrWhiteSpace(result.ImageUrl))
                .ThenByDescending(result => !string.IsNullOrWhiteSpace(result.Price))
                .ThenByDescending(result => result.Snippet?.Length ?? 0)
                .First())
            .Where(result => !IsUnavailableResult(result))
            .OrderByDescending(result => result.MatchCount)
            .ThenByDescending(result => !string.IsNullOrWhiteSpace(result.ImageUrl))
            .ThenByDescending(result => !string.IsNullOrWhiteSpace(result.Price))
            .ThenByDescending(result => result.Snippet?.Length ?? 0)
            .Take(12)
            .ToList();

        if (results.Count == 0)
        {
            var samples = candidates
                .Take(8)
                .Select(candidate => Truncate(OneLine(candidate.Text), 160))
                .Where(sample => sample.Length > 0);

            SearchLog.Write($"Không tìm thấy kết quả phù hợp: {provider}. Mẫu ứng viên: {string.Join(" | ", samples)}");
        }

        return results;
    }

    private static async Task<IReadOnlyList<MedicineResult>> SearchWebIndexFallbackAsync(
        string keyword,
        string provider,
        string searchUrl,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(searchUrl, UriKind.Absolute, out var providerUri))
        {
            return Array.Empty<MedicineResult>();
        }

        var query = Uri.EscapeDataString($"site:{providerUri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase)} {keyword}");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.bing.com/search?q={query}&count=12");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 MedicineQuickSearch/1.0");
        request.Headers.AcceptLanguage.ParseAdd("vi-VN,vi;q=0.9,en-US;q=0.8,en;q=0.7");

        using var response = await WebIndexClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            SearchLog.Write($"Fallback web index tháº¥t báº¡i: {provider}, HTTP {(int)response.StatusCode}");
            return Array.Empty<MedicineResult>();
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var keywordParts = keyword
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length > 1)
            .ToArray();

        var results = new List<MedicineResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match block in Regex.Matches(html, @"<li\s+class=""b_algo""[\s\S]*?</li>", RegexOptions.IgnoreCase))
        {
            var anchor = Regex.Match(block.Value, @"<a\s+href=""(?<url>https?://[^""]+)""[^>]*>(?<title>[\s\S]*?)</a>", RegexOptions.IgnoreCase);
            if (!anchor.Success)
            {
                continue;
            }

            var url = WebUtility.HtmlDecode(anchor.Groups["url"].Value);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var resultUri) ||
                !resultUri.Host.EndsWith(providerUri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var title = Truncate(CleanHtml(anchor.Groups["title"].Value), 130);
            var snippetMatch = Regex.Match(block.Value, @"<p[^>]*>(?<snippet>[\s\S]*?)</p>", RegexOptions.IgnoreCase);
            var snippet = snippetMatch.Success ? Truncate(CleanHtml(snippetMatch.Groups["snippet"].Value), 220) : title;
            var haystack = $"{title} {snippet} {url}";
            if (IsUnavailableProduct(haystack))
            {
                continue;
            }

            var score = keywordParts.Count(part => haystack.Contains(part, StringComparison.OrdinalIgnoreCase));
            if (score == 0)
            {
                continue;
            }

            var key = NormalizeProductUrl(url);
            if (!seen.Add(string.IsNullOrWhiteSpace(key) ? url : key))
            {
                continue;
            }

            var price = Regex.Match(snippet, @"(?:\d{1,3}(?:[.,]\d{3})+|\d+)\s*(?:₫|đ|vnd|VND|VNĐ)", RegexOptions.IgnoreCase);
            results.Add(new MedicineResult(provider, title, NormalizePriceDisplay(price.Success ? price.Value : null), url, null, null, snippet, score));
            if (results.Count >= 12)
            {
                break;
            }
        }

        SearchLog.Write($"Fallback web index: {provider}, káº¿t quáº£={results.Count}");
        return results
            .OrderByDescending(result => result.MatchCount)
            .ThenByDescending(result => !string.IsNullOrWhiteSpace(result.Price))
            .ThenByDescending(result => result.Snippet?.Length ?? 0)
            .ToArray();
    }

    private static async Task<IReadOnlyList<MedicineResult>> AddPageScreenshotsAsync(
        IBrowserContext context,
        IReadOnlyList<MedicineResult> results,
        CancellationToken cancellationToken)
    {
        if (results.Count == 0)
        {
            return results;
        }

        var updated = new List<MedicineResult>(results);
        var limit = Math.Min(updated.Count, 6);
        for (var index = 0; index < limit; index++)
        {
            var result = updated[index];
            if (string.IsNullOrWhiteSpace(result.Url) ||
                !Uri.TryCreate(result.Url, UriKind.Absolute, out _))
            {
                continue;
            }

            try
            {
                using var screenshotTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                screenshotTimeout.CancelAfter(ScreenshotTimeout);

                var detail = await CapturePageDetailsAsync(context, result.Url, screenshotTimeout.Token);
                if (detail.IsUnavailable)
                {
                    SearchLog.Write($"Bỏ sản phẩm ngừng kinh doanh: {result.Provider} | {result.Url}");
                    updated.RemoveAt(index);
                    index--;
                    limit = Math.Min(updated.Count, 6);
                    continue;
                }

                updated[index] = result with
                {
                    Price = string.IsNullOrWhiteSpace(result.Price) ? NormalizePriceDisplay(detail.Price) : result.Price,
                    ScreenshotUrl = detail.ScreenshotUrl
                };

                if (!string.IsNullOrWhiteSpace(detail.ScreenshotUrl))
                {
                    SearchLog.Write($"Đã chụp màn hình: {result.Provider} | {detail.ScreenshotUrl}");
                }
            }
            catch (Exception ex) when (ex is PlaywrightException or OperationCanceledException or TimeoutException or IOException)
            {
                SearchLog.Write($"Không chụp được màn hình: {result.Provider} | {result.Url} | {OneLine(ex.Message)}");
            }
        }

        return updated;
    }

    private static async Task<PageDetail> CapturePageDetailsAsync(
        IBrowserContext context,
        string url,
        CancellationToken cancellationToken)
    {
        var screenshotDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot", "screenshots");
        Directory.CreateDirectory(screenshotDirectory);

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{SearchRuntime.ExtractorVersion}|{url}")))[..24].ToLowerInvariant();
        var fileName = $"{key}.png";
        var filePath = Path.Combine(screenshotDirectory, fileName);
        var screenshotUrl = $"/screenshots/{fileName}";
        var hasCachedScreenshot = File.Exists(filePath);

        var screenshotPage = await context.NewPageAsync();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await screenshotPage.SetViewportSizeAsync(420, 720);
            await screenshotPage.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 5000
            });
            await screenshotPage.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 1500 }).ContinueWith(_ => { }, cancellationToken);
            var detail = await screenshotPage.Locator("body").EvaluateAsync<PageDetailCandidate>(
                @"(body) => {
                    const text = (body.innerText || body.textContent || '').replace(/\s+/g, ' ').trim();
                    const pricePattern = /(?:\d{1,3}(?:[.,]\d{3})+|\d+)\s*(?:\u20ab|\u0111|vnd|VND|VN\u0110)(?:\s*\/\s*[^\s]+)?/g;
                    const priceScopes = Array.from(body.querySelectorAll('[class*=price i], [data-testid*=price i], [aria-label*=price i]'));
                    const scopedText = priceScopes.map((item) => item.innerText || item.textContent || '').filter(Boolean).join(' ');
                    const price = (scopedText.match(pricePattern) || text.match(pricePattern) || [null])[0];
                    return { Text: text, Price: price };
                }");
            if (!hasCachedScreenshot)
            {
                await screenshotPage.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = filePath,
                    FullPage = false
                });
            }

            return new PageDetail(screenshotUrl, detail.Price, IsUnavailableProduct(detail.Text));
        }
        finally
        {
            await screenshotPage.CloseAsync();
        }
    }

    private static string CleanHtml(string value)
    {
        var withoutTags = Regex.Replace(value, "<.*?>", " ");
        return WebUtility.HtmlDecode(withoutTags).Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static string BuildResultKey(MedicineResult result)
    {
        var urlKey = NormalizeProductUrl(result.Url);
        if (!string.IsNullOrWhiteSpace(urlKey))
        {
            return $"{result.Provider}|{urlKey}";
        }

        return $"{result.Provider}|{NormalizeTitle(result.Title)}";
    }

    private static string NormalizeProductUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "";
        }

        return $"{uri.Host}{uri.AbsolutePath}".TrimEnd('/').ToLowerInvariant();
    }

    private static string NormalizeTitle(string title)
    {
        return string.Join(
            " ",
            title.ToLowerInvariant()
                .Replace(" label", "", StringComparison.OrdinalIgnoreCase)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static bool IsLikelyCategory(string? href, string text)
    {
        var value = $"{href} {text}".ToLowerInvariant();
        return value.Contains("/danh-muc") ||
            value.Contains("/category") ||
            value.Contains("/categories") ||
            value.Contains("/collections") ||
            value.Contains("danh mục") ||
            value.Contains("nhóm hàng") ||
            value.Contains("xem thêm");
    }

    private static bool IsUnavailableResult(MedicineResult result)
    {
        return IsUnavailableProduct($"{result.Title} {result.Snippet} {result.Url}");
    }

    private static bool IsUnavailableProduct(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = NormalizeAvailabilityText(text);
        var compactValue = value.Replace(" ", "", StringComparison.Ordinal);

        return value.Contains("ngung kinh doanh") ||
            compactValue.Contains("ngungkinhdoanh") ||
            value.Contains("tam ngung kinh doanh") ||
            compactValue.Contains("tamngungkinhdoanh") ||
            value.Contains("ngung ban") ||
            compactValue.Contains("ngungban") ||
            value.Contains("tam het hang") ||
            compactValue.Contains("tamhethang") ||
            value.Contains("het hang") ||
            compactValue.Contains("hethang") ||
            value.Contains("sold out") ||
            compactValue.Contains("soldout") ||
            value.Contains("out of stock") ||
            compactValue.Contains("outofstock") ||
            value.Contains("discontinued");
    }

    private static string NormalizeAvailabilityText(string value)
    {
        var withoutDiacritics = RemoveDiacritics(value).ToLowerInvariant();
        return Regex.Replace(withoutDiacritics, @"[^\p{L}\p{N}]+", " ").Trim();
    }

    private static string RemoveDiacritics(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool IsLikelyProductUrl(string url)
    {
        var value = url.ToLowerInvariant();
        return value.Contains("/p/") ||
            value.Contains("/san-pham") ||
            value.Contains("/product") ||
            value.Contains("/products") ||
            value.Contains(".html");
    }

    private static string OneLine(string value)
    {
        return value.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static string StripCountryPrefix(string value)
    {
        return Regex.Replace(
            value,
            @"^(?:Việt\s*Nam|Hoa\s*Kỳ|Mỹ|Anh|Nhật\s*Bản|Trung\s*Quốc|Pháp|Đức)(?:\s+(?:Việt\s*Nam|Hoa\s*Kỳ|Mỹ|Anh|Nhật\s*Bản|Trung\s*Quốc|Pháp|Đức))*\s+",
            "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
    }

    private async Task<IBrowser> GetBrowserAsync()
    {
        if (_browser is { IsConnected: true })
        {
            return _browser;
        }

        await _browserLock.WaitAsync();
        try
        {
            if (_browser is { IsConnected: true })
            {
                return _browser;
            }

            _playwright ??= await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                ExecutablePath = FindChromiumExecutable()
            });

            return _browser;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    private static ProviderSearchResult Failed(string provider, string url, long elapsedMs, string status, string message)
    {
        return new ProviderSearchResult(
            Array.Empty<MedicineResult>(),
            new ProviderStatus(provider, url, status, 0, elapsedMs, message));
    }

    private static string BuildSearchUrl(string urlTemplate, string keyword)
    {
        var encodedKeyword = Uri.EscapeDataString(keyword);
        if (urlTemplate.Contains("{keyword}", StringComparison.OrdinalIgnoreCase))
        {
            return urlTemplate.Replace("{keyword}", encodedKeyword, StringComparison.OrdinalIgnoreCase);
        }

        if (!Uri.TryCreate(urlTemplate, UriKind.Absolute, out var uri))
        {
            return urlTemplate;
        }

        if (IsLikelyProductUrl(urlTemplate))
        {
            return urlTemplate;
        }

        var separator = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
        return $"{uri}{separator}q={encodedKeyword}";
    }

    private static string GetProviderName(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase)
            : url;
    }

    private static string? BuildAbsoluteUrl(string? rawUrl, string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }

        var firstUrl = rawUrl.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault())
            .FirstOrDefault(part => !string.IsNullOrWhiteSpace(part));

        if (string.IsNullOrWhiteSpace(firstUrl) ||
            firstUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        return Uri.TryCreate(baseUri, firstUrl, out var absoluteUri)
            ? absoluteUri.ToString()
            : null;
    }

    private static string? NormalizePriceDisplay(string? price)
    {
        if (string.IsNullOrWhiteSpace(price))
        {
            return null;
        }

        var display = price
            .Replace("VNĐ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("VND", "", StringComparison.OrdinalIgnoreCase)
            .Replace("đ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("₫", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        return display.Contains('/')
            ? display
            : $"{display}/viên";
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength].Trim() + "...";
    }

    private static string? FindChromiumExecutable()
    {
        var systemBrowsers = new[]
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
        };

        var systemBrowser = systemBrowsers.FirstOrDefault(File.Exists);
        if (systemBrowser is not null)
        {
            return systemBrowser;
        }

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ms-playwright");

        if (!Directory.Exists(root))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(root, "chrome.exe", SearchOption.AllDirectories)
            .FirstOrDefault(path => path.Contains("chromium-", StringComparison.OrdinalIgnoreCase));
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
        _browserLock.Dispose();
    }

    private sealed record ProviderSearchResult(IReadOnlyList<MedicineResult> Results, ProviderStatus Status);

    private sealed record PageDetail(string? ScreenshotUrl, string? Price, bool IsUnavailable);

    private sealed class PageDetailCandidate
    {
        public string Text { get; set; } = "";
        public string? Price { get; set; }
    }

    private sealed class ResultCandidate
    {
        public string Text { get; set; } = "";
        public string? Href { get; set; }
        public string? Price { get; set; }
        public string? ImageSrc { get; set; }
    }
}

public static class SearchLog
{
    private static readonly object Gate = new();
    private static readonly string LogDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
    private static readonly string LogPath = Path.Combine(LogDirectory, "search.log");

    public static void Write(string message)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
    }

    public static string ReadTail(int maxLines = 300)
    {
        lock (Gate)
        {
            if (!File.Exists(LogPath))
            {
                return "Chưa có nhật ký tìm kiếm.";
            }

            return string.Join(Environment.NewLine, File.ReadLines(LogPath).TakeLast(maxLines));
        }
    }
}
