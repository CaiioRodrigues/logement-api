namespace Convivium.Domain.Billing;

using Convivium.Domain.Common;
using Convivium.Domain.Condominiums;
using Convivium.Domain.Payments;
using Convivium.Domain.People;

/// <summary>
/// A cobranca de uma unidade em uma competencia — o "boleto do condominio".
/// </summary>
public class Charge : Entity, ITenantScoped
{
    public Guid CondominiumId { get; set; }

    /// <summary>Ciclo que gerou a cobranca. Nulo em cobrancas avulsas fora do rateio.</summary>
    public Guid? BillingCycleId { get; set; }

    public BillingCycle? BillingCycle { get; set; }

    public Guid UnitId { get; set; }

    public Unit Unit { get; set; } = null!;

    /// <summary>Quem recebe e paga: proprietario ou inquilino, conforme a ocupacao responsavel.</summary>
    public Guid? PayerPersonId { get; set; }

    public Person? Payer { get; set; }

    public Competence Competence { get; set; }

    public DateOnly DueDate { get; set; }

    /// <summary>Soma dos itens, congelada na geracao.</summary>
    public decimal TotalAmount { get; set; }

    public decimal PaidAmount { get; set; }

    public ChargeStatus Status { get; set; } = ChargeStatus.Open;

    public DateOnly? PaidOn { get; set; }

    /// <summary>
    /// Token opaco que da acesso ao boleto sem login, para colar no e-mail.
    /// Aleatorio e de uso unico por cobranca.
    /// </summary>
    public string PublicToken { get; set; } = string.Empty;

    /// <summary>Codigo copia-e-cola do PIX (BR Code EMV), gerado no fechamento.</summary>
    public string? PixPayload { get; set; }

    /// <summary>
    /// Identificador da cobranca no provedor bancario, quando houver integracao
    /// de boleto registrado. Fica nulo enquanto a cobranca for so PIX.
    /// </summary>
    public string? ExternalSlipId { get; set; }

    /// <summary>Linha digitavel do boleto bancario: 47 digitos, sem pontuacao.</summary>
    public string? BarcodeLine { get; set; }

    /// <summary>Codigo de barras do boleto bancario: 44 digitos.</summary>
    public string? SlipBarcode { get; set; }

    /// <summary>"Nosso numero": a identificacao do titulo na carteira do banco.</summary>
    public string? SlipOurNumber { get; set; }

    /// <summary>
    /// Copia-e-cola do PIX embutido no boleto hibrido pelo proprio banco.
    /// </summary>
    /// <remarks>
    /// Diferente de <see cref="PixPayload"/>, que e o PIX avulso do condominio:
    /// pagar por este aqui da baixa no titulo registrado, e o banco avisa. Pagar
    /// pelo avulso cai na conta do mesmo jeito, mas o boleto continua em aberto
    /// na camara de compensacao ate alguem baixar na mao.
    /// </remarks>
    public string? SlipPixPayload { get; set; }

    /// <summary>Estado do titulo no banco. Nulo quando a cobranca nao tem boleto.</summary>
    public BoletoState? SlipState { get; set; }

    public DateTimeOffset? SlipIssuedAt { get; set; }

    /// <summary>Ultima vez que o estado do titulo foi conferido com o banco.</summary>
    public DateTimeOffset? SlipSyncedAt { get; set; }

    /// <summary>O boleto esta registrado no banco e pode ser pago.</summary>
    public bool HasRegisteredSlip =>
        SlipState is BoletoState.Registered or BoletoState.Paid
        && !string.IsNullOrWhiteSpace(SlipBarcode);

    public DateTimeOffset? NotifiedAt { get; set; }

    public ICollection<ChargeItem> Items { get; set; } = [];

    public ICollection<Payment> Payments { get; set; } = [];

    public decimal OutstandingAmount => Math.Max(0m, TotalAmount - PaidAmount);

    public bool IsSettled => Status is ChargeStatus.Paid or ChargeStatus.Cancelled;
}
