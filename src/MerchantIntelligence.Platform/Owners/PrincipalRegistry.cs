using MerchantIntelligence.Kyb;
using MerchantIntelligence.Platform.Storage;

namespace MerchantIntelligence.Platform.Owners;

/// <summary>
/// Remembers which people were declared as principals on which applications, so a later application can be checked for
/// the same person reappearing behind a different business (duplicate or velocity pattern). Only the normalised name /
/// date-of-birth key, the merchant and the assessment id are stored.
/// </summary>
public sealed class PrincipalRegistry
{
    private readonly PlatformDatabase _db;

    public PrincipalRegistry(PlatformDatabase db)
    {
        _db = db;
        EnsureSchema();
    }

    /// <summary>Earlier applications for a different merchant on which this person was a principal, newest first.</summary>
    public IReadOnlyList<PriorApplication> PriorApplications(BeneficialOwner owner, string currentMerchant, string currentAssessmentId)
    {
        var key = OwnerIdentityAssessor.PrincipalKey(owner);
        var nameOnly = key[..key.IndexOf('|')] + "|";
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT assessment_id, merchant_name, seen_at FROM applicant_principals
            WHERE (principal_key = $key OR principal_key = $nameOnly OR ($dobless = 1 AND principal_key LIKE $namePrefix))
              AND assessment_id <> $asmt AND merchant_key <> $merchant
            ORDER BY seen_at DESC LIMIT 50
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$nameOnly", nameOnly);
        cmd.Parameters.AddWithValue("$dobless", key.EndsWith('|') ? 1 : 0);
        cmd.Parameters.AddWithValue("$namePrefix", nameOnly + "%");
        cmd.Parameters.AddWithValue("$asmt", currentAssessmentId);
        cmd.Parameters.AddWithValue("$merchant", MerchantKey(currentMerchant));
        var list = new List<PriorApplication>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new PriorApplication(r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2), System.Globalization.CultureInfo.InvariantCulture)));
        return list;
    }

    public void Remember(string assessmentId, string merchantName, IEnumerable<BeneficialOwner> owners, DateTimeOffset seenAt)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var o in owners)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO applicant_principals (assessment_id, principal_key, full_name, merchant_key, merchant_name, seen_at)
                VALUES ($a, $k, $n, $mk, $m, $t)
                """;
            cmd.Parameters.AddWithValue("$a", assessmentId);
            cmd.Parameters.AddWithValue("$k", OwnerIdentityAssessor.PrincipalKey(o));
            cmd.Parameters.AddWithValue("$n", o.FullName);
            cmd.Parameters.AddWithValue("$mk", MerchantKey(merchantName));
            cmd.Parameters.AddWithValue("$m", merchantName);
            cmd.Parameters.AddWithValue("$t", seenAt.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static string MerchantKey(string name) => new(name.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    private void EnsureSchema()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS applicant_principals (
                assessment_id TEXT NOT NULL,
                principal_key TEXT NOT NULL,
                full_name TEXT NOT NULL,
                merchant_key TEXT NOT NULL,
                merchant_name TEXT NOT NULL,
                seen_at TEXT NOT NULL,
                PRIMARY KEY (assessment_id, principal_key)
            );
            CREATE INDEX IF NOT EXISTS ix_applicant_principals_key ON applicant_principals (principal_key);
            """;
        cmd.ExecuteNonQuery();
    }
}
