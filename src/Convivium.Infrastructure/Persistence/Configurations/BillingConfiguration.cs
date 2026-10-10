namespace Convivium.Infrastructure.Persistence.Configurations;

using Convivium.Domain.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class BillingCycleConfiguration : IEntityTypeConfiguration<BillingCycle>
{
    public void Configure(EntityTypeBuilder<BillingCycle> builder)
    {
        builder.Property(c => c.Notes).HasMaxLength(1000);
        builder.Property(c => c.ReserveFundRate).HasPrecision(9, 6);

        // So existe um rateio por competencia em cada condominio.
        builder.HasIndex(c => new { c.CondominiumId, c.Competence }).IsUnique();

        builder.Ignore(c => c.IsEditable);
    }
}

public sealed class ChargeConfiguration : IEntityTypeConfiguration<Charge>
{
    public void Configure(EntityTypeBuilder<Charge> builder)
    {
        builder.Property(c => c.PublicToken).HasMaxLength(64).IsRequired();
        builder.Property(c => c.PixPayload).HasMaxLength(512);
        builder.Property(c => c.ExternalSlipId).HasMaxLength(100);
        builder.Property(c => c.BarcodeLine).HasMaxLength(60);
        builder.Property(c => c.SlipBarcode).HasMaxLength(44);
        builder.Property(c => c.SlipOurNumber).HasMaxLength(40);
        builder.Property(c => c.SlipPixPayload).HasMaxLength(512);
        builder.Ignore(c => c.HasRegisteredSlip);

        builder.HasOne(c => c.BillingCycle)
            .WithMany(b => b.Charges)
            .HasForeignKey(c => c.BillingCycleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(c => c.Unit)
            .WithMany()
            .HasForeignKey(c => c.UnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(c => c.Payer)
            .WithMany()
            .HasForeignKey(c => c.PayerPersonId)
            .OnDelete(DeleteBehavior.SetNull);

        // O token do link publico precisa ser unico globalmente: a rota que o
        // morador abre nao tem condominio no contexto ainda.
        builder.HasIndex(c => c.PublicToken).IsUnique();

        // Uma unidade tem no maximo uma cobranca por ciclo.
        builder.HasIndex(c => new { c.BillingCycleId, c.UnitId })
            .IsUnique()
            .HasFilter("billing_cycle_id IS NOT NULL");

        // Serve o painel de inadimplencia e "minhas cobrancas".
        builder.HasIndex(c => new { c.CondominiumId, c.Status, c.DueDate });
        builder.HasIndex(c => new { c.CondominiumId, c.UnitId, c.Competence });
        builder.HasIndex(c => c.PayerPersonId);

        // A conferencia periodica procura so os boletos que ainda podem mudar.
        builder.HasIndex(c => new { c.CondominiumId, c.SlipState });

        builder.Ignore(c => c.OutstandingAmount);
        builder.Ignore(c => c.IsSettled);
    }
}

public sealed class ChargeItemConfiguration : IEntityTypeConfiguration<ChargeItem>
{
    public void Configure(EntityTypeBuilder<ChargeItem> builder)
    {
        builder.Property(i => i.Description).HasMaxLength(200).IsRequired();

        builder.HasOne(i => i.Charge)
            .WithMany(c => c.Items)
            .HasForeignKey(i => i.ChargeId)
            // Cascade aqui e correto: o item nao existe sem a cobranca.
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(i => i.LedgerAccount)
            .WithMany()
            .HasForeignKey(i => i.LedgerAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => i.ChargeId);
    }
}

public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.Property(p => p.ExternalId).HasMaxLength(120);
        builder.Property(p => p.Notes).HasMaxLength(500);

        builder.HasOne(p => p.Charge)
            .WithMany(c => c.Payments)
            .HasForeignKey(p => p.ChargeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => new { p.CondominiumId, p.PaidOn });
        builder.HasIndex(p => p.ChargeId);

        // Evita lancar o mesmo PIX duas vezes na conciliacao automatica.
        builder.HasIndex(p => new { p.CondominiumId, p.ExternalId })
            .IsUnique()
            .HasFilter("external_id IS NOT NULL");
    }
}
