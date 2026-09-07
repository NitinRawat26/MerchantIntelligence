using MerchantIntelligence.Kyb.Matching;

namespace MerchantIntelligence.Kyb.Sanctions;

/// <summary>
/// In-memory inverted index over entity names and aliases. Candidate retrieval is by shared
/// normalised token (or 3-char prefix for typo tolerance); candidates are then fuzzy-scored.
/// </summary>
public sealed class SanctionsIndex
{
    private static readonly HashSet<string> StopTokens = new(StringComparer.Ordinal)
    {
        "the", "and", "of", "inc", "llc", "ltd", "co", "company", "corp", "corporation", "limited", "group",
        "al", "el", "bin", "ibn", "abu", "de", "da", "del", "la", "le", "van", "von", "mr", "mrs", "dr"
    };

    private readonly List<SanctionedEntity> _entities = new();
    private readonly Dictionary<string, List<int>> _tokenPostings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _prefixPostings = new(StringComparer.Ordinal);

    public int Count => _entities.Count;

    public void Add(SanctionedEntity entity)
    {
        var id = _entities.Count;
        _entities.Add(entity);
        foreach (var name in entity.AllNames)
        {
            foreach (var token in NameMatcher.Tokens(name, stripLegalSuffixes: false))
            {
                if (token.Length < 2 || StopTokens.Contains(token)) continue;
                Post(_tokenPostings, token, id);
                if (token.Length >= 4) Post(_prefixPostings, token[..3], id);
            }
        }
    }

    public void AddRange(IEnumerable<SanctionedEntity> entities)
    {
        foreach (var e in entities) Add(e);
    }

    public IReadOnlyList<SanctionsHit> Search(ScreeningSubject subject, double threshold = 0.85, int maxHits = 10)
    {
        var queryTokens = NameMatcher.Tokens(subject.Name, stripLegalSuffixes: false)
            .Where(t => t.Length >= 2 && !StopTokens.Contains(t)).ToList();
        if (queryTokens.Count == 0) return Array.Empty<SanctionsHit>();

        var candidateCounts = new Dictionary<int, int>();
        foreach (var token in queryTokens)
        {
            if (_tokenPostings.TryGetValue(token, out var ids))
                foreach (var id in ids) candidateCounts[id] = candidateCounts.GetValueOrDefault(id) + 2;
            if (token.Length >= 4 && _prefixPostings.TryGetValue(token[..3], out var pids))
                foreach (var id in pids) candidateCounts[id] = candidateCounts.GetValueOrDefault(id) + 1;
        }

        // Require at least one exact token hit, or half the query tokens by prefix, before paying for fuzzy scoring.
        var minVotes = Math.Max(2, queryTokens.Count);
        var hits = new List<SanctionsHit>();
        foreach (var (id, votes) in candidateCounts)
        {
            if (votes < minVotes) continue;
            var entity = _entities[id];
            var (bestName, bestScore) = entity.AllNames
                .Select(n => (n, NameMatcher.Similarity(subject.Name, n)))
                .OrderByDescending(x => x.Item2)
                .First();
            if (bestScore < threshold - 0.1) continue;

            var reasons = new List<string> { $"Name similarity {bestScore:P0} vs '{bestName}'" };
            var score = bestScore;

            if (subject.IsIndividual && entity.Type == SanctionedEntityType.Organization) { score -= 0.1; reasons.Add("Type mismatch: subject is a person, listing is an organisation"); }
            else if (!subject.IsIndividual && entity.Type == SanctionedEntityType.Person) { score -= 0.1; reasons.Add("Type mismatch: subject is an organisation, listing is a person"); }

            if (subject.DateOfBirth is DateOnly dob && entity.BirthDates.Count > 0)
            {
                if (entity.BirthDates.Any(b => b.Contains(dob.Year.ToString(), StringComparison.Ordinal))) { score += 0.08; reasons.Add($"Birth year {dob.Year} matches listing"); }
                else { score -= 0.15; reasons.Add($"Birth year {dob.Year} does not match listing ({string.Join('/', entity.BirthDates.Take(2))})"); }
            }

            if (!string.IsNullOrWhiteSpace(subject.Country) && entity.Countries.Count > 0)
            {
                if (entity.Countries.Any(c => CountryMatches(c, subject.Country))) { score += 0.05; reasons.Add($"Country {subject.Country} matches listing"); }
                else { score -= 0.05; reasons.Add($"Country {subject.Country} differs from listing ({string.Join('/', entity.Countries.Take(3))})"); }
            }

            score = Math.Clamp(score, 0, 1);
            if (score >= threshold) hits.Add(new SanctionsHit(entity, bestName, bestScore, Math.Round(score, 4), reasons));
        }

        return hits.OrderByDescending(h => h.Score).Take(maxHits).ToList();
    }

    private static bool CountryMatches(string listed, string subject)
    {
        listed = listed.Trim();
        subject = subject.Trim();
        return listed.Equals(subject, StringComparison.OrdinalIgnoreCase)
               || listed.Contains(subject, StringComparison.OrdinalIgnoreCase)
               || subject.Contains(listed, StringComparison.OrdinalIgnoreCase);
    }

    private static void Post(Dictionary<string, List<int>> postings, string key, int id)
    {
        if (!postings.TryGetValue(key, out var list)) postings[key] = list = new List<int>();
        if (list.Count == 0 || list[^1] != id) list.Add(id);
    }
}
