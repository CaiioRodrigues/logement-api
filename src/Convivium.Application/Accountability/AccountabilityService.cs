namespace Convivium.Application.Accountability;

using Convivium.Application.Abstractions;
using Convivium.Domain.Billing;
using Convivium.Domain.Common;
using Convivium.Domain.Condominiums;
using Convivium.Domain.Expenses;
using Convivium.Domain.Finance;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Monta o balancete que o sindico leva para a assembleia.
/// </summary>
/// <remarks>
/// Le do livro caixa e nao das cobrancas ou das despesas: cobranca e despesa
/// dizem o que era para acontecer, o lancamento diz o que aconteceu. Numa
/// prestacao de contas quem manda e a segunda.
/// </remarks>
public sealed class AccountabilityService(IApplicationDbContext db, IClock clock)
{
    public async Task<MonthlyStatement> GetMonthlyAsync(
        Competence competence,
        CancellationToken cancellationToken = default)
    {
        DateOnly from = competence.FirstDay;
        DateOnly to = competence.LastDay;

        Condominium condominium = await db.Condominiums
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new DomainException("Nenhum condomínio ativo no contexto da requisição.");

        var warnings = new List<string>();

        // Saldo anterior por conta: abertura mais tudo que se moveu antes do
        // primeiro dia. Saldo nunca e coluna gravada — seria a primeira coisa a
        // divergir do extrato.
        var accounts = await db.BankAccounts
            .AsNoTracking()
            .Select(a => new
            {
                a.Id,
                a.Name,
                a.IsReserveFund,
                a.IsActive,
                Opening = a.OpeningBalance + a.Entries
                    .Where(e => e.Date < from)
                    .Sum(e => e.Direction == EntryDirection.In ? e.Amount : -e.Amount),
                In = a.Entries
                    .Where(e => e.Date >= from && e.Date <= to && e.Direction == EntryDirection.In)
                    .Sum(e => e.Amount),
                Out = a.Entries
                    .Where(e => e.Date >= from && e.Date <= to && e.Direction == EntryDirection.Out)
                    .Sum(e => e.Amount),
            })
            .ToListAsync(cancellationToken);

        // Conta inativa entra se movimentou ou se tinha saldo: sumir com ela
        // faria o balancete deixar de fechar com o extrato dela.
        var saldos = accounts
            .Where(a => a.IsActive || a.Opening != 0 || a.In != 0 || a.Out != 0)
            .OrderByDescending(a => !a.IsReserveFund)
            .ThenBy(a => a.Name, StringComparer.CurrentCulture)
            .Select(a => new StatementAccountBalance(
                a.Name, a.IsReserveFund, a.Opening, a.In, a.Out, a.Opening + a.In - a.Out))
            .ToList();

        var entries = await db.LedgerEntries
            .AsNoTracking()
            .Include(e => e.LedgerAccount)
            .Include(e => e.BankAccount)
            .Where(e => e.Date >= from && e.Date <= to)
            .OrderBy(e => e.Date)
            .ThenBy(e => e.CreatedAt)
            .ToListAsync(cancellationToken);

        var movimento = entries
            .Select(e => new StatementEntry(
                e.Date,
                e.Description,
                e.LedgerAccount.Code,
                e.LedgerAccount.Name,
                e.BankAccount.Name,
                e.DocumentNumber,
                e.Direction == EntryDirection.In,
                e.Amount,
                e.ReconciledAt is not null))
            .ToList();

        decimal totalIncome = movimento.Where(e => e.IsIncome).Sum(e => e.Amount);
        decimal totalExpense = movimento.Where(e => !e.IsIncome).Sum(e => e.Amount);

        var receitas = StatementMath.GroupByAccount(movimento.Where(e => e.IsIncome), totalIncome);
        var despesas = StatementMath.GroupByAccount(movimento.Where(e => !e.IsIncome), totalExpense);

        decimal opening = saldos.Sum(a => a.Opening);
        decimal closing = saldos.Sum(a => a.Closing);

        if (movimento.Count == 0)
        {
            warnings.Add(
                $"Nenhum lançamento no caixa entre {from:dd/MM/yyyy} e {to:dd/MM/yyyy}. " +
                "Confira se os recebimentos e pagamentos do mês foram registrados.");
        }

        int semConferir = movimento.Count(e => !e.Reconciled);
        if (semConferir > 0)
        {
            warnings.Add(
                $"{semConferir} lançamento(s) ainda não foram conferidos contra o extrato bancário.");
        }

        if (totalExpense > totalIncome && movimento.Count > 0)
        {
            warnings.Add(
                $"As despesas do mês superaram as receitas em " +
                $"{Money(totalExpense - totalIncome)}. O saldo anterior cobriu a diferença.");
        }

        if (closing < 0)
        {
            warnings.Add("O saldo final está negativo. Confira os lançamentos antes de apresentar.");
        }

        var aPagar = await ContasAPagarAsync(to, cancellationToken);
        var aReceber = await ContasAReceberAsync(to, cancellationToken);

        if (aPagar.Count > 0)
        {
            decimal total = aPagar.Sum(p => p.Amount);

            if (total > closing)
            {
                warnings.Add(
                    $"Há {Money(total)} em contas a pagar e {Money(closing)} em caixa. " +
                    "Confira antes de aprovar despesa nova.");
            }
        }

        return new MonthlyStatement(
            competence.ToString(),
            from,
            to,
            condominium.Name,
            condominium.Cnpj,
            condominium.Address.ToString(),
            opening,
            totalIncome,
            totalExpense,
            totalIncome - totalExpense,
            closing,
            receitas,
            despesas,
            saldos,
            movimento,
            aPagar,
            aReceber,
            FundoDeReserva(saldos, closing),
            await ResumoDaInadimplenciaAsync(to, condominium, cancellationToken),
            warnings);
    }

    /// <summary>
    /// Contas do condominio ainda nao pagas no fim do periodo.
    /// </summary>
    /// <remarks>
    /// Pelo vencimento, e nao pela competencia: o que interessa aqui e o que
    /// ja deveria ter saido do caixa, nao a que mes a despesa pertence. Conta
    /// lancada hoje com vencimento em marco nao e pendencia de janeiro.
    /// </remarks>
    private async Task<IReadOnlyList<PendingItem>> ContasAPagarAsync(
        DateOnly ate,
        CancellationToken cancellationToken)
    {
        var pendentes = await db.Expenses
            .AsNoTracking()
            .Include(e => e.Supplier)
            .Where(e => e.Status == ExpenseStatus.Pending)
            .Where(e => e.DueDate <= ate)
            .OrderBy(e => e.DueDate)
            .ThenByDescending(e => e.Amount)
            .ToListAsync(cancellationToken);

        return pendentes
            .Select(e => new PendingItem(
                e.Description,
                e.Supplier?.Name,
                e.DueDate,
                e.Amount,
                DiasDeAtraso(e.DueDate, ate)))
            .ToList();
    }

    /// <summary>
    /// Cobrancas ainda em aberto no fim do periodo, uma linha por cobranca.
    /// </summary>
    /// <remarks>
    /// Sem multa e juros de proposito: aqui vale o valor nominal, que e o que
    /// entra no confronto com as contas a pagar. Os encargos apurados ficam no
    /// resumo de inadimplencia, que responde outra pergunta.
    /// </remarks>
    private async Task<IReadOnlyList<PendingItem>> ContasAReceberAsync(
        DateOnly ate,
        CancellationToken cancellationToken)
    {
        var abertas = await db.Charges
            .AsNoTracking()
            .Include(c => c.Unit).ThenInclude(u => u.Block)
            .Where(c => c.Status != ChargeStatus.Paid && c.Status != ChargeStatus.Cancelled)
            .Where(c => c.DueDate <= ate)
            .OrderBy(c => c.DueDate)
            .ToListAsync(cancellationToken);

        return abertas
            .Select(c => new PendingItem(
                $"Cobrança de {c.Competence}",
                c.Unit?.FullIdentifier,
                c.DueDate,
                c.OutstandingAmount,
                DiasDeAtraso(c.DueDate, ate)))
            .ToList();
    }

    /// <summary>
    /// Junta as contas carimbadas como fundo de reserva numa linha so.
    /// </summary>
    private static ReserveFundBalance FundoDeReserva(
        IReadOnlyList<StatementAccountBalance> contas,
        decimal saldoTotal)
    {
        var fundo = contas.Where(c => c.IsReserveFund).ToList();

        decimal fechamento = fundo.Sum(c => c.Closing);

        return new ReserveFundBalance(
            fundo.Sum(c => c.Opening),
            fundo.Sum(c => c.In),
            fundo.Sum(c => c.Out),
            fechamento,
            saldoTotal > 0 ? fechamento / saldoTotal : 0m);
    }

    private static int DiasDeAtraso(DateOnly vencimento, DateOnly referencia) =>
        referencia > vencimento ? referencia.DayNumber - vencimento.DayNumber : 0;

    /// <summary>
    /// O que estava vencido e em aberto no ultimo dia do periodo.
    /// </summary>
    /// <remarks>
    /// Multa e juros sao calculados ate a data do balancete, e nao ate hoje:
    /// um documento de setembro reaberto em dezembro precisa mostrar os mesmos
    /// numeros que mostrou na assembleia.
    /// </remarks>
    private async Task<DelinquencySummary> ResumoDaInadimplenciaAsync(
        DateOnly ate,
        Condominium condominium,
        CancellationToken cancellationToken)
    {
        DateOnly referencia = ate < clock.Today ? ate : clock.Today;

        var abertas = await db.Charges
            .AsNoTracking()
            .Where(c => c.Status != ChargeStatus.Paid && c.Status != ChargeStatus.Cancelled)
            .Where(c => c.DueDate < referencia)
            .Select(c => new { c.UnitId, c.OutstandingAmount, c.DueDate })
            .ToListAsync(cancellationToken);

        decimal encargos = abertas.Sum(c => LateChargeCalculator.Compute(
            c.OutstandingAmount,
            c.DueDate,
            referencia,
            condominium.Billing.LateFeeRate,
            condominium.Billing.MonthlyInterestRate).Total);

        return new DelinquencySummary(
            abertas.Select(c => c.UnitId).Distinct().Count(),
            abertas.Sum(c => c.OutstandingAmount),
            encargos);
    }

    private static string Money(decimal value) =>
        $"R$ {value.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"))}";
}
