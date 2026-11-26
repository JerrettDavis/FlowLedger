using Bogus;

namespace FlowLedger.TestSupport;

/// <summary>Seeded Bogus helpers: realistic-looking strings that are identical on every run (no flaky data).</summary>
public static class Fake
{
    public static string Merchant(int seed = 1) => new Faker { Random = new Randomizer(seed) }.Company.CompanyName();

    public static string Person(int seed = 1) => new Faker { Random = new Randomizer(seed) }.Name.FullName();

    /// <summary>A generator of seeded merchant names, usable as a CsCheck generator via <c>Gen.Int.Select(Fake.Merchant)</c>.</summary>
    public static IEnumerable<string> Merchants(int count, int seed = 1)
    {
        var faker = new Faker { Random = new Randomizer(seed) };
        return Enumerable.Range(0, count).Select(_ => faker.Company.CompanyName());
    }
}
