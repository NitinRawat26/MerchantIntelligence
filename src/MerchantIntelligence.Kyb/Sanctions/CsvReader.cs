using System.Text;

namespace MerchantIntelligence.Kyb.Sanctions;

/// <summary>Minimal RFC 4180 reader (quoted fields, embedded commas/newlines, doubled quotes).</summary>
public static class CsvReader
{
    public static IEnumerable<string[]> Read(TextReader reader)
    {
        var field = new StringBuilder();
        var row = new List<string>();
        var inQuotes = false;
        int ch;
        while ((ch = reader.Read()) != -1)
        {
            var c = (char)ch;
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"') { field.Append('"'); reader.Read(); }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    yield return row.ToArray();
                    row.Clear();
                    break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row.ToArray();
        }
    }
}
