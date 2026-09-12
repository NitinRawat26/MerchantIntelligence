using Microsoft.Data.Sqlite;

namespace MerchantIntelligence.Platform.Storage;

public sealed class PlatformOptions
{
    /// <summary>SQLite file path; ":memory:" keeps everything in-process (tests).</summary>
    public string DatabasePath { get; set; } = "data/platform.db";
    public string ModelsDirectory { get; set; } = "models";
}

/// <summary>Owns the SQLite connection string and schema. One file holds cases, audit, rules, decision log, webhooks.</summary>
public sealed class PlatformDatabase : IDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection? _keepAlive;

    public PlatformDatabase(PlatformOptions options)
    {
        var path = options.DatabasePath;
        if (path == ":memory:")
        {
            // Shared in-memory DB must keep at least one connection open or it is dropped.
            _connectionString = $"Data Source=platform-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();
        }
        else
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _connectionString = $"Data Source={path}";
        }
        Migrate();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Migrate()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS cases (
                id TEXT PRIMARY KEY,
                merchant_name TEXT NOT NULL,
                external_ref TEXT,
                status TEXT NOT NULL,
                assigned_to TEXT,
                priority TEXT NOT NULL,
                risk_score INTEGER,
                risk_tier TEXT,
                rules_outcome TEXT,
                final_decision TEXT,
                snapshot_json TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_cases_status ON cases(status);
            CREATE TABLE IF NOT EXISTS case_notes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                case_id TEXT NOT NULL REFERENCES cases(id),
                author TEXT NOT NULL,
                body TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS audit_events (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                case_id TEXT,
                actor TEXT NOT NULL,
                action TEXT NOT NULL,
                detail_json TEXT,
                occurred_at TEXT NOT NULL,
                prev_hash TEXT NOT NULL,
                hash TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_audit_case ON audit_events(case_id);
            CREATE TABLE IF NOT EXISTS rule_sets (
                version INTEGER PRIMARY KEY AUTOINCREMENT,
                json TEXT NOT NULL,
                author TEXT NOT NULL,
                comment TEXT,
                created_at TEXT NOT NULL,
                active INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS assessment_workflows (
                version INTEGER PRIMARY KEY AUTOINCREMENT,
                json TEXT NOT NULL,
                author TEXT NOT NULL,
                comment TEXT,
                created_at TEXT NOT NULL,
                active INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS decision_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                case_id TEXT,
                model_version TEXT NOT NULL,
                mcc REAL NOT NULL, annual_volume REAL NOT NULL, average_ticket REAL NOT NULL, highest_ticket REAL NOT NULL,
                match_found INTEGER NOT NULL, existing_relationship INTEGER NOT NULL,
                predicted TEXT NOT NULL, confidence REAL NOT NULL,
                challenger_predicted TEXT, challenger_confidence REAL,
                actual TEXT,
                scored_at TEXT NOT NULL,
                outcome_at TEXT
            );
            CREATE TABLE IF NOT EXISTS model_registry (
                version TEXT PRIMARY KEY,
                path TEXT NOT NULL,
                role TEXT NOT NULL,
                metrics_json TEXT,
                training_rows INTEGER,
                registered_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS webhooks (
                id TEXT PRIMARY KEY,
                url TEXT NOT NULL,
                secret TEXT NOT NULL,
                events TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS webhook_deliveries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                webhook_id TEXT NOT NULL,
                event TEXT NOT NULL,
                payload TEXT NOT NULL,
                attempts INTEGER NOT NULL,
                status_code INTEGER,
                error TEXT,
                delivered INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                last_attempt_at TEXT
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _keepAlive?.Dispose();
}
