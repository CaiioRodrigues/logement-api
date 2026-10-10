namespace Convivium.Application.Banking;

using Convivium.Domain.Payments;

/// <summary>
/// Emite e acompanha boletos registrados num banco.
/// </summary>
/// <remarks>
/// <para>
/// Boleto registrado nao e o mesmo que o aviso de cobranca com PIX que o
/// sistema ja emitia: ele existe na camara de compensacao, pode ser pago em
/// qualquer banco ou lotérica, e o banco emissor avisa quando foi pago. Em
/// troca, custa tarifa por titulo e exige conta PJ com convenio de cobranca.
/// </para>
/// <para>
/// A interface fica atras de uma abstracao porque o banco e uma decisao da
/// assembleia, nao do software: trocar de Inter para Cora ou Sicoob nao pode
/// significar reescrever a cobranca.
/// </para>
/// </remarks>
public interface IBoletoProvider
{
    /// <summary>Qual banco este adaptador atende.</summary>
    Domain.Condominiums.BoletoProvider Provider { get; }

    /// <summary>
    /// Registra o titulo no banco. A emissao costuma ser assincrona: o retorno
    /// pode vir sem codigo de barras, com o estado <see cref="BoletoState.Pending"/>.
    /// </summary>
    Task<BoletoRegistration> IssueAsync(
        BoletoIssueRequest request,
        BankCredentials credentials,
        CancellationToken cancellationToken = default);

    /// <summary>Como esta o titulo no banco agora.</summary>
    /// <param name="expected">
    /// Valor e vencimento pedidos na emissao. Quando informado, um codigo de
    /// barras que nao bata com eles e recusado com
    /// <see cref="BoletoProviderException.SlipMismatch"/>.
    /// </param>
    Task<BoletoRegistration> QueryAsync(
        string externalId,
        BankCredentials credentials,
        BoletoExpectation? expected = null,
        CancellationToken cancellationToken = default);

    /// <summary>Baixa o titulo antes do vencimento: ele deixa de ser pagavel.</summary>
    Task CancelAsync(
        string externalId,
        string reason,
        BankCredentials credentials,
        CancellationToken cancellationToken = default);

    /// <summary>A ficha de compensacao em PDF, desenhada pelo proprio banco.</summary>
    Task<byte[]> DownloadPdfAsync(
        string externalId,
        BankCredentials credentials,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pede um token so para conferir se as credenciais funcionam, sem emitir nada.
    /// </summary>
    Task VerifyAsync(BankCredentials credentials, CancellationToken cancellationToken = default);
}

/// <summary>Credenciais ja decifradas de um condominio, prontas para falar com o banco.</summary>
/// <remarks>
/// Vive so em memoria e so durante a chamada. Nao tem <c>ToString</c> proprio
/// de proposito: <see langword="record"/> imprime as propriedades, e um
/// <c>record</c> com segredo dentro de um log e um vazamento — por isso o
/// <see cref="ToString"/> abaixo e sobrescrito.
/// </remarks>
public sealed record BankCredentials
{
    /// <summary>Chave estavel para guardar o token em cache. Nao e segredo.</summary>
    public required string CacheKey { get; init; }

    public required bool Sandbox { get; init; }

    public required string ClientId { get; init; }

    public required string ClientSecret { get; init; }

    /// <summary>Certificado mTLS em PKCS#12.</summary>
    public required byte[] Certificate { get; init; }

    public string? CertificatePassword { get; init; }

    /// <summary>Conta corrente, quando a aplicacao enxerga mais de uma.</summary>
    public string? CheckingAccount { get; init; }

    public override string ToString() => $"BankCredentials {{ CacheKey = {CacheKey}, Sandbox = {Sandbox} }}";
}

/// <summary>O que o codigo de barras devolvido pelo banco tem de trazer.</summary>
public sealed record BoletoExpectation(decimal Amount, DateOnly DueDate);

/// <summary>O que o banco precisa saber para registrar o titulo.</summary>
public sealed record BoletoIssueRequest
{
    /// <summary>
    /// Identificador da cobranca do lado do condominio — o "seu numero" do
    /// banco. E por ele que o retorno do banco reencontra a cobranca aqui.
    /// </summary>
    public required string OwnNumber { get; init; }

    public required decimal Amount { get; init; }

    public required DateOnly DueDate { get; init; }

    /// <summary>Dias apos o vencimento em que o titulo ainda pode ser pago.</summary>
    public required int DaysOnAgenda { get; init; }

    public required BoletoPayer Payer { get; init; }

    /// <summary>Multa por atraso em percentual do titulo (0.02 = 2%).</summary>
    public decimal LateFeeRate { get; init; }

    /// <summary>Juros de mora ao mes (0.01 = 1% a.m.).</summary>
    public decimal MonthlyInterestRate { get; init; }

    /// <summary>Linhas de instrucao impressas na ficha.</summary>
    public IReadOnlyList<string> Instructions { get; init; } = [];
}

/// <summary>
/// Quem paga o boleto. O banco exige CPF ou CNPJ e endereco completo: sem
/// isso ele recusa o registro do titulo.
/// </summary>
public sealed record BoletoPayer
{
    public required string Name { get; init; }

    /// <summary>CPF ou CNPJ somente com digitos.</summary>
    public required string Document { get; init; }

    public required string Street { get; init; }

    public required string City { get; init; }

    /// <summary>Sigla da UF.</summary>
    public required string State { get; init; }

    /// <summary>CEP somente com digitos.</summary>
    public required string ZipCode { get; init; }

    public string? Number { get; init; }

    public string? Complement { get; init; }

    public string? District { get; init; }

    public string? Email { get; init; }
}

/// <summary>O titulo como o banco o conhece agora.</summary>
public sealed record BoletoRegistration
{
    /// <summary>Identificador do titulo no banco. E com ele que se consulta e cancela.</summary>
    public required string ExternalId { get; init; }

    public required BoletoState State { get; init; }

    /// <summary>
    /// Codigo de barras e linha digitavel ja conferidos. Nulo enquanto o
    /// banco nao terminar de registrar.
    /// </summary>
    public BankSlip? Slip { get; init; }

    /// <summary>"Nosso numero": a identificacao do titulo na carteira do banco.</summary>
    public string? OurNumber { get; init; }

    /// <summary>
    /// Copia-e-cola do PIX que o proprio banco embute no boleto hibrido. Pagar
    /// por ele da baixa no titulo, diferente do PIX avulso do condominio.
    /// </summary>
    public string? PixPayload { get; init; }

    public decimal? PaidAmount { get; init; }

    public DateOnly? PaidOn { get; init; }
}

/// <summary>
/// Deu errado do lado do banco. Carrega a mensagem que o banco devolveu para
/// o sindico ver o motivo real, em vez de um "erro ao emitir boleto".
/// </summary>
public sealed class BoletoProviderException(
    string message,
    Exception? innerException = null,
    bool outcomeUnknown = false,
    bool slipMismatch = false,
    string? externalId = null) : Exception(message, innerException)
{
    /// <summary>
    /// O titulo existe no banco, mas com valor, vencimento ou codigo de barras
    /// diferentes do que foi pedido. Quem recebe isto precisa cancelar o
    /// titulo — ele e pagavel, so que errado.
    /// </summary>
    public bool SlipMismatch { get; } = slipMismatch;

    /// <summary>Identificador do titulo no banco, quando ele chegou a existir.</summary>
    public string? ExternalId { get; } = externalId;

    /// <summary>
    /// O pedido pode ter chegado ao banco mesmo com o erro.
    /// </summary>
    /// <remarks>
    /// Acontece quando a conexao cai ou o banco demora a responder depois de
    /// receber uma emissao. Tentar de novo as cegas pode registrar dois titulos
    /// para a mesma cobranca — e o morador recebe dois boletos validos. Nesses
    /// casos a cobranca fica travada ate alguem conferir no internet banking.
    /// </remarks>
    public bool OutcomeUnknown { get; } = outcomeUnknown;
}

/// <summary>
/// Entrega o <see cref="HttpClient"/> ja com o certificado mTLS do condominio.
/// </summary>
/// <remarks>
/// Separado do adaptador para os testes trocarem o transporte por um falso
/// e conferirem como a resposta do banco e lida, sem certificado nem rede.
/// </remarks>
public interface IBankHttpClientSource
{
    HttpClient Get(BankCredentials credentials);

    /// <summary>Descarta o cliente do condominio quando a credencial muda.</summary>
    void Invalidate(string cacheKey);
}
