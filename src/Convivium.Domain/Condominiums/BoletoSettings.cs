namespace Convivium.Domain.Condominiums;

/// <summary>
/// Credenciais e parametros do banco que emite os boletos do condominio.
/// Owned type do <see cref="Condominium"/>.
/// </summary>
/// <remarks>
/// Os tres campos terminados em <c>Cipher</c> guardam o texto ja cifrado pelo
/// <c>ISecretProtector</c>. Eles nunca saem da API em claro: quem tem o client
/// secret e o certificado do condominio emite boleto em nome dele e, com os
/// escopos de pagamento, move dinheiro da conta.
/// </remarks>
public class BoletoSettings
{
    public BoletoProvider Provider { get; set; } = BoletoProvider.None;

    /// <summary>
    /// Usa o ambiente de homologacao do banco em vez do de producao.
    /// </summary>
    /// <remarks>
    /// Existe para o sindico conferir o layout do boleto antes de mandar o
    /// primeiro para um morador. Boleto de homologacao nao e cobravel.
    /// </remarks>
    public bool Sandbox { get; set; }

    public string? ClientId { get; set; }

    /// <summary>Client secret cifrado.</summary>
    public string? ClientSecretCipher { get; set; }

    /// <summary>Certificado mTLS em PKCS#12, cifrado.</summary>
    public string? CertificateCipher { get; set; }

    /// <summary>Senha do PKCS#12, cifrada. Nula quando o arquivo nao tem senha.</summary>
    public string? CertificatePasswordCipher { get; set; }

    /// <summary>
    /// Conta corrente do condominio, so com digitos. Obrigatoria quando a
    /// aplicacao do banco enxerga mais de uma conta.
    /// </summary>
    public string? CheckingAccount { get; set; }

    /// <summary>
    /// Dias apos o vencimento em que o boleto ainda pode ser pago; passados
    /// eles, o banco da baixa por decurso de prazo.
    /// </summary>
    public int DaysOnAgenda { get; set; } = 30;

    /// <summary>Texto que o banco imprime no campo de instrucoes da ficha.</summary>
    public string? Instructions { get; set; }

    /// <summary>Ultima vez que as credenciais foram aceitas pelo banco.</summary>
    public DateTimeOffset? VerifiedAt { get; set; }

    /// <summary>Tem o bastante para tentar falar com o banco.</summary>
    public bool IsConfigured =>
        Provider is not BoletoProvider.None
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecretCipher)
        && !string.IsNullOrWhiteSpace(CertificateCipher);
}
