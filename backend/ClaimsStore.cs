using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http.Json;

namespace Claims.Api;

public sealed class ClaimsStore(IConfiguration config, IWebHostEnvironment environment,
    IOptions<JsonOptions> jsonOptions, TimeProvider clock)
{
    private const string ClaimColumns = "id, claimant_id, category, incident_date, description, reported_loss_minor, " +
        "status, assignee_id, version, updated_at";

    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath(config, environment), ForeignKeys = true, DefaultTimeout = 5
    }.ToString();

    public void Initialize()
    {
        using var db = Open();
        using var command = Command(db, null, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS claims (
                id TEXT PRIMARY KEY, claimant_id TEXT NOT NULL, category TEXT NOT NULL,
                incident_date TEXT NOT NULL, description TEXT NOT NULL, reported_loss_minor INTEGER NOT NULL,
                status TEXT NOT NULL, assignee_id TEXT, version INTEGER NOT NULL, updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS claims_by_claimant ON claims(claimant_id, updated_at);
            CREATE TABLE IF NOT EXISTS claim_history (
                claim_id TEXT NOT NULL REFERENCES claims(id), version INTEGER NOT NULL, status TEXT NOT NULL,
                actor TEXT NOT NULL, note TEXT, occurred_at TEXT NOT NULL, PRIMARY KEY (claim_id, version)
            );
            CREATE TABLE IF NOT EXISTS outbox (
                sequence INTEGER PRIMARY KEY, claim_id TEXT NOT NULL REFERENCES claims(id),
                event_json TEXT NOT NULL
            );
            """);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<Claim> List(DemoUser actor)
    {
        using var db = Open();
        using var command = Command(db, null, $"SELECT {ClaimColumns} FROM claims " +
            "WHERE ($claimant IS NULL OR claimant_id = $claimant) ORDER BY updated_at DESC, id",
            ("$claimant", actor.Role == DemoAccounts.Claimant ? actor.Id : null));
        using var reader = command.ExecuteReader();
        var claims = new List<Claim>();
        while (reader.Read()) claims.Add(ReadClaim(reader));
        return claims;
    }

    public ClaimDetail? Get(Guid id, DemoUser actor)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        var claim = Find(db, tx, id, actor);
        return claim is null ? null : ReadDetail(db, tx, claim);
    }

    public ClaimDetail Create(DemoUser actor, ClaimCategory category, DateOnly date, string description, long lossMinor)
    {
        var claim = new Claim(Guid.NewGuid(), actor.Id, category, date, description, lossMinor,
            ClaimStatus.Submitted, null, 1, clock.GetUtcNow());
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var command = Command(db, tx, $"INSERT INTO claims ({ClaimColumns}) " +
            "VALUES ($id, $claimant, $category, $date, $description, $loss, $status, NULL, 1, $updated)",
            ("$id", claim.Id.ToString()), ("$claimant", claim.ClaimantId), ("$category", category.ToString()),
            ("$date", date.ToString("O", CultureInfo.InvariantCulture)), ("$description", description),
            ("$loss", lossMinor), ("$status", claim.Status.ToString()), ("$updated", Timestamp(claim.UpdatedAt)));
        command.ExecuteNonQuery();
        AppendChange(db, tx, claim, null, actor, null);
        var detail = ReadDetail(db, tx, claim);
        tx.Commit();
        return detail;
    }

    public ClaimDetail? Change(Guid id, DemoUser actor, ClaimOperation operation, int expectedVersion, string? note,
        ClaimStatus? decision = null)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var current = Find(db, tx, id, actor);
        if (current is null) return null;

        var next = ClaimWorkflow.Apply(current, operation, actor, expectedVersion, decision, clock.GetUtcNow());
        using var command = Command(db, tx, """
            UPDATE claims SET status = $status, assignee_id = $assignee, version = $version, updated_at = $updated
            WHERE id = $id
            """, ("$status", next.Status.ToString()), ("$assignee", next.AssigneeId), ("$version", next.Version),
            ("$updated", Timestamp(next.UpdatedAt)), ("$id", id.ToString()));
        command.ExecuteNonQuery();
        AppendChange(db, tx, next, current.Status, actor, note);
        var detail = ReadDetail(db, tx, next);
        tx.Commit();
        return detail;
    }

    public Workload GetWorkload()
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT status, COUNT(*), SUM(reported_loss_minor) FROM claims GROUP BY status");
        using var reader = command.ExecuteReader();
        var totals = new Dictionary<ClaimStatus, int>();
        long outstanding = 0;
        while (reader.Read())
        {
            var status = Enum.Parse<ClaimStatus>(reader.GetString(0));
            totals[status] = reader.GetInt32(1);
            if (status is not (ClaimStatus.Settled or ClaimStatus.Rejected)) outstanding += reader.GetInt64(2);
        }
        return new(totals.GetValueOrDefault(ClaimStatus.Submitted), totals.GetValueOrDefault(ClaimStatus.InReview),
            totals.GetValueOrDefault(ClaimStatus.InformationRequested), totals.GetValueOrDefault(ClaimStatus.Settled),
            totals.GetValueOrDefault(ClaimStatus.Rejected), outstanding, "MYR");
    }

    public (long Sequence, string ClaimId, string Json)? NextEvent()
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT sequence, claim_id, event_json FROM outbox ORDER BY sequence LIMIT 1");
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    public void MarkPublished(long sequence)
    {
        using var db = Open();
        using var command = Command(db, null, "DELETE FROM outbox WHERE sequence = $sequence", ("$sequence", sequence));
        command.ExecuteNonQuery();
    }

    private void AppendChange(SqliteConnection db, SqliteTransaction tx, Claim claim, ClaimStatus? previous,
        DemoUser actor, string? note)
    {
        using var history = Command(db, tx, """
            INSERT INTO claim_history (claim_id, version, status, actor, note, occurred_at)
            VALUES ($id, $version, $status, $actor, $note, $at)
            """, ("$id", claim.Id.ToString()), ("$version", claim.Version), ("$status", claim.Status.ToString()),
            ("$actor", actor.Role), ("$note", note), ("$at", Timestamp(claim.UpdatedAt)));
        history.ExecuteNonQuery();

        var message = new StatusChanged(Guid.NewGuid(), claim.Id, claim.Version, previous, claim.Status, claim.UpdatedAt);
        using var outbox = Command(db, tx, "INSERT INTO outbox (claim_id, event_json) VALUES ($id, $json)",
            ("$id", claim.Id.ToString()), ("$json", JsonSerializer.Serialize(message, jsonOptions.Value.SerializerOptions)));
        outbox.ExecuteNonQuery();
    }

    private static Claim? Find(SqliteConnection db, SqliteTransaction tx, Guid id, DemoUser actor)
    {
        using var command = Command(db, tx, $"SELECT {ClaimColumns} FROM claims " +
            "WHERE id = $id AND ($claimant IS NULL OR claimant_id = $claimant)", ("$id", id.ToString()),
            ("$claimant", actor.Role == DemoAccounts.Claimant ? actor.Id : null));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadClaim(reader) : null;
    }

    private static ClaimDetail ReadDetail(SqliteConnection db, SqliteTransaction tx, Claim claim)
    {
        using var command = Command(db, tx, """
            SELECT version, status, actor, note, occurred_at FROM claim_history WHERE claim_id = $id ORDER BY version
            """, ("$id", claim.Id.ToString()));
        using var reader = command.ExecuteReader();
        var history = new List<ClaimHistoryEntry>();
        while (reader.Read())
            history.Add(new(reader.GetInt32(0), Enum.Parse<ClaimStatus>(reader.GetString(1)), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
        return new(claim, history);
    }

    private static Claim ReadClaim(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(0)), reader.GetString(1),
        Enum.Parse<ClaimCategory>(reader.GetString(2)), DateOnly.ParseExact(reader.GetString(3), "O", CultureInfo.InvariantCulture),
        reader.GetString(4), reader.GetInt64(5), Enum.Parse<ClaimStatus>(reader.GetString(6)),
        reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt32(8),
        DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture));

    private static string Timestamp(DateTimeOffset time) => time.ToString("O", CultureInfo.InvariantCulture);

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        try
        {
            db.Open();
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static string DatabasePath(IConfiguration config, IWebHostEnvironment environment)
    {
        var path = Path.GetFullPath(config["Claims:DatabasePath"] ?? "data/claims.db", environment.ContentRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
