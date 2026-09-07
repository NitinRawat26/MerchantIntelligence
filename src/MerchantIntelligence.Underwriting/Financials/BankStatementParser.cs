using System.Globalization;
using System.Text.RegularExpressions;
using MerchantIntelligence.Kyb.Sanctions;
using UglyToad.PdfPig;

namespace MerchantIntelligence.Underwriting.Financials;

public sealed record BankTransaction(DateOnly Date, string Description, decimal Amount, decimal? Balance);

public sealed record ParsedStatement(
    IReadOnlyList<BankTransaction> Transactions,
    IReadOnlyList<string> Warnings,
    string Format);

/// <summary>
/// Parses bank statements exported as CSV (any column order; auto-detects date / description /
/// amount or debit+credit / balance columns) or as text-based PDF (line-oriented heuristics:
/// date, description, amount[, balance]). Scanned/image PDFs are not supported (no OCR).
/// </summary>
public static class BankStatementParser
{
    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "d/M/yyyy", "M/d/yyyy", "dd-MM-yyyy", "MM-dd-yyyy",
        "dd MMM yyyy", "d MMM yyyy", "MMM d, yyyy", "MMM dd, yyyy", "dd.MM.yyyy", "yyyy/MM/dd", "dd/MM/yy", "MM/dd/yy", "dd MMM yy"
    };

    private static readonly Regex AmountRegex = new(@"^\(?-?[$£€]?\s?-?\d{1,3}(,\d{3})*(\.\d{1,2})?\)?(\s?(CR|DR))?$|^\(?-?[$£€]?\s?-?\d+(\.\d{1,2})?\)?(\s?(CR|DR))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PdfLineRegex = new(
        @"^(?<date>\d{1,2}[/\-.]\d{1,2}([/\-.]\d{2,4})?|\d{4}-\d{2}-\d{2}|\d{1,2}\s[A-Za-z]{3}(\s\d{2,4})?|[A-Za-z]{3}\s\d{1,2},?(\s\d{4})?)\s+(?<desc>.+?)\s+(?<amounts>(-?\(?[$£€]?\d[\d,]*\.\d{2}\)?-?(\s?(CR|DR))?\s*){1,3})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ParsedStatement Parse(Stream stream, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext == ".pdf") return ParsePdf(stream);
        using var reader = new StreamReader(stream);
        return ParseCsv(reader.ReadToEnd());
    }

    public static ParsedStatement ParseCsv(string csv)
    {
        using var textReader = new StringReader(csv);
        var rows = CsvReader.Read(textReader, SniffDelimiter(csv)).Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).Select(r => (IReadOnlyList<string>)r).ToList();
        var warnings = new List<string>();
        if (rows.Count < 2) return new ParsedStatement(Array.Empty<BankTransaction>(), new[] { "CSV has no data rows." }, "csv");

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToArray();
        int dateCol = FindColumn(header, "date", "posted", "transaction date", "value date");
        int descCol = FindColumn(header, "description", "memo", "details", "narrative", "payee", "transaction", "particulars");
        int amountCol = FindColumn(header, "amount", "value", "net");
        int debitCol = FindColumn(header, "debit", "withdrawal", "money out", "paid out", "out");
        int creditCol = FindColumn(header, "credit", "deposit", "money in", "paid in", "in");
        int balanceCol = FindColumn(header, "balance", "running balance", "closing balance");

        var hasHeader = dateCol >= 0 || amountCol >= 0 || debitCol >= 0;
        if (!hasHeader)
        {
            warnings.Add("No recognisable header row; inferring columns from data.");
            (dateCol, descCol, amountCol, balanceCol) = InferColumns(rows);
            if (dateCol < 0 || amountCol < 0)
                return new ParsedStatement(Array.Empty<BankTransaction>(), new[] { "Could not identify date and amount columns." }, "csv");
        }
        if (dateCol < 0) return new ParsedStatement(Array.Empty<BankTransaction>(), new[] { "No date column found." }, "csv");
        if (amountCol < 0 && debitCol < 0 && creditCol < 0) return new ParsedStatement(Array.Empty<BankTransaction>(), new[] { "No amount, debit or credit column found." }, "csv");

        var txns = new List<BankTransaction>();
        var skipped = 0;
        foreach (var row in rows.Skip(hasHeader ? 1 : 0))
        {
            var date = TryParseDate(Get(row, dateCol));
            if (date is null) { skipped++; continue; }

            decimal? amount = null;
            if (amountCol >= 0) amount = TryParseAmount(Get(row, amountCol));
            if (amount is null && (debitCol >= 0 || creditCol >= 0))
            {
                var debit = debitCol >= 0 ? TryParseAmount(Get(row, debitCol)) : null;
                var credit = creditCol >= 0 ? TryParseAmount(Get(row, creditCol)) : null;
                if (debit is null && credit is null) { skipped++; continue; }
                amount = (credit ?? 0) - Math.Abs(debit ?? 0);
            }
            if (amount is null) { skipped++; continue; }

            txns.Add(new BankTransaction(date.Value, descCol >= 0 ? Get(row, descCol).Trim() : string.Empty, amount.Value,
                balanceCol >= 0 ? TryParseAmount(Get(row, balanceCol)) : null));
        }
        if (skipped > 0) warnings.Add($"{skipped} row(s) skipped (unparseable date or amount).");
        return new ParsedStatement(txns.OrderBy(t => t.Date).ToList(), warnings, "csv");
    }

    private static char SniffDelimiter(string csv)
    {
        var firstLine = csv.Split('\n', 2)[0];
        var candidates = new[] { ',', ';', '\t', '|' };
        return candidates.MaxBy(c => firstLine.Count(ch => ch == c));
    }

    public static ParsedStatement ParsePdf(Stream stream)
    {
        var warnings = new List<string>();
        var txns = new List<BankTransaction>();
        int? statementYear = null;
        using var pdf = PdfDocument.Open(stream);
        var pages = 0;
        foreach (var page in pdf.GetPages())
        {
            pages++;
            var lines = page.Text.Length > 0 ? ReconstructLines(page) : Array.Empty<string>();
            foreach (var line in lines)
            {
                statementYear ??= Regex.Match(line, @"\b(20\d{2})\b") is { Success: true } y ? int.Parse(y.Groups[1].Value) : null;
                var m = PdfLineRegex.Match(line.Trim());
                if (!m.Success) continue;
                var date = TryParseDate(m.Groups["date"].Value, statementYear);
                if (date is null) continue;
                var amounts = Regex.Matches(m.Groups["amounts"].Value, @"-?\(?[$£€]?\d[\d,]*\.\d{2}\)?-?(\s?(CR|DR))?", RegexOptions.IgnoreCase)
                    .Select(a => TryParseAmount(a.Value)).Where(a => a.HasValue).Select(a => a!.Value).ToList();
                if (amounts.Count == 0) continue;
                // date desc amount balance  |  date desc amount  |  date desc debit credit balance
                var amount = amounts[0];
                decimal? balance = amounts.Count >= 2 ? amounts[^1] : null;
                if (amounts.Count == 3) amount = amounts[0] != 0 ? -Math.Abs(amounts[0]) : amounts[1];
                txns.Add(new BankTransaction(date.Value, m.Groups["desc"].Value.Trim(), amount, balance));
            }
        }
        if (txns.Count == 0)
            warnings.Add(pages == 0 ? "PDF has no pages." : "No transaction lines recognised; the PDF may be scanned (image-only) or use an unsupported layout.");
        else
            InferSignsFromBalances(txns, warnings);
        return new ParsedStatement(txns, warnings, "pdf");
    }

    /// <summary>PDF text often loses sign information; when a running balance column exists, derive the sign from balance deltas.</summary>
    private static void InferSignsFromBalances(List<BankTransaction> txns, List<string> warnings)
    {
        if (txns.Count < 2 || txns.Any(t => t.Balance is null)) return;
        var fixedCount = 0;
        for (var i = 1; i < txns.Count; i++)
        {
            var delta = txns[i].Balance!.Value - txns[i - 1].Balance!.Value;
            if (Math.Abs(Math.Abs(delta) - Math.Abs(txns[i].Amount)) > 0.01m) continue;
            var signed = delta < 0 ? -Math.Abs(txns[i].Amount) : Math.Abs(txns[i].Amount);
            if (signed != txns[i].Amount) { txns[i] = txns[i] with { Amount = signed }; fixedCount++; }
        }
        if (fixedCount > 0) warnings.Add($"Sign of {fixedCount} transaction(s) inferred from running balance.");
    }

    private static IReadOnlyList<string> ReconstructLines(UglyToad.PdfPig.Content.Page page)
    {
        // Group words by baseline (rounded Y) to rebuild rows, then order by X.
        return page.GetWords()
            .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3.0))
            .OrderByDescending(g => g.Key)
            .Select(g => string.Join(' ', g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)))
            .ToList();
    }

    private static int FindColumn(string[] header, params string[] names)
    {
        foreach (var n in names)
        {
            var idx = Array.FindIndex(header, h => h == n);
            if (idx >= 0) return idx;
        }
        foreach (var n in names)
        {
            var idx = Array.FindIndex(header, h => h.Contains(n, StringComparison.Ordinal));
            if (idx >= 0) return idx;
        }
        return -1;
    }

    private static (int Date, int Desc, int Amount, int Balance) InferColumns(List<IReadOnlyList<string>> rows)
    {
        var width = rows.Max(r => r.Count);
        int date = -1, desc = -1, amount = -1, balance = -1;
        var sample = rows.Take(20).ToList();
        for (var c = 0; c < width; c++)
        {
            var values = sample.Select(r => Get(r, c)).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (values.Count == 0) continue;
            if (date < 0 && values.Count(v => TryParseDate(v) != null) >= values.Count * 0.8) { date = c; continue; }
            if (values.Count(v => TryParseAmount(v) != null) >= values.Count * 0.8)
            {
                if (amount < 0) amount = c; else if (balance < 0) balance = c;
                continue;
            }
            if (desc < 0 && values.Average(v => v.Length) > 6) desc = c;
        }
        return (date, desc, amount, balance);
    }

    private static string Get(IReadOnlyList<string> row, int i) => i >= 0 && i < row.Count ? row[i] : string.Empty;

    internal static DateOnly? TryParseDate(string? s, int? defaultYear = null)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimEnd(',');
        if (DateOnly.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return DateOnly.FromDateTime(dt);
        if (defaultYear is int year)
        {
            foreach (var f in new[] { "dd/MM", "MM/dd", "d/M", "M/d", "dd MMM", "d MMM", "MMM d", "MMM dd" })
                if (DateOnly.TryParseExact($"{s} {year}", $"{f} yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
        }
        return null;
    }

    internal static decimal? TryParseAmount(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (!AmountRegex.IsMatch(s)) return null;
        var negative = s.StartsWith('(') || s.Contains('-') || s.EndsWith("DR", StringComparison.OrdinalIgnoreCase);
        var cleaned = Regex.Replace(s, @"[()$£€,\s\-]|CR|DR", string.Empty, RegexOptions.IgnoreCase);
        if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var v)) return null;
        return negative ? -Math.Abs(v) : v;
    }
}
