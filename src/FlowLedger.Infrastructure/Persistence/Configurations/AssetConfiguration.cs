using System.Text.Json;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    private static readonly ValueConverter<Dictionary<string, string>, string> AttributesConverter = new(
        d => JsonSerializer.Serialize(d, (JsonSerializerOptions?)null),
        s => Deserialize(s));

    // Without a comparer EF cannot see in-place edits of the dictionary and would silently skip saving them.
    private static readonly ValueComparer<Dictionary<string, string>> AttributesComparer = new(
        (a, b) => a != null && b != null && a.Count == b.Count && !a.Except(b).Any(),
        d => d.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value)),
        d => new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase));

    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        builder.ToTable("assets");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Ignore(a => a.AssetId);

        builder.Property<TenantId>("TenantId")
            .HasColumnName("tenant_id")
            .HasConversion(StronglyTypedIdConverters.TenantIdConverter)
            .IsRequired();

        builder.Property(a => a.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(a => a.Category).HasColumnName("category").HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(a => a.Manufacturer).HasColumnName("manufacturer").HasMaxLength(200);
        builder.Property(a => a.Model).HasColumnName("model").HasMaxLength(200);
        builder.Property(a => a.SerialNumber).HasColumnName("serial_number").HasMaxLength(200);
        builder.Property(a => a.Notes).HasColumnName("notes").HasMaxLength(4000);
        builder.Property(a => a.PurchaseDate).HasColumnName("purchase_date");

        builder.OwnsOne(a => a.CurrentValue, money =>
        {
            money.Property(m => m.Amount).HasColumnName("value_amount").HasColumnType("numeric(18,4)").IsRequired();
            money.Property(m => m.Currency).HasColumnName("value_currency").HasMaxLength(3)
                .HasConversion(CurrencyConverter.Instance).IsRequired();
        });
        builder.Navigation(a => a.CurrentValue).IsRequired();

        builder.OwnsOne(a => a.PurchasePrice, money =>
        {
            money.Property(m => m.Amount).HasColumnName("purchase_price_amount").HasColumnType("numeric(18,4)");
            money.Property(m => m.Currency).HasColumnName("purchase_price_currency").HasMaxLength(3)
                .HasConversion(CurrencyConverter.Instance);
        });

        builder.Property(a => a.ValuedOn).HasColumnName("valued_on").IsRequired();
        builder.Property(a => a.Method).HasColumnName("valuation_method").HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(a => a.AnnualRate).HasColumnName("annual_rate").HasColumnType("numeric(9,6)").IsRequired();
        builder.Property(a => a.SalvageValue).HasColumnName("salvage_value").HasColumnType("numeric(18,4)");
        builder.Property(a => a.UsefulLifeYears).HasColumnName("useful_life_years");
        builder.Property(a => a.LinkedAccountId).HasColumnName("linked_account_id");

        builder.Property(a => a.Attributes)
            .HasColumnName("attributes")
            .HasColumnType("jsonb")
            .HasConversion(AttributesConverter, AttributesComparer)
            .IsRequired();

        builder.Property(a => a.IsActive).HasColumnName("is_active").HasDefaultValue(true).IsRequired();
        builder.Property(a => a.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.OwnsMany(a => a.Valuations, v =>
        {
            v.ToTable("asset_valuations");
            v.WithOwner().HasForeignKey("asset_id");
            v.HasKey(x => x.Id);
            v.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            v.Property(x => x.Amount).HasColumnName("amount").HasColumnType("numeric(18,4)").IsRequired();
            v.Property(x => x.AsOf).HasColumnName("as_of").IsRequired();
            v.Property(x => x.Source).HasColumnName("source").HasMaxLength(100).IsRequired();
            v.Property(x => x.Note).HasColumnName("note").HasMaxLength(1000);
            v.Property(x => x.RecordedAt).HasColumnName("recorded_at").IsRequired();
            v.HasIndex("asset_id", nameof(AssetValuation.AsOf)).HasDatabaseName("ix_asset_valuations_asset_as_of");
        });

        builder.HasIndex("TenantId").HasDatabaseName("ix_assets_tenant_id");
        builder.HasIndex(a => a.LinkedAccountId).HasDatabaseName("ix_assets_linked_account_id");
    }

    private static Dictionary<string, string> Deserialize(string json)
    {
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json, (JsonSerializerOptions?)null);
        return parsed is null ? new Dictionary<string, string>() : new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
    }
}
