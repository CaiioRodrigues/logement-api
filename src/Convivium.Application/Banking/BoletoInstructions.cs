namespace Convivium.Application.Banking;

using Convivium.Domain.Common;
using Convivium.Domain.Condominiums;

/// <summary>
/// As linhas de instrucao da ficha. Moram num lugar so porque saem em dois:
/// no pedido ao banco e na ficha que o sistema desenha.
/// </summary>
public static class BoletoInstructions
{
    public const int MaxLines = 5;

    public static IReadOnlyList<string> Build(Competence competence, string unit, Condominium condominium)
    {
        var lines = new List<string> { $"Taxa condominial {competence} - {unit}" };

        BillingSettings billing = condominium.Billing;

        if (billing.LateFeeRate > 0 || billing.MonthlyInterestRate > 0)
        {
            lines.Add(
                $"Após o vencimento: multa de {billing.LateFeeRate * 100:0.##}% " +
                $"e juros de {billing.MonthlyInterestRate * 100:0.##}% ao mês.");
        }

        if (condominium.Boleto.Instructions is { Length: > 0 } extra)
        {
            lines.AddRange(extra.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return [.. lines.Take(MaxLines)];
    }
}
