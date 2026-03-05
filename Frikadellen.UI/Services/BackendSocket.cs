using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Frikadellen.UI.Models;

namespace Frikadellen.UI.Services;

/// <summary>State-change payload from the WebSocket <c>state_change</c> message.</summary>
public record WsStateChange(string State, long? Purse, int? QueueDepth);

/// <summary>
/// WebSocket client for the frikadellen-fancy Rust backend.
/// Connects to <c>ws://localhost:PORT/ws</c>, auto-reconnects with exponential
/// back-off, and dispatches all events on the Avalonia UI thread.
/// </summary>
public sealed class BackendSocket : IDisposable
{
    private readonly string _wsUrl;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    // ── Events (all fired on the Avalonia UI thread) ──

    /// <summary>A completed flip arrived via <c>flip_received</c>.</summary>
    public event Action<FlipRecord>? FlipReceived;

    /// <summary>Any non-flip event arrived (item_purchased, item_sold, bazaar_trade, error).</summary>
    public event Action<EventItem>? EventReceived;

    /// <summary>A <c>state_change</c> message arrived.</summary>
    public event Action<WsStateChange>? StateChanged;

    /// <summary>Connection state changed — <c>true</c> = connected, <c>false</c> = disconnected.</summary>
    public event Action<bool>? ConnectionStateChanged;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public BackendSocket(string wsUrl) => _wsUrl = wsUrl;

    // ── Public API ──

    /// <summary>
    /// Starts connecting in the background. Returns immediately.
    /// Cancels any previous connect-loop before starting a new one.
    /// </summary>
    public void Connect()
    {
        if (_disposed) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = ConnectLoopAsync(_cts.Token);
    }

    /// <summary>Gracefully disconnects and stops auto-reconnect.</summary>
    public void Disconnect()
    {
        _cts?.Cancel();
        _ws?.Abort();
    }

    // ── Private implementation ──

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        var backoffMs = 1_000;

        while (!ct.IsCancellationRequested)
        {
            ClientWebSocket? ws = null;
            try
            {
                ws = new ClientWebSocket();
                _ws = ws;

                await ws.ConnectAsync(new Uri(_wsUrl), ct).ConfigureAwait(false);
                backoffMs = 1_000; // reset on successful connect

                FireConnectionState(true);
                await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { /* backend offline or reset — will retry */ }
            finally
            {
                if (ReferenceEquals(_ws, ws)) _ws = null;
                ws?.Dispose();
            }

            FireConnectionState(false);

            if (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(backoffMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                backoffMs = Math.Min(backoffMs * 2, 30_000);
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16_384];
        var sb = new StringBuilder();

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            sb.Clear();
            WebSocketReceiveResult result;

            do
            {
                result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.Count > 0)
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            if (sb.Length > 0)
                ParseAndDispatch(sb.ToString());
        }
    }

    private void ParseAndDispatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp)) return;
            var msgType = typeProp.GetString();

            switch (msgType)
            {
                case "flip_received":
                {
                    var flip = new FlipRecord
                    {
                        ItemName   = GetStr(root, "item",   "Unknown"),
                        BuyPrice   = GetLong(root, "buy_price"),
                        SellPrice  = GetLong(root, "sell_price"),
                        BuySpeedMs = root.TryGetProperty("buy_speed_ms", out var sp) ? sp.GetInt64() : null,
                        Finder     = GetStr(root, "finder", "SNIPER"),
                        ItemTag    = root.TryGetProperty("item_tag", out var it) ? it.GetString() : null,
                    };
                    Dispatcher.UIThread.Post(() => FlipReceived?.Invoke(flip));
                    break;
                }

                case "item_purchased":
                {
                    var item  = GetStr(root, "item",  "Unknown");
                    var price = GetLong(root, "price");
                    var evt = new EventItem
                    {
                        Type      = "Purchase",
                        Tag       = "purchase",
                        Avatar    = "🛒",
                        Message   = $"Bought {item} for {Fmt.Coins(price)}",
                        Timestamp = DateTimeOffset.Now,
                    };
                    Dispatcher.UIThread.Post(() => EventReceived?.Invoke(evt));
                    break;
                }

                case "item_sold":
                {
                    var item   = GetStr(root, "item",   "Unknown");
                    var price  = GetLong(root, "price");
                    var profit = GetLong(root, "profit");
                    var evt = new EventItem
                    {
                        Type      = "Sale",
                        Tag       = "sold",
                        Avatar    = "⚡",
                        Message   = $"Sold {item} for {Fmt.Coins(price)} — profit: +{Fmt.Coins(profit)}",
                        Timestamp = DateTimeOffset.Now,
                    };
                    Dispatcher.UIThread.Post(() => EventReceived?.Invoke(evt));
                    break;
                }

                case "bazaar_trade":
                {
                    var item = GetStr(root, "item", "Unknown");
                    var side = GetStr(root, "side", "BUY");
                    var qty  = root.TryGetProperty("quantity",      out var q) ? q.GetInt32()    : 1;
                    var ppu  = root.TryGetProperty("price_per_unit",out var p) ? p.GetDouble()   : 0.0;
                    var evt = new EventItem
                    {
                        Type      = "Bazaar",
                        Tag       = "bazaar",
                        Avatar    = "📦",
                        Message   = $"[BZ] {side}: {item} x{qty} @ {Fmt.Coins((long)ppu)}/unit",
                        Timestamp = DateTimeOffset.Now,
                    };
                    Dispatcher.UIThread.Post(() => EventReceived?.Invoke(evt));
                    break;
                }

                case "state_change":
                {
                    var state = GetStr(root, "state", "Unknown");
                    long? purse      = root.TryGetProperty("purse",       out var pu) ? pu.GetInt64() : null;
                    int?  queueDepth = root.TryGetProperty("queue_depth", out var qd) ? qd.GetInt32() : null;
                    var sc = new WsStateChange(state, purse, queueDepth);
                    Dispatcher.UIThread.Post(() => StateChanged?.Invoke(sc));
                    break;
                }

                case "error":
                {
                    var msg = GetStr(root, "message", "Unknown error");
                    var evt = new EventItem
                    {
                        Type      = "Error",
                        Tag       = "error",
                        Avatar    = "🔴",
                        Message   = msg,
                        Timestamp = DateTimeOffset.Now,
                    };
                    Dispatcher.UIThread.Post(() => EventReceived?.Invoke(evt));
                    break;
                }
            }
        }
        catch { /* discard malformed messages */ }
    }

    // ── Helpers ──

    private static string GetStr(JsonElement el, string key, string fallback)
        => el.TryGetProperty(key, out var v) ? v.GetString() ?? fallback : fallback;

    private static long GetLong(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) ? v.GetInt64() : 0L;

    private void FireConnectionState(bool connected)
        => Dispatcher.UIThread.Post(() => ConnectionStateChanged?.Invoke(connected));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _ws?.Abort();
        _cts?.Dispose();
    }
}
