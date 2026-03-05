using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Frikadellen.UI.Models;

namespace Frikadellen.UI.Services;

// ────────── DTOs (mirrors frikadellen-fancy REST API) ──────────

/// <summary>Response from GET /api/stats</summary>
public record StatsDto(
    long   SessionProfit,
    long   TotalCoinsSpent,
    long   TotalCoinsEarned,
    int    TotalFlips,
    int    WinCount,
    int    LossCount)
{
    public double WinRate => TotalFlips > 0
        ? Math.Round(WinCount / (double)TotalFlips * 100, 1)
        : 0.0;
}

/// <summary>Single flip entry from GET /api/flips</summary>
public record FlipDto(
    string ItemName,
    long   BuyPrice,
    long   SellPrice,
    long?  BuySpeedMs,
    string Finder,
    string? ItemTag,
    DateTimeOffset Timestamp);

/// <summary>
/// Thin HTTP client for the frikadellen-fancy Rust backend.
/// All methods fail gracefully when the backend is offline.
/// </summary>
public sealed class BackendClient : IDisposable
{
    private readonly HttpClient _http;

    public BackendClient(string baseUrl = "http://localhost:8080")
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout     = TimeSpan.FromSeconds(5),
        };
    }

    // ── Status ──

    /// <summary>GET /api/status — returns null when backend is offline.</summary>
    public async Task<string?> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetStringAsync("/api/status", ct);
        }
        catch { return null; }
    }

    // ── Stats ──

    /// <summary>GET /api/stats — returns null when backend is offline.</summary>
    public async Task<StatsDto?> GetStatsAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<StatsDto>("/api/stats", ct);
        }
        catch { return null; }
    }

    // ── Flips ──

    /// <summary>GET /api/flips — returns an empty array when backend is offline.</summary>
    public async Task<FlipDto[]> GetFlipsAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<FlipDto[]>("/api/flips", ct)
                   ?? Array.Empty<FlipDto>();
        }
        catch { return Array.Empty<FlipDto>(); }
    }

    // ── Bot control ──

    /// <summary>POST /api/start — starts the bot. Returns false on network error.</summary>
    public async Task<bool> StartBotAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await _http.PostAsync("/api/start", content: null, ct);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>POST /api/stop — stops the bot. Returns false on network error.</summary>
    public async Task<bool> StopBotAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await _http.PostAsync("/api/stop", content: null, ct);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ── Config ──

    /// <summary>GET /api/config — returns a raw JSON element, or null when offline.</summary>
    public async Task<JsonElement?> GetConfigAsync(CancellationToken ct = default)
    {
        try
        {
            await using var stream = await _http.GetStreamAsync("/api/config", ct).ConfigureAwait(false);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    /// <summary>POST /api/config — pushes an updated config object. Silently swallows errors.</summary>
    public async Task PostConfigAsync(object configDto, CancellationToken ct = default)
    {
        try { await _http.PostAsJsonAsync("/api/config", configDto, ct); }
        catch { }
    }

    /// <summary>PUT /api/config — fire-and-forget; silently swallows errors.</summary>
    public async Task PutConfigAsync(object configDto, CancellationToken ct = default)
    {
        try { await _http.PutAsJsonAsync("/api/config", configDto, ct); }
        catch { /* backend offline – ignore */ }
    }

    public void Dispose() => _http.Dispose();
}
