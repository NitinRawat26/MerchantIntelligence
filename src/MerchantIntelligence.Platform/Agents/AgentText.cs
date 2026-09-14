namespace MerchantIntelligence.Platform.Agents;

internal static class AgentText
{
    public static string Money(decimal v) => "$" + v.ToString("N0");
    public static string Norm(string s) => new string(s.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
}
