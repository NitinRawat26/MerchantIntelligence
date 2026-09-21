using System.Text.RegularExpressions;
using MerchantIntelligence.MccValidation.Taxonomy;

namespace MerchantIntelligence.Kyb.Registry;

public enum AddressType
{
    /// <summary>Not enough evidence to say; must be treated as unknown, never as commercial.</summary>
    Unknown,
    Residential,
    Commercial,
    /// <summary>Commercial building with dwellings above / mixed tags; common for small storefronts.</summary>
    MixedUse,
    /// <summary>Commercial mail-receiving agency, PO box, virtual office or coworking mail address.</summary>
    Cmra
}

/// <summary>What the geocoder said about the declared address, beyond coordinates.</summary>
public sealed record GeocodeHit(
    GeoPoint Point,
    string? Category,
    string? Type,
    string? AddressType,
    IReadOnlyDictionary<string, string> ExtraTags,
    string? DisplayName);

public sealed record AddressClassification(
    AddressType Type,
    /// <summary>0..1; how strongly the evidence supports <see cref="Type"/>.</summary>
    double Confidence,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<KybFlag> Flags,
    /// <summary>False when no geocoder / places evidence was available and only the address text could be read.</summary>
    bool Covered);

/// <summary>
/// Classifies the declared trading address as residential, commercial, mixed-use or a mail-drop (CMRA / PO box / virtual
/// office) from free evidence: the address text itself, OpenStreetMap tags on the geocoded feature, and named points of
/// interest at the same spot. Then judges whether that fits the declared MCC — a café or retail shop at a residential
/// address, or any card-present MCC at a mail-drop, is a finding. No result is ever assumed commercial by default.
/// </summary>
public static class AddressClassifier
{
    private static readonly Regex PoBox = new(@"\b(P\.?\s*O\.?\s*BOX|POST\s+OFFICE\s+BOX|PMB|PRIVATE\s+MAIL\s*BOX|MAILBOX)\b\s*#?\s*\d*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Apartment = new(@"\b(APT|APARTMENT|FLAT|UNIT\s*#?\s*\d+[A-Z]?|#\s*\d+[A-Z]?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Suite = new(@"\b(STE|SUITE|FLOOR|FL|LEVEL|PLAZA|MALL|CENTER|CENTRE|TOWER|BUILDING|BLDG|OFFICE\s+PARK|INDUSTRIAL)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Operators whose storefront is, for a merchant application, a mail-drop or virtual office rather than a trading premise.</summary>
    public static readonly string[] CmraOperators =
    [
        "UPS Store", "Mail Boxes Etc", "PostalAnnex", "Postal Annex", "PostNet", "Pak Mail", "Parcel Plus", "Goin' Postal", "AIM Mail Center",
        "Regus", "WeWork", "Spaces", "Davinci Virtual", "iPostal", "Anytime Mailbox", "Earth Class Mail", "Opus Virtual Offices", "Alliance Virtual Offices",
        "Intelligent Office", "Servcorp", "Industrious", "Venture X", "Office Evolution", "Premier Workspaces"
    ];

    private static readonly HashSet<string> ResidentialBuildings = new(StringComparer.OrdinalIgnoreCase)
        { "house", "residential", "apartments", "detached", "semidetached_house", "terrace", "bungalow", "dormitory", "cabin", "static_caravan", "farm", "hut", "ger" };
    private static readonly HashSet<string> CommercialBuildings = new(StringComparer.OrdinalIgnoreCase)
        { "commercial", "retail", "office", "industrial", "warehouse", "supermarket", "kiosk", "service", "hotel", "restaurant", "shop", "mall" };
    private static readonly HashSet<string> CommercialCategories = new(StringComparer.OrdinalIgnoreCase)
        { "shop", "amenity", "office", "craft", "tourism", "leisure", "healthcare", "commercial", "retail", "industrial" };

    /// <summary>
    /// MCCs that describe a physical storefront customers walk into; at a residential or mail-drop address they do not add up.
    /// Range-based: restaurants / bars (5811–5814 except 5811 catering), grocery and retail (5200–5999 broadly), personal services with premises.
    /// </summary>
    public static bool IsStorefrontMcc(int mcc) =>
        mcc is 5812 or 5813 or 5814 or 5411 or 5422 or 5441 or 5451 or 5462 or 5499 or 5541 or 5542 or 5912 or 5921 or 5993
        || mcc is >= 5300 and <= 5399 || mcc is >= 5600 and <= 5699 || mcc is 5712 or 5719 or 5722 or 5732 or 5733 or 5734 or 5735
        || mcc is 5940 or 5941 or 5942 or 5943 or 5944 or 5945 or 5946 or 5947 or 5948 or 5949 or 5950 or 5970 or 5977 or 5992 or 5995
        || mcc is 7230 or 7251 or 7298 or 7542 or 7832 or 7911 or 7932 or 7933 or 7941 or 7991 or 7992 or 7996 or 7997 or 7998 or 8011 or 8021 or 8041 or 8042 or 8043 or 8049 or 8071;

    /// <summary>MCCs that are commonly and legitimately home-based (services, catering, direct marketing, professional and repair trades).</summary>
    public static bool IsHomeCompatibleMcc(int mcc) =>
        mcc is 5811 or 5045 or 5046 or 5065 or 5099 or 5111 or 5192 or 5193 or 5964 or 5965 or 5966 or 5967 or 5968 or 5969
        || mcc is 7311 or 7333 or 7338 or 7361 or 7372 or 7375 or 7379 or 7392 or 7393 or 7394 or 7399 or 7629 or 7641 or 7699 or 8111 or 8351 or 8641 or 8699 or 8734 or 8911 or 8931 or 8999
        || mcc is 1520 or 1711 or 1731 or 1740 or 1750 or 1761 or 1771 or 1799 or 742 or 763 or 780;

    public static AddressClassification Classify(string? addressLine, GeocodeHit? hit, IReadOnlyList<PlaceRecord> nearbyPois, int? mcc)
    {
        var evidence = new List<string>();
        double residential = 0, commercial = 0, cmra = 0;
        var covered = hit is not null || nearbyPois.Count > 0;

        // 1. Address text
        if (!string.IsNullOrWhiteSpace(addressLine))
        {
            if (PoBox.IsMatch(addressLine)) { cmra += 0.9; evidence.Add("Address text is a PO box / private mailbox."); }
            else if (Apartment.IsMatch(addressLine)) { residential += 0.35; evidence.Add("Address text carries an apartment / unit designator."); }
            else if (Suite.IsMatch(addressLine)) { commercial += 0.25; evidence.Add("Address text carries a suite / floor / building designator."); }
        }

        // 2. Geocoder feature tags
        if (hit is not null)
        {
            var building = hit.ExtraTags.TryGetValue("building", out var b) ? b : null;
            var cat = hit.Category; var osmType = hit.Type;
            if (cat is "building" && osmType is not null && ResidentialBuildings.Contains(osmType) || building is not null && ResidentialBuildings.Contains(building))
            { residential += 0.6; evidence.Add($"OpenStreetMap maps the geocoded feature as a residential building ({building ?? osmType})."); }
            else if (cat is "building" && osmType is not null && CommercialBuildings.Contains(osmType) || building is not null && CommercialBuildings.Contains(building))
            { commercial += 0.6; evidence.Add($"OpenStreetMap maps the geocoded feature as a commercial building ({building ?? osmType})."); }
            else if (cat is not null && CommercialCategories.Contains(cat))
            { commercial += 0.5; evidence.Add($"Geocoded feature is a {cat}={osmType} point of interest."); }
            else if (cat is "place" && osmType is "house" || hit.AddressType is "house")
            { residential += 0.3; evidence.Add("Geocoder resolved the address to a house-number point with no business tags."); }

            if (hit.ExtraTags.TryGetValue("landuse", out var lu))
            {
                if (lu is "residential") { residential += 0.3; evidence.Add("Land use at the point is residential."); }
                else if (lu is "commercial" or "retail" or "industrial") { commercial += 0.3; evidence.Add($"Land use at the point is {lu}."); }
            }
            if (hit.ExtraTags.TryGetValue("building:levels", out var lv) && int.TryParse(lv, out var levels) && levels >= 2 && commercial > 0 && residential > 0)
                evidence.Add("Multi-storey building with both residential and commercial tags (mixed use).");
        }

        // 3. Named points of interest at the same spot
        var atSpot = nearbyPois.Where(p => p.Category is not null).ToList();
        var mailDrop = nearbyPois.FirstOrDefault(p => CmraOperators.Any(o => p.Name.Contains(o, StringComparison.OrdinalIgnoreCase))
                                                   || p.Category?.Contains("post_office", StringComparison.OrdinalIgnoreCase) == true
                                                   || p.Category?.Contains("coworking", StringComparison.OrdinalIgnoreCase) == true);
        if (mailDrop is not null) { cmra += 0.8; evidence.Add($"A mail-receiving / virtual-office operator is listed at this address: {mailDrop.Name}."); }
        else if (atSpot.Count > 0)
        {
            commercial += Math.Min(0.6, 0.25 * atSpot.Count);
            evidence.Add($"{atSpot.Count} named business(es) mapped within a few metres: {string.Join(", ", atSpot.Take(3).Select(p => $"{p.Name} ({p.Category})"))}.");
        }

        AddressType type;
        double confidence;
        if (cmra >= 0.8) { type = AddressType.Cmra; confidence = Math.Min(1, cmra); }
        else if (commercial >= 0.5 && residential >= 0.5) { type = AddressType.MixedUse; confidence = Math.Min(1, Math.Min(commercial, residential)); }
        else if (commercial >= 0.5 && commercial > residential) { type = AddressType.Commercial; confidence = Math.Min(1, commercial); }
        else if (residential >= 0.5 && residential > commercial) { type = AddressType.Residential; confidence = Math.Min(1, residential); }
        else if (commercial > 0 || residential > 0)
        {
            type = commercial > residential ? AddressType.Commercial : AddressType.Residential;
            confidence = Math.Min(0.45, Math.Max(commercial, residential));
        }
        else { type = AddressType.Unknown; confidence = 0; }
        if (evidence.Count == 0) evidence.Add(covered ? "No residential, commercial or mail-drop indicators were found at the geocoded point." : "No geocoder or places evidence was available; only the address text was read.");

        var flags = new List<KybFlag>();
        switch (type)
        {
            case AddressType.Cmra:
                flags.Add(new("ADDRESS_CMRA", $"The declared trading address is a mail-drop / virtual office ({string.Join(" ", evidence.Where(e => e.Contains("mail", StringComparison.OrdinalIgnoreCase) || e.Contains("PO box")))}). Nothing trades there; obtain the physical premises address.", mcc is { } m1 && IsStorefrontMcc(m1) ? RiskTier.High : RiskTier.Medium));
                break;
            case AddressType.Residential when confidence >= 0.5:
                if (mcc is { } m2 && IsStorefrontMcc(m2))
                    flags.Add(new("ADDRESS_RESIDENTIAL_STOREFRONT_MCC", $"MCC {m2} describes a walk-in storefront but the declared address classifies as residential ({confidence:P0}). Confirm where customers are actually served.", RiskTier.Medium));
                else if (mcc is { } m3 && IsHomeCompatibleMcc(m3))
                    flags.Add(new("ADDRESS_HOME_BASED", $"Residential address ({confidence:P0}); MCC {m3} is commonly operated from home, so this is consistent.", RiskTier.Low));
                else
                    flags.Add(new("ADDRESS_RESIDENTIAL", $"Declared address classifies as residential ({confidence:P0}); the merchant appears to be home-based.", RiskTier.Low));
                break;
            case AddressType.Unknown:
                flags.Add(new("ADDRESS_TYPE_UNKNOWN", covered
                    ? "Address type could not be classified from map data; treat as unverified premises, not as commercial."
                    : "Address type was not classified because no geocoder or places evidence was available.", RiskTier.Low));
                break;
            case AddressType.Residential:
            case AddressType.Commercial when confidence < 0.5:
                flags.Add(new("ADDRESS_TYPE_WEAK", $"Address type leans {type} on weak evidence ({confidence:P0}); inconclusive.", RiskTier.Low));
                break;
        }
        return new AddressClassification(type, Math.Round(confidence, 2), evidence, flags, covered);
    }
}
