namespace Convivium.Domain.Payments;

/// <summary>Nome dos bancos pelo codigo da camara de compensacao.</summary>
/// <remarks>
/// So os que aparecem em cobranca de condominio. Um codigo fora da lista
/// ainda funciona: sai como "Banco 123" na ficha, sem inventar nome.
/// </remarks>
public static class BankDirectory
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["001"] = "Banco do Brasil",
        ["033"] = "Santander",
        ["077"] = "Banco Inter",
        ["104"] = "Caixa",
        ["237"] = "Bradesco",
        ["260"] = "Nubank",
        ["341"] = "Itaú",
        ["403"] = "Cora",
        ["748"] = "Sicredi",
        ["756"] = "Sicoob",
    };

    public static string NameOf(string bankCode) =>
        Names.TryGetValue(bankCode, out string? name) ? name : $"Banco {bankCode}";
}
