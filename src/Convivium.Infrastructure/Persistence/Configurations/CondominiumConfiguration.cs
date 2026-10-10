namespace Convivium.Infrastructure.Persistence.Configurations;

using Convivium.Domain.Condominiums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class CondominiumConfiguration : IEntityTypeConfiguration<Condominium>
{
    public void Configure(EntityTypeBuilder<Condominium> builder)
    {
        builder.Property(c => c.Name).HasMaxLength(160).IsRequired();
        builder.Property(c => c.LegalName).HasMaxLength(200);
        builder.Property(c => c.Cnpj).HasMaxLength(14);
        builder.Property(c => c.PixKey).HasMaxLength(80);
        builder.Property(c => c.PixReceiverName).HasMaxLength(25);
        builder.Property(c => c.PixReceiverCity).HasMaxLength(15);

        builder.HasIndex(c => c.Cnpj).IsUnique().HasFilter("cnpj IS NOT NULL");

        builder.OwnsOne(c => c.Address, address =>
        {
            address.Property(a => a.Street).HasMaxLength(200);
            address.Property(a => a.Number).HasMaxLength(20);
            address.Property(a => a.Complement).HasMaxLength(100);
            address.Property(a => a.District).HasMaxLength(120);
            address.Property(a => a.City).HasMaxLength(120);
            address.Property(a => a.State).HasMaxLength(2);
            address.Property(a => a.ZipCode).HasMaxLength(8);
        });

        builder.OwnsOne(c => c.Billing, billing =>
        {
            billing.Property(b => b.ReserveFundRate).HasPrecision(9, 6);
            billing.Property(b => b.LateFeeRate).HasPrecision(9, 6);
            billing.Property(b => b.MonthlyInterestRate).HasPrecision(9, 6);
        });

        // Os campos *Cipher guardam o texto ja cifrado pelo ISecretProtector,
        // em base64. O certificado e o maior: um PKCS#12 com cadeia cabe com
        // folga em 16 KB depois de cifrado e codificado.
        builder.OwnsOne(c => c.Boleto, boleto =>
        {
            boleto.Property(b => b.ClientId).HasMaxLength(200);
            boleto.Property(b => b.ClientSecretCipher).HasMaxLength(1024);
            boleto.Property(b => b.CertificateCipher).HasMaxLength(16384);
            boleto.Property(b => b.CertificatePasswordCipher).HasMaxLength(1024);
            boleto.Property(b => b.CertificateSubject).HasMaxLength(300);
            boleto.Property(b => b.CheckingAccount).HasMaxLength(20);
            boleto.Property(b => b.Instructions).HasMaxLength(400);
            boleto.Ignore(b => b.IsConfigured);
        });
    }
}

public sealed class BlockConfiguration : IEntityTypeConfiguration<Block>
{
    public void Configure(EntityTypeBuilder<Block> builder)
    {
        builder.Property(b => b.Name).HasMaxLength(80).IsRequired();

        builder.HasOne(b => b.Condominium)
            .WithMany(c => c.Blocks)
            .HasForeignKey(b => b.CondominiumId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(b => new { b.CondominiumId, b.Name }).IsUnique();
    }
}

public sealed class UnitConfiguration : IEntityTypeConfiguration<Unit>
{
    public void Configure(EntityTypeBuilder<Unit> builder)
    {
        builder.Property(u => u.Identifier).HasMaxLength(30).IsRequired();
        builder.Property(u => u.AreaM2).HasPrecision(10, 2);

        // Fracao ideal precisa de casas decimais suficientes para um predio grande:
        // 400 unidades iguais dao 0,00250000 cada.
        builder.Property(u => u.IdealFraction).HasPrecision(12, 8);

        builder.HasOne(u => u.Condominium)
            .WithMany(c => c.Units)
            .HasForeignKey(u => u.CondominiumId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(u => u.Block)
            .WithMany(b => b.Units)
            .HasForeignKey(u => u.BlockId)
            .OnDelete(DeleteBehavior.SetNull);

        // Nao pode existir "Bloco A - 101" duas vezes no mesmo condominio.
        builder.HasIndex(u => new { u.CondominiumId, u.BlockId, u.Identifier }).IsUnique();
    }
}
