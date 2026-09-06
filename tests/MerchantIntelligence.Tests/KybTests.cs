using System.Text;
using MerchantIntelligence.Kyb.Matching;
using MerchantIntelligence.Kyb.Prohibited;
using MerchantIntelligence.Kyb.Registry;
using MerchantIntelligence.Kyb.Sanctions;
using MerchantIntelligence.MccValidation.Taxonomy;

namespace MerchantIntelligence.Tests;

public sealed class NameMatcherTests
{
    [Theory]
    [InlineData("Acme Holdings, Inc.", "ACME HOLDINGS INC", 1.0)]
    [InlineData("Acme Holdings Ltd", "Holdings Acme Limited", 1.0)]
    [InlineData("Muhammad Al-Qasimi", "Mohammed Al Qasimi", 0.85)]
    public void Similar_names_score_high(string a, string b, double min) =>
        Assert.True(NameMatcher.Similarity(a, b) >= min, $"{a} vs {b} = {NameMatcher.Similarity(a, b)}");

    [Theory]
    [InlineData("Blue Ocean Bakery", "Northwind Logistics GmbH", 0.5)]
    [InlineData("Viktor Bout", "Viktor Ignatov", 0.8)]
    [InlineData("Jane Ordinary Smith", "Samantha Jane Power", 0.8)]
    public void Unrelated_names_score_low(string a, string b, double max) =>
        Assert.True(NameMatcher.Similarity(a, b) < max, $"{a} vs {b} = {NameMatcher.Similarity(a, b)}");

    [Fact]
    public void Normalize_strips_accents_and_punctuation() =>
        Assert.Equal("cafe du monde", NameMatcher.Normalize("Café du Monde!"));
}

public sealed class SanctionsIndexTests
{
    private static SanctionedEntity Entity(string id, string name, SanctionedEntityType type, params string[] aliases) =>
        new(id, "Test", type, name, aliases, new[] { "1965-03-12" }, new[] { "Iran" }, new[] { "SDGT" }, null, null);

    [Fact]
    public void Finds_fuzzy_and_alias_matches()
    {
        var index = new SanctionsIndex();
        index.Add(Entity("1", "Ali Reza Hosseini", SanctionedEntityType.Person, "Alireza Hoseini"));
        index.Add(Entity("2", "Northwind Trading Company", SanctionedEntityType.Organization));

        var hits = index.Search(new ScreeningSubject("Alireza Hosseini", new DateOnly(1965, 3, 12), "IR", IsIndividual: true));
        Assert.Single(hits);
        Assert.Equal("1", hits[0].Entity.Id);
        Assert.True(hits[0].Score >= 0.85);

        Assert.Empty(index.Search(new ScreeningSubject("Southwind Bakery", IsIndividual: false)));
    }

    [Fact]
    public void Type_mismatch_is_penalised()
    {
        var index = new SanctionsIndex();
        index.Add(Entity("1", "Northwind Trading", SanctionedEntityType.Organization));
        var asPerson = index.Search(new ScreeningSubject("Northwind Trading", IsIndividual: true));
        var asOrg = index.Search(new ScreeningSubject("Northwind Trading", IsIndividual: false));
        Assert.NotEmpty(asOrg);
        Assert.True(asPerson.Count == 0 || asPerson[0].Score < asOrg[0].Score);
    }
}

public sealed class SanctionsSourceParsingTests
{
    [Fact]
    public void Parses_ofac_sdn_csv()
    {
        const string csv = "36,\"AEROCARIBBEAN AIRLINES\",\"-0-\",\"CUBA\",\"-0-\",\"-0-\",\"-0-\",\"-0-\",\"-0-\",\"-0-\",\"-0-\",\"a.k.a. 'AERO-CARIBBEAN'.\"\n" +
                           "173,\"DOE, John\",\"individual\",\"SDGT\",\"-0-\",\"-0-\",\"-0-\",\"-0-\",\"-0-\",\"-0-\",\"-0-\",\"DOB 12 Mar 1965; nationality Iran; a.k.a. 'JOHNNY DOE'.\"\n";
        var entities = new OfacSdnSource().Parse(new MemoryStream(Encoding.UTF8.GetBytes(csv))).ToList();

        Assert.Equal(2, entities.Count);
        Assert.Equal(SanctionedEntityType.Organization, entities[0].Type);
        Assert.Contains("AERO-CARIBBEAN", entities[0].Aliases);
        Assert.Equal(SanctionedEntityType.Person, entities[1].Type);
        Assert.Contains("SDGT", entities[1].Programs);
        Assert.Contains("12 Mar 1965", entities[1].BirthDates);
        Assert.Contains("Iran", entities[1].Countries);
    }

    [Fact]
    public void Parses_un_consolidated_xml()
    {
        const string xml = """
            <CONSOLIDATED_LIST>
              <INDIVIDUALS><INDIVIDUAL><DATAID>1</DATAID><FIRST_NAME>Jane</FIRST_NAME><SECOND_NAME>Roe</SECOND_NAME>
                <UN_LIST_TYPE>Al-Qaida</UN_LIST_TYPE><NATIONALITY><VALUE>Yemen</VALUE></NATIONALITY>
                <INDIVIDUAL_DATE_OF_BIRTH><DATE>1970-01-01</DATE></INDIVIDUAL_DATE_OF_BIRTH>
                <INDIVIDUAL_ALIAS><ALIAS_NAME>Janie Roe</ALIAS_NAME></INDIVIDUAL_ALIAS></INDIVIDUAL></INDIVIDUALS>
              <ENTITIES><ENTITY><DATAID>2</DATAID><FIRST_NAME>Bad Corp</FIRST_NAME><UN_LIST_TYPE>DPRK</UN_LIST_TYPE></ENTITY></ENTITIES>
            </CONSOLIDATED_LIST>
            """;
        var entities = new UnConsolidatedSource().Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml))).ToList();

        Assert.Equal(2, entities.Count);
        var jane = entities.Single(e => e.Type == SanctionedEntityType.Person);
        Assert.Equal("Jane Roe", jane.Name);
        Assert.Contains("Janie Roe", jane.Aliases);
        Assert.Contains("1970-01-01", jane.BirthDates);
        Assert.Equal("Bad Corp", entities.Single(e => e.Type == SanctionedEntityType.Organization).Name);
    }

    [Fact]
    public void Csv_reader_handles_quotes_and_embedded_newlines()
    {
        var rows = CsvReader.Read(new StringReader("a,\"b, with comma\",\"multi\nline\",\"say \"\"hi\"\"\"\n1,2,3,4\n")).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "a", "b, with comma", "multi\nline", "say \"hi\"" }, rows[0]);
    }
}

public sealed class ProhibitedBusinessDetectorTests
{
    [Fact]
    public void Flags_prohibited_content()
    {
        var result = ProhibitedBusinessDetector.Default.Analyze(
            "Buy replica Rolex watches and knock-off Louis Vuitton handbags. Counterfeit designer bags at wholesale prices.");
        Assert.Equal(BusinessPolicy.Prohibited, result.Verdict);
        Assert.Contains(result.Matches, m => m.Category.Code == "COUNTERFEIT_IP");
    }

    [Fact]
    public void Flags_restricted_cbd_with_mcc_alignment()
    {
        var result = ProhibitedBusinessDetector.Default.Analyze(
            "Premium full spectrum CBD oil, hemp gummies and delta-8 THC vapes.", declaredMcc: 5912);
        Assert.NotEqual(BusinessPolicy.Acceptable, result.Verdict);
        Assert.Contains(result.Matches, m => m.Category.Code == "CBD_CANNABIS");
    }

    [Fact]
    public void Ordinary_business_is_acceptable()
    {
        var result = ProhibitedBusinessDetector.Default.Analyze("Family-run Italian restaurant serving pizza and pasta. Book a table online.");
        Assert.Equal(BusinessPolicy.Acceptable, result.Verdict);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void Catalog_has_all_categories_with_keywords()
    {
        var cats = ProhibitedBusinessDetector.Default.Categories;
        Assert.True(cats.Count >= 20);
        Assert.All(cats, c => Assert.NotEmpty(c.Keywords));
    }
}

public sealed class BusinessVerificationScoringTests
{
    [Fact]
    public void Exact_registration_number_and_name_score_high()
    {
        var identity = new BusinessIdentity("Acme Holdings Inc", RegistrationNumber: "12345", AddressLine: "1 Main St", City: "Springfield", Country: "US");
        var record = new RegistryRecord("GLEIF", "X", "ACME HOLDINGS, INC.", "ACTIVE", new DateOnly(2010, 1, 1), "US", "12345", "1 Main Street, Springfield", null, null);
        var match = BusinessVerificationService.Score(identity, record);
        Assert.True(match.NameScore >= 0.95);
        Assert.True(match.OverallScore >= 0.8, match.OverallScore.ToString());
    }

    [Fact]
    public void Different_company_scores_low()
    {
        var identity = new BusinessIdentity("Acme Holdings Inc", Country: "US");
        var record = new RegistryRecord("GLEIF", "X", "Zenith Marine Services GmbH", "ACTIVE", null, "DE", null, null, null, null);
        Assert.True(BusinessVerificationService.Score(identity, record).OverallScore < 0.4);
    }

    [Fact]
    public void Address_matcher_normalises_abbreviations() =>
        Assert.True(AddressMatcher.Similarity("100 North Main Street, Suite 4", "100 N Main St Ste 4") >= 0.9);
}
