namespace Convivium.Domain.Condominiums;

using Convivium.Domain.Common;

/// <summary>
/// Raiz do multi-tenant: todo dado do sistema pendura em um condominio.
/// </summary>
public class Condominium : Entity
{
    /// <summary>Nome pelo qual o condominio e conhecido. Ex.: "Residencial Aurora".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Razao social registrada na Receita.</summary>
    public string? LegalName { get; set; }

    /// <summary>CNPJ somente com digitos.</summary>
    public string? Cnpj { get; set; }

    public Address Address { get; set; } = new();

    public BillingSettings Billing { get; set; } = new();

    /// <summary>Chave PIX que recebe as taxas condominiais.</summary>
    public string? PixKey { get; set; }

    public PixKeyType? PixKeyType { get; set; }

    /// <summary>Nome do beneficiario no QR Code PIX (limite de 25 caracteres no BR Code).</summary>
    public string? PixReceiverName { get; set; }

    /// <summary>Cidade do beneficiario no QR Code PIX (limite de 15 caracteres).</summary>
    public string? PixReceiverCity { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<Block> Blocks { get; set; } = [];

    public ICollection<Unit> Units { get; set; } = [];
}
