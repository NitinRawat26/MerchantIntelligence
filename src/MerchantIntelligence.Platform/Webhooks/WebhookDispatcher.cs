using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MerchantIntelligence.Platform.Rules;
using MerchantIntelligence.Platform.Storage;
using Microsoft.Extensions.Logging;

namespace MerchantIntelligence.Platform.Webhooks;

public sealed record WebhookSubscription(string Id, string Url, IReadOnlyList<string> Events, bool Enabled, DateTimeOffset CreatedAt);

public sealed record WebhookDelivery(long Id, string WebhookId, string Event, int Attempts, int? StatusCode, string? Error, bool Delivered, DateTimeOffset CreatedAt, DateTimeOffset? LastAttemptAt);

/// <summary>
/// Outbound webhooks for onboarding platforms. Payloads are signed with HMAC-SHA256 over the raw body
/// (<c>X-MI-Signature: sha256=&lt;hex&gt;</c>, plus <c>X-MI-Event</c> and <c>X-MI-Delivery</c> headers); receivers
/// should verify with the shared secret. Delivery is fire-and-forget with 3 attempts and exponential backoff;
/// every attempt is logged so failed deliveries can be replayed.
/// </summary>
public sealed class WebhookDispatcher
{
    public const string HttpClientName = "MerchantIntelligence.Webhooks";
    public static readonly string[] KnownEvents =
        ["case.created", "case.assigned", "case.status_changed", "case.decided", "rules.published", "model.promoted", "model.drift_alert", "*"];

    private readonly PlatformDatabase _db;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<WebhookDispatcher> _logger;
    private readonly TimeSpan[] _backoff;

    public WebhookDispatcher(PlatformDatabase db, IHttpClientFactory http, ILogger<WebhookDispatcher> logger, TimeSpan[]? backoff = null)
    {
        _db = db;
        _http = http;
        _logger = logger;
        _backoff = backoff ?? [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)];
    }

    public WebhookSubscription Register(string url, string secret, IReadOnlyList<string> events)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Webhook url must be an absolute http(s) URL.", nameof(url));
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 16)
            throw new ArgumentException("Webhook secret must be at least 16 characters.", nameof(secret));
        var unknown = events.Where(e => !KnownEvents.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0) throw new ArgumentException($"Unknown events: {string.Join(", ", unknown)}. Known: {string.Join(", ", KnownEvents)}", nameof(events));

        var id = "whk_" + Guid.NewGuid().ToString("N")[..12];
        var now = DateTimeOffset.UtcNow;
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO webhooks (id, url, secret, events, enabled, created_at) VALUES ($id, $url, $secret, $events, 1, $now)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$url", url);
        cmd.Parameters.AddWithValue("$secret", secret);
        cmd.Parameters.AddWithValue("$events", string.Join(',', events));
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.ExecuteNonQuery();
        return new WebhookSubscription(id, url, events, true, now);
    }

    public IReadOnlyList<WebhookSubscription> List()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, url, events, enabled, created_at FROM webhooks ORDER BY created_at";
        var list = new List<WebhookSubscription>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new WebhookSubscription(r.GetString(0), r.GetString(1), r.GetString(2).Split(','), r.GetInt32(3) == 1, DateTimeOffset.Parse(r.GetString(4))));
        return list;
    }

    public bool Remove(string id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM webhooks WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<WebhookDelivery> Deliveries(string? webhookId = null, int limit = 100)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, webhook_id, event, attempts, status_code, error, delivered, created_at, last_attempt_at FROM webhook_deliveries WHERE ($w IS NULL OR webhook_id = $w) ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$w", (object?)webhookId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", limit);
        var list = new List<WebhookDelivery>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new WebhookDelivery(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetInt32(6) == 1,
                DateTimeOffset.Parse(r.GetString(7)), r.IsDBNull(8) ? null : DateTimeOffset.Parse(r.GetString(8))));
        return list;
    }

    /// <summary>Queues the event for every matching subscription; returns the delivery tasks so callers (tests) can await them.</summary>
    public IReadOnlyList<Task> Publish(string eventName, object payload)
    {
        var body = JsonSerializer.Serialize(new { @event = eventName, occurredAt = DateTimeOffset.UtcNow, data = payload }, RulesEngine.JsonOptions);
        var tasks = new List<Task>();
        foreach (var (id, url, secret) in Subscribers(eventName))
        {
            var deliveryId = InsertDelivery(id, eventName, body);
            tasks.Add(Task.Run(() => DeliverAsync(deliveryId, url, secret, eventName, body)));
        }
        return tasks;
    }

    public static string Sign(string secret, string body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private async Task DeliverAsync(long deliveryId, string url, string secret, string eventName, string body)
    {
        var client = _http.CreateClient(HttpClientName);
        for (var attempt = 1; attempt <= _backoff.Length + 1; attempt++)
        {
            int? status = null;
            string? error = null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("X-MI-Event", eventName);
                req.Headers.Add("X-MI-Delivery", deliveryId.ToString());
                req.Headers.Add("X-MI-Signature", Sign(secret, body));
                using var resp = await client.SendAsync(req);
                status = (int)resp.StatusCode;
                if (resp.IsSuccessStatusCode)
                {
                    RecordAttempt(deliveryId, attempt, status, null, delivered: true);
                    return;
                }
                error = $"HTTP {status}";
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
            }
            RecordAttempt(deliveryId, attempt, status, error, delivered: false);
            _logger.LogWarning("Webhook delivery {Id} attempt {Attempt} to {Url} failed: {Error}", deliveryId, attempt, url, error);
            if (attempt <= _backoff.Length) await Task.Delay(_backoff[attempt - 1]);
        }
    }

    private IEnumerable<(string Id, string Url, string Secret)> Subscribers(string eventName)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, url, secret, events FROM webhooks WHERE enabled = 1";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var events = r.GetString(3).Split(',');
            if (events.Contains("*") || events.Contains(eventName, StringComparer.OrdinalIgnoreCase))
                yield return (r.GetString(0), r.GetString(1), r.GetString(2));
        }
    }

    private long InsertDelivery(string webhookId, string eventName, string body)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO webhook_deliveries (webhook_id, event, payload, attempts, created_at) VALUES ($w, $e, $p, 0, $t); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$w", webhookId);
        cmd.Parameters.AddWithValue("$e", eventName);
        cmd.Parameters.AddWithValue("$p", body);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        return (long)cmd.ExecuteScalar()!;
    }

    private void RecordAttempt(long deliveryId, int attempts, int? status, string? error, bool delivered)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE webhook_deliveries SET attempts = $a, status_code = $s, error = $e, delivered = $d, last_attempt_at = $t WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", deliveryId);
        cmd.Parameters.AddWithValue("$a", attempts);
        cmd.Parameters.AddWithValue("$s", (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", delivered ? 1 : 0);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}
