namespace Convivium.Application.Billing;

/// <summary>
/// Tudo que o boleto em PDF precisa mostrar, ja resolvido.
/// </summary>
/// <remarks>
/// O gerador de PDF recebe este modelo pronto e nao consulta o banco:
/// assim ele fica testavel sem infraestrutura e a regra de calculo de
/// multa e juros mora num lugar so.
/// </remarks>
public sealed record ChargeDocument
{
    public required string CondominiumName { get; init; }

    public string? CondominiumCnpj { get; init; }

    public required string CondominiumAddress { get; init; }

    public required string UnitIdentifier { get; init; }

    public string? PayerName { get; init; }

    public required string Competence { get; init; }

    public required DateOnly DueDate { get; init; }

    public required decimal TotalAmount { get; init; }

    public decimal PaidAmount { get; init; }

    public decimal LateFee { get; init; }

    public decimal Interest { get; init; }

    /// <summary>Valor a pagar hoje, ja com multa e juros.</summary>
    public required decimal TotalDue { get; init; }

    public int DaysLate { get; init; }

    public required IReadOnlyList<ChargeItemDto> Items { get; init; }

    /// <summary>Copia-e-cola do PIX. Nulo quando o condominio nao tem chave cadastrada.</summary>
    public string? PixPayload { get; init; }

    /// <summary>Link publico para o morador abrir a cobranca no navegador.</summary>
    public string? PublicUrl { get; init; }

    /// <summary>Observacao do ciclo, ex.: "inclui rateio da pintura da fachada".</summary>
    public string? Notes { get; init; }

    public bool IsPaid { get; init; }

    public bool IsOverdue => DaysLate > 0 && !IsPaid;

    /// <summary>
    /// Ficha de compensacao do boleto registrado. Nula quando a cobranca e so
    /// PIX — ai o documento continua sendo o aviso de cobranca de antes.
    /// </summary>
    public BankSlipDocument? Slip { get; init; }
}

/// <summary>O que a ficha de compensacao imprime, ja conferido e formatado.</summary>
public sealed record BankSlipDocument
{
    /// <summary>Ex.: "Banco Inter".</summary>
    public required string BankName { get; init; }

    /// <summary>Codigo do banco com o DV, como sai no cabecalho. Ex.: "077-9".</summary>
    public required string BankCode { get; init; }

    /// <summary>Linha digitavel pontuada.</summary>
    public required string DigitableLine { get; init; }

    /// <summary>Os 44 digitos que viram as barras.</summary>
    public required string Barcode { get; init; }

    public string? OurNumber { get; init; }

    /// <summary>Numero do documento: o "seu numero" enviado ao banco.</summary>
    public string? DocumentNumber { get; init; }

    public required string BeneficiaryName { get; init; }

    public string? BeneficiaryDocument { get; init; }

    public required string PayerName { get; init; }

    public string? PayerDocument { get; init; }

    public required string PayerAddress { get; init; }

    /// <summary>Valor do titulo — o que esta gravado no codigo de barras.</summary>
    public required decimal Amount { get; init; }

    public required DateOnly DueDate { get; init; }

    public IReadOnlyList<string> Instructions { get; init; } = [];
}

/// <summary>Gera o boleto de cobranca, em PDF ou em imagem.</summary>
public interface IChargeDocumentRenderer
{
    /// <summary>PDF, para arquivar, imprimir e anexar no e-mail.</summary>
    byte[] Render(ChargeDocument document);

    /// <summary>
    /// PNG da primeira pagina, para mandar no WhatsApp.
    /// </summary>
    /// <remarks>
    /// Existe por causa de como o WhatsApp mostra cada coisa: PDF chega como
    /// cartao de documento, que o morador precisa tocar para abrir, e imagem
    /// aparece aberta na conversa. Com o QR Code do PIX visivel sem abrir
    /// nada, o boleto e pago no mesmo minuto em que chega.
    /// </remarks>
    byte[] RenderImage(ChargeDocument document);
}
