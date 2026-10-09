namespace Convivium.Domain.Condominiums;

/// <summary>Banco que registra os boletos do condominio.</summary>
/// <remarks>
/// O beneficiario do boleto e sempre o CNPJ do condominio, nunca o da
/// administradora: dinheiro de condominio em conta de terceiro e problema
/// juridico antes de ser problema contabil. Por isso a credencial mora em
/// cada condominio, e nao numa configuracao unica do sistema.
/// </remarks>
public enum BoletoProvider
{
    /// <summary>Sem boleto registrado: a cobranca sai so com PIX.</summary>
    None = 0,

    /// <summary>Banco Inter, API de cobranca v3.</summary>
    Inter = 1,
}
