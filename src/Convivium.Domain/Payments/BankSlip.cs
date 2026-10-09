namespace Convivium.Domain.Payments;

/// <summary>Um boleto bancario ja lido e conferido digito por digito.</summary>
public sealed record BankSlip
{
    /// <summary>Os 44 digitos que viram as barras da ficha de compensacao.</summary>
    public required string Barcode { get; init; }

    /// <summary>Os 47 digitos que o pagador digita no aplicativo do banco.</summary>
    public required string DigitableLine { get; init; }

    /// <summary>Codigo do banco emissor na camara de compensacao. Ex.: "077".</summary>
    public required string BankCode { get; init; }

    /// <summary>Vencimento guardado no fator de vencimento.</summary>
    public required DateOnly DueDate { get; init; }

    /// <summary>Valor do titulo lido dos centavos do codigo de barras.</summary>
    public required decimal Amount { get; init; }

    /// <summary>Os 25 digitos de uso do banco emissor.</summary>
    public required string FreeField { get; init; }

    /// <summary>A linha digitavel pontuada, como ela e impressa.</summary>
    public string FormattedDigitableLine => BoletoLayout.FormatDigitableLine(DigitableLine);
}
