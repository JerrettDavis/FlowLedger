using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories");

        // Category implements IEntity; uses Guid Id mapped from backing field _id.
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id)
            .HasColumnName("id")
            .ValueGeneratedNever()
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .HasField("_id");

        // CategoryId is computed from Id backing field — ignore the typed property.
        builder.Ignore(c => c.CategoryId);

        builder.Property<TenantId>("TenantId")
            .HasColumnName("tenant_id")
            .HasConversion(StronglyTypedIdConverters.TenantIdConverter)
            .IsRequired();

        // CategoryPath — stored as a single varchar column via value converter.
        // This avoids OwnsOne which would require CategoryPath to have a parameterless ctor.
        builder.Property(c => c.Path)
            .HasColumnName("path")
            .HasMaxLength(500)
            .HasConversion(CategoryPathConverter.Instance)
            .IsRequired();

        builder.Property(c => c.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(c => c.IsSystem)
            .HasColumnName("is_system")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property<CategoryId?>("ParentId")
            .HasColumnName("parent_id")
            .HasConversion(StronglyTypedIdConverters.NullableCategoryIdConverter);

        // Indexes
        builder.HasIndex("TenantId").HasDatabaseName("ix_categories_tenant_id");
        builder.HasIndex("TenantId", nameof(Category.Path))
            .HasDatabaseName("ix_categories_tenant_path");
    }
}
