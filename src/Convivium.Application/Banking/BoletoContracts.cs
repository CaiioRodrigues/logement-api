namespace Convivium.Application.Banking;

using Convivium.Domain.Condominiums;
using Convivium.Domain.Payments;

/// <summary>
/// Configuracao do banco como a tela enxerga: sem nenhum segredo.
/// </summary>
/// <remarks>
/// Client secret e certificado nunca voltam da API, nem para o sindico que
/// os cadastrou. A tela so precisa saber se eles existem — e trocar um deles
/// e mandar o novo, nao editar o antigo.
/// </remarks>
public sealed record BoletoSettingsDto(
    BoletoProvider Provider,
    bool Sandbox,
    string? ClientId,
    bool HasClientSecret,
    bool HasCertificate,
    string? CertificateSubject,
    DateTimeOffset? CertificateExpiresAt,
    string? CheckingAccount,
    int DaysOnAgenda,
    string? Instructions,
    Guid? BankAccountId,
    DateTimeOffset? VerifiedAt,
    bool IsConfigured,
    bool SecretsKeyConfigured,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Atualiza o banco do condominio. Campo secreto nulo ou vazio mantem o que
/// ja estava guardado.
/// </summary>
/// <remarks>
/// O certificado chega de um jeito ou de outro: o par PEM que o Inter entrega
/// (<see cref="CertificatePem"/> e <see cref="PrivateKeyPem"/>), ou um PKCS#12
/// em base64 com a senha dele.
/// </remarks>
public sealed record UpdateBoletoSettingsRequest
{
    public BoletoProvider Provider { get; init; }

    public bool Sandbox { get; init; }

    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public string? CertificatePem { get; init; }

    public string? PrivateKeyPem { get; init; }

    public string? CertificatePkcs12Base64 { get; init; }

    public string? CertificatePassword { get; init; }

    public string? CheckingAccount { get; init; }

    public int DaysOnAgenda { get; init; } = 30;

    public string? Instructions { get; init; }

    public Guid? BankAccountId { get; init; }
}

/// <summary>O boleto registrado de uma cobranca.</summary>
public sealed record BankSlipDto(
    BoletoState State,
    /// <summary>Linha digitavel pontuada, pronta para copiar.</summary>
    string? DigitableLine,
    string? Barcode,
    string? OurNumber,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? SyncedAt,
    /// <summary>
    /// A emissao pode ter chegado ao banco sem resposta. Antes de emitir de
    /// novo, alguem precisa conferir no internet banking.
    /// </summary>
    bool NeedsBankCheck);

/// <summary>Uma cobranca que ficou de fora de uma emissao em lote, e por que.</summary>
public sealed record BoletoBatchItem(Guid ChargeId, string UnitIdentifier, string Reason);

/// <summary>Resultado da emissao dos boletos de um ciclo inteiro.</summary>
public sealed record BoletoBatchResult(
    int Issued,
    /// <summary>Aceitos pelo banco, ainda sem codigo de barras.</summary>
    int Pending,
    /// <summary>Nem foram ao banco: falta CPF, ja estao pagos, ja tem boleto.</summary>
    IReadOnlyList<BoletoBatchItem> Skipped,
    /// <summary>Foram ao banco e voltaram com erro.</summary>
    IReadOnlyList<BoletoBatchItem> Failed);

/// <summary>
/// O que impede de emitir os boletos de um ciclo, conferido sem falar com o
/// banco. E a resposta para "quantos moradores estao sem CPF?".
/// </summary>
public sealed record BoletoReadinessDto(
    bool CanIssue,
    IReadOnlyList<string> SettingsProblems,
    IReadOnlyList<BoletoBatchItem> ChargeProblems,
    int ReadyCharges);

/// <summary>Resultado da conferencia dos boletos em aberto com o banco.</summary>
public sealed record BoletoSyncResult(int Checked, int Changed, int PaymentsRegistered, IReadOnlyList<string> Errors);
