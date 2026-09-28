namespace Convivium.Application.Billing;

using System.Globalization;
using System.Security.Cryptography;
using Convivium.Application.Abstractions;
using Convivium.Application.Common;
using Convivium.Domain.Billing;
using Convivium.Domain.Common;
using Convivium.Domain.Condominiums;
using Convivium.Domain.Metering;
using Convivium.Domain.Expenses;
using Convivium.Domain.Finance;
using Convivium.Domain.Payments;
using Convivium.Domain.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// O rateio mensal e as cobrancas por unidade.
/// </summary>
/// <remarks>
/// O ciclo de vida de uma competencia e Draft -> Closed -> Published.
/// Fechar congela os valores e gera uma cobranca por unidade; publicar
/// gera o PIX e libera o acesso do morador. Depois de fechado, mexer nas
/// despesas da competencia nao muda mais o que foi cobrado — que e
/// exatamente o ponto: o morador precisa poder confiar no boleto que recebeu.
/// </remarks>
public sealed class BillingService(
    IApplicationDbContext db,
    IClock clock,
    IOptions<ConviviumOptions> options)
{
    /// <summary>
    /// Simula o rateio da competencia sem gravar nada, para conferencia do
    /// sindico e aprovacao do conselho antes de fechar.
    /// </summary>
    public async Task<ApportionmentPreview> PreviewAsync(
        Competence competence,
        ApportionmentMethod? method = null,
        CancellationToken cancellationToken = default)
    {
        Condominium condominium = await LoadCondominiumAsync(cancellationToken);
        ApportionmentMethod effectiveMethod = method ?? condominium.Billing.DefaultApportionmentMethod;

        var warnings = new List<string>();

        var expenses = await db.Expenses
            .AsNoTracking()
            .Where(e => e.Competence == competence)
            .Where(e => e.IsApportionable)
            .Where(e => e.Status != ExpenseStatus.Cancelled)
            .Select(e => new
            {
                e.Amount,
                e.Status,
                Code = e.LedgerAccount.Code,
                AccountName = e.LedgerAccount.Name,
            })
            .ToListAsync(cancellationToken);

        decimal apportionableTotal = expenses.Sum(e => e.Amount);

        int stillPending = expenses.Count(e => e.Status == ExpenseStatus.Pending);
        if (stillPending > 0)
        {
            warnings.Add(
                $"{stillPending} despesa(s) da competência ainda estão em aberto. " +
                "O valor pode mudar até o fechamento.");
        }

        if (apportionableTotal <= 0)
        {
            warnings.Add("Nenhuma despesa rateável lançada nesta competência.");
        }

        var breakdown = expenses
            .GroupBy(e => new { e.Code, e.AccountName })
            .Select(g => new ExpenseBreakdownLine(
                g.Key.Code, g.Key.AccountName, g.Sum(e => e.Amount), g.Count()))
            .OrderByDescending(l => l.Amount)
            .ToList();

        var units = await LoadBillableUnitsAsync(cancellationToken);

        if (units.Count == 0)
        {
            warnings.Add("Nenhuma unidade ativa cadastrada.");

            return new ApportionmentPreview(
                competence.ToString(), effectiveMethod, apportionableTotal,
                condominium.Billing.ReserveFundRate, 0m, 0m, 0, expenses.Count,
                breakdown, [], warnings);
        }

        decimal fractionSum = units.Sum(u => u.IdealFraction);
        if (effectiveMethod == ApportionmentMethod.IdealFraction && Math.Abs(fractionSum - 1m) > 0.0001m)
        {
            warnings.Add(
                $"A soma das frações ideais é {fractionSum:N6} em vez de 1. " +
                "Corrija o cadastro das unidades para o rateio ficar correto.");
        }

        decimal reserveFundTotal = Math.Round(
            apportionableTotal * condominium.Billing.ReserveFundRate, 2, MidpointRounding.AwayFromZero);

        var metered = await LoadMeteredAsync(competence, cancellationToken);
        var lines = BuildShares(apportionableTotal, reserveFundTotal, units, effectiveMethod, metered);

        return new ApportionmentPreview(
            competence.ToString(),
            effectiveMethod,
            apportionableTotal,
            condominium.Billing.ReserveFundRate,
            reserveFundTotal,
            lines.Sum(l => l.Total),
            units.Count,
            expenses.Count,
            breakdown,
            lines,
            warnings,
            lines.Sum(l => l.Metered));
    }

    public async Task<IReadOnlyList<BillingCycleDto>> ListCyclesAsync(
        CancellationToken cancellationToken = default)
        => await db.BillingCycles
            .AsNoTracking()
            .OrderByDescending(c => c.Competence)
            .Select(ToCycleDto())
            .ToListAsync(cancellationToken);

    public async Task<BillingCycleDto> GetCycleAsync(Guid id, CancellationToken cancellationToken = default)
        => await db.BillingCycles
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(ToCycleDto())
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("Ciclo de cobrança não encontrado.");

    /// <summary>Abre a competencia em rascunho, pronta para receber despesas.</summary>
    public async Task<BillingCycleDto> OpenCycleAsync(
        OpenBillingCycleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Competence competence = Competence.Parse(request.Competence);
        Condominium condominium = await LoadCondominiumAsync(cancellationToken);

        bool exists = await db.BillingCycles.AnyAsync(c => c.Competence == competence, cancellationToken);
        DomainException.ThrowIf(exists, $"Já existe um ciclo para a competência {competence}.");

        var cycle = new BillingCycle
        {
            Competence = competence,
            DueDate = request.DueDate ?? DefaultDueDate(competence, condominium.Billing.DueDay),
            Method = request.Method ?? condominium.Billing.DefaultApportionmentMethod,
            ReserveFundRate = condominium.Billing.ReserveFundRate,
            Notes = request.Notes?.Trim(),
        };

        db.BillingCycles.Add(cycle);
        await db.SaveChangesAsync(cancellationToken);

        return await GetCycleAsync(cycle.Id, cancellationToken);
    }

    /// <summary>
    /// Congela os valores e gera uma cobranca por unidade.
    /// </summary>
    public async Task<BillingCycleDto> CloseCycleAsync(
        Guid cycleId,
        Guid? closedByPersonId,
        CancellationToken cancellationToken = default)
    {
        BillingCycle cycle = await db.BillingCycles
            .FirstOrDefaultAsync(c => c.Id == cycleId, cancellationToken)
            ?? throw new KeyNotFoundException("Ciclo de cobrança não encontrado.");

        DomainException.ThrowIf(
            cycle.Status != BillingCycleStatus.Draft,
            $"O ciclo de {cycle.Competence} já foi fechado.");

        Condominium condominium = await LoadCondominiumAsync(cancellationToken);

        decimal apportionableTotal = await db.Expenses
            .Where(e => e.Competence == cycle.Competence)
            .Where(e => e.IsApportionable && e.Status != ExpenseStatus.Cancelled)
            .SumAsync(e => e.Amount, cancellationToken);

        DomainException.ThrowIf(
            apportionableTotal <= 0,
            $"Não há despesas rateáveis na competência {cycle.Competence}. " +
            "Lance as despesas do mês antes de fechar o rateio.");

        var units = await LoadBillableUnitsAsync(cancellationToken);
        DomainException.ThrowIf(units.Count == 0, "Nenhuma unidade ativa para cobrar.");

        decimal reserveFundTotal = Math.Round(
            apportionableTotal * cycle.ReserveFundRate, 2, MidpointRounding.AwayFromZero);

        var metered = await LoadMeteredAsync(cycle.Competence, cancellationToken);
        var lines = BuildShares(apportionableTotal, reserveFundTotal, units, cycle.Method, metered);

        Guid? condoFeeAccount = await FindAccountIdAsync("4.1", cancellationToken);
        Guid? reserveFundAccount = await FindAccountIdAsync("4.2", cancellationToken);
        Guid? meteredAccount = await FindAccountIdAsync("4.6", cancellationToken);

        foreach (ApportionmentPreviewLine line in lines)
        {
            var charge = new Charge
            {
                CondominiumId = cycle.CondominiumId,
                BillingCycleId = cycle.Id,
                UnitId = line.UnitId,
                PayerPersonId = line.PayerPersonId,
                Competence = cycle.Competence,
                DueDate = cycle.DueDate,
                TotalAmount = line.Total,
                PublicToken = GeneratePublicToken(),
            };

            charge.Items.Add(new ChargeItem
            {
                Kind = ChargeItemKind.CondoFee,
                Description = $"Taxa condominial - {cycle.Competence}",
                Amount = line.CondoFee,
                LedgerAccountId = condoFeeAccount,
                Sort = 1,
            });

            if (line.ReserveFund > 0)
            {
                charge.Items.Add(new ChargeItem
                {
                    Kind = ChargeItemKind.ReserveFund,
                    // Formatado na mao: ":P0" depende da cultura do servidor e
                    // renderiza "10 %" com espaco em varios locales.
                    Description = $"Fundo de reserva ({cycle.ReserveFundRate * 100:0.##}%)",
                    Amount = line.ReserveFund,
                    LedgerAccountId = reserveFundAccount,
                    Sort = 2,
                });
            }

            if (line.Metered > 0)
            {
                charge.Items.Add(new ChargeItem
                {
                    Kind = ChargeItemKind.Metered,
                    // O consumo vai na descricao porque e o que o morador
                    // confere contra o proprio medidor; o valor sozinho nao
                    // permite discordar.
                    //
                    // Cultura explicita: o servidor roda em invariante e "N3"
                    // sairia "6.033 m3", que um brasileiro le como seis mil.
                    Description = $"Gás - {line.MeteredConsumption.ToString("N3", PtBr)} m³",
                    Amount = line.Metered,
                    LedgerAccountId = meteredAccount,
                    Sort = 3,
                });
            }

            db.Charges.Add(charge);
        }

        cycle.ApportionableTotal = apportionableTotal;
        cycle.ReserveFundTotal = reserveFundTotal;
        cycle.ChargedTotal = lines.Sum(l => l.Total);
        cycle.Status = BillingCycleStatus.Closed;
        cycle.ClosedAt = clock.Now;
        cycle.ClosedByPersonId = closedByPersonId;

        await db.SaveChangesAsync(cancellationToken);

        return await GetCycleAsync(cycle.Id, cancellationToken);
    }

    /// <summary>
    /// Publica o ciclo: gera o PIX de cada cobranca e libera o acesso do morador.
    /// </summary>
    public async Task<BillingCycleDto> PublishAsync(
        Guid cycleId,
        CancellationToken cancellationToken = default)
    {
        BillingCycle cycle = await db.BillingCycles
            .FirstOrDefaultAsync(c => c.Id == cycleId, cancellationToken)
            ?? throw new KeyNotFoundException("Ciclo de cobrança não encontrado.");

        DomainException.ThrowIf(
            cycle.Status != BillingCycleStatus.Closed,
            "Só é possível publicar um ciclo que já foi fechado.");

        Condominium condominium = await LoadCondominiumAsync(cancellationToken);

        var charges = await db.Charges
            .Include(c => c.Unit)
            .Where(c => c.BillingCycleId == cycle.Id)
            .ToListAsync(cancellationToken);

        foreach (Charge charge in charges)
        {
            charge.PixPayload = BuildPixPayload(condominium, charge, charge.TotalAmount);
        }

        cycle.Status = BillingCycleStatus.Published;
        cycle.PublishedAt = clock.Now;

        await db.SaveChangesAsync(cancellationToken);

        return await GetCycleAsync(cycle.Id, cancellationToken);
    }

    // --- Cobrancas ---

    public async Task<PagedResult<ChargeDto>> ListChargesAsync(
        ChargeFilter filter,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Charge> query = ApplyFilter(db.Charges.AsNoTracking(), filter, clock.Today);

        int total = await query.CountAsync(cancellationToken);

        var charges = await query
            .Include(c => c.Unit).ThenInclude(u => u.Block)
            .Include(c => c.Items)
            .Include(c => c.Payer)
            .OrderBy(c => c.DueDate)
            .ThenBy(c => c.Unit.Identifier)
            .Skip(page.Skip)
            .Take(page.NormalizedPageSize)
            .ToListAsync(cancellationToken);

        Condominium condominium = await LoadCondominiumAsync(cancellationToken);
        var items = charges.Select(c => ToDto(c, condominium)).ToList();

        return new PagedResult<ChargeDto>(items, total, page.NormalizedPage, page.NormalizedPageSize);
    }

    public async Task<ChargeDto> GetChargeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Charge charge = await LoadChargeGraph().FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Cobrança não encontrada.");

        return ToDto(charge, await LoadCondominiumAsync(cancellationToken));
    }

    /// <summary>Cobrancas das unidades onde a pessoa mora ou e proprietaria.</summary>
    public async Task<IReadOnlyList<ChargeDto>> GetChargesForPersonAsync(
        Guid personId,
        CancellationToken cancellationToken = default)
    {
        var unitIds = await db.UnitOccupancies
            .Where(o => o.PersonId == personId)
            .Where(o => o.EndedOn == null || o.EndedOn >= clock.Today)
            .Select(o => o.UnitId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var charges = await LoadChargeGraph()
            .Where(c => unitIds.Contains(c.UnitId) || c.PayerPersonId == personId)
            .Where(c => c.Status != ChargeStatus.Cancelled)
            .OrderByDescending(c => c.DueDate)
            .ToListAsync(cancellationToken);

        Condominium condominium = await LoadCondominiumAsync(cancellationToken);
        return charges.Select(c => ToDto(c, condominium)).ToList();
    }

    /// <summary>
    /// Desfaz o ultimo recebimento de uma cobranca.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Desfaz o mais recente, e nao todos: e o Ctrl+Z de quem marcou a linha
    /// errada ou digitou o valor errado. Quem lancou tres parciais e quer
    /// zerar clica tres vezes, o que e explicito; apagar tudo de uma vez seria
    /// destrutivo demais para um botao que se aperta sem pensar.
    /// </para>
    /// <para>
    /// Recusa quando o lancamento ja foi conferido contra o extrato. Apagar
    /// um lancamento conciliado deixa o sistema divergindo do banco em
    /// silencio, e silencio e o pior jeito de descobrir isso.
    /// </para>
    /// </remarks>
    public async Task<ChargeDto> ReversePaymentAsync(
        Guid chargeId,
        CancellationToken cancellationToken = default)
    {
        Charge charge = await db.Charges
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == chargeId, cancellationToken)
            ?? throw new KeyNotFoundException("Cobrança não encontrada.");

        Payment? payment = await db.Payments
            .Where(p => p.ChargeId == charge.Id)
            .OrderByDescending(p => p.PaidOn)
            .ThenByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        DomainException.ThrowIf(
            payment is null,
            "Esta cobrança não tem recebimento para estornar.");

        LedgerEntry? entry = await db.LedgerEntries
            .FirstOrDefaultAsync(e => e.PaymentId == payment!.Id, cancellationToken);

        DomainException.ThrowIf(
            entry?.ReconciledAt is not null,
            "Este recebimento já foi conferido contra o extrato bancário e não pode ser " +
            "estornado. Lance um ajuste no caixa para corrigir.");

        if (entry is not null)
        {
            db.LedgerEntries.Remove(entry);
        }

        db.Payments.Remove(payment!);

        charge.PaidAmount -= payment!.Amount;

        if (charge.PaidAmount < 0)
        {
            charge.PaidAmount = 0;
        }

        // Volta para "em aberto" ou "parcial" conforme o que sobrou. Nao
        // precisa decidir entre aberta e vencida: o status vencido e derivado
        // da data na hora de montar o DTO, e nao gravado.
        charge.Status = charge.PaidAmount > 0 ? ChargeStatus.PartiallyPaid : ChargeStatus.Open;
        charge.PaidOn = null;

        await db.SaveChangesAsync(cancellationToken);

        return await GetChargeAsync(charge.Id, cancellationToken);
    }

    /// <summary>
    /// Cobranca acessada pelo link publico do e-mail, sem login.
    /// </summary>
    /// <remarks>
    /// Precisa de <c>IgnoreQueryFilters</c>: quem abre o link nao tem token,
    /// entao nao ha condominio ativo. A autorizacao aqui e o proprio token,
    /// que tem 256 bits de entropia e vale para uma unica cobranca.
    /// </remarks>
    public async Task<ChargeDto> GetByPublicTokenAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        DomainException.ThrowIf(string.IsNullOrWhiteSpace(token), "Link inválido.");

        Charge charge = await LoadChargeGraph()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.PublicToken == token, cancellationToken)
            ?? throw new KeyNotFoundException("Cobrança não encontrada ou link expirado.");

        Condominium condominium = await db.Condominiums
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstAsync(c => c.Id == charge.CondominiumId, cancellationToken);

        return ToDto(charge, condominium);
    }

    /// <summary>
    /// Registra o recebimento e gera a entrada no caixa.
    /// </summary>
    public async Task<ChargeDto> RegisterPaymentAsync(
        Guid chargeId,
        RegisterPaymentRequest request,
        Guid? registeredByPersonId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DomainException.ThrowIf(request.Amount <= 0, "O valor recebido deve ser maior que zero.");

        Charge charge = await db.Charges
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == chargeId, cancellationToken)
            ?? throw new KeyNotFoundException("Cobrança não encontrada.");

        DomainException.ThrowIf(charge.Status == ChargeStatus.Cancelled, "Cobrança cancelada.");
        DomainException.ThrowIf(charge.Status == ChargeStatus.Paid, "Cobrança já quitada.");

        bool accountExists = await db.BankAccounts
            .AnyAsync(a => a.Id == request.BankAccountId && a.IsActive, cancellationToken);
        DomainException.ThrowIf(!accountExists, "Conta bancária não encontrada ou inativa.");

        DateOnly paidOn = request.PaidOn ?? clock.Today;

        // A receita vai para a conta da taxa condominial; na falta dela,
        // para o primeiro item da cobranca que tenha conta contabil.
        Guid revenueAccountId = await FindAccountIdAsync("4.1", cancellationToken)
            ?? charge.Items.FirstOrDefault(i => i.LedgerAccountId is not null)?.LedgerAccountId
            ?? throw new DomainException(
                "Cadastre a conta 4.1 (Taxa Condominial) no plano de contas antes de receber.");

        var payment = new Payment
        {
            CondominiumId = charge.CondominiumId,
            ChargeId = charge.Id,
            Amount = request.Amount,
            PaidOn = paidOn,
            Method = request.Method,
            ExternalId = request.ExternalId?.Trim(),
            Notes = request.Notes?.Trim(),
            RegisteredByPersonId = registeredByPersonId,
        };

        var entry = new LedgerEntry
        {
            CondominiumId = charge.CondominiumId,
            BankAccountId = request.BankAccountId,
            LedgerAccountId = revenueAccountId,
            Direction = EntryDirection.In,
            Amount = request.Amount,
            Date = paidOn,
            Competence = charge.Competence,
            Description = $"Recebimento de cobrança - {charge.Competence}",
            PaymentId = payment.Id,
            CreatedByPersonId = registeredByPersonId,
        };

        payment.LedgerEntryId = entry.Id;

        db.Payments.Add(payment);
        db.LedgerEntries.Add(entry);

        charge.PaidAmount += request.Amount;

        // Tolerancia de um centavo: diferenca de arredondamento no PIX nao
        // deve deixar a cobranca eternamente "parcialmente paga".
        if (charge.PaidAmount >= charge.TotalAmount - 0.01m)
        {
            charge.Status = ChargeStatus.Paid;
            charge.PaidOn = paidOn;
        }
        else
        {
            charge.Status = ChargeStatus.PartiallyPaid;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetChargeAsync(charge.Id, cancellationToken);
    }

    /// <summary>Cria uma cobranca avulsa para uma unidade, fora do rateio mensal.</summary>
    public async Task<ChargeDto> CreateExtraChargeAsync(
        CreateExtraChargeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DomainException.ThrowIf(request.Amount <= 0, "O valor deve ser maior que zero.");
        DomainException.ThrowIf(string.IsNullOrWhiteSpace(request.Description), "Descreva a cobrança.");

        Unit unit = await db.Units
            .FirstOrDefaultAsync(u => u.Id == request.UnitId, cancellationToken)
            ?? throw new KeyNotFoundException("Unidade não encontrada.");

        Guid? payerId = await FindBillingResponsibleAsync(unit.Id, cancellationToken);
        Condominium condominium = await LoadCondominiumAsync(cancellationToken);

        Competence competence = request.Competence is { Length: > 0 } text
            ? Competence.Parse(text)
            : Competence.From(request.DueDate);

        var charge = new Charge
        {
            UnitId = unit.Id,
            PayerPersonId = payerId,
            Competence = competence,
            DueDate = request.DueDate,
            TotalAmount = request.Amount,
            PublicToken = GeneratePublicToken(),
        };

        charge.Items.Add(new ChargeItem
        {
            Kind = request.Kind,
            Description = request.Description.Trim(),
            Amount = request.Amount,
            Sort = 1,
        });

        db.Charges.Add(charge);
        await db.SaveChangesAsync(cancellationToken);

        // O PIX so pode ser montado depois do SaveChanges: o txid deriva do Id.
        charge.PixPayload = BuildPixPayload(condominium, charge, charge.TotalAmount);
        await db.SaveChangesAsync(cancellationToken);

        return await GetChargeAsync(charge.Id, cancellationToken);
    }

    public async Task<ChargeDto> CancelChargeAsync(
        Guid chargeId,
        CancellationToken cancellationToken = default)
    {
        Charge charge = await db.Charges.FirstOrDefaultAsync(c => c.Id == chargeId, cancellationToken)
            ?? throw new KeyNotFoundException("Cobrança não encontrada.");

        DomainException.ThrowIf(
            charge.PaidAmount > 0,
            "Cobrança com pagamento registrado não pode ser cancelada. Estorne o pagamento antes.");

        charge.Status = ChargeStatus.Cancelled;
        await db.SaveChangesAsync(cancellationToken);

        return await GetChargeAsync(charge.Id, cancellationToken);
    }

    /// <summary>Unidades inadimplentes, com multa e juros ja apurados.</summary>
    public async Task<IReadOnlyList<DelinquentUnit>> GetDelinquencyAsync(
        CancellationToken cancellationToken = default)
    {
        DateOnly today = clock.Today;
        Condominium condominium = await LoadCondominiumAsync(cancellationToken);

        var charges = await db.Charges
            .AsNoTracking()
            .Include(c => c.Unit).ThenInclude(u => u.Block)
            .Include(c => c.Payer)
            .Where(c => c.Status != ChargeStatus.Paid && c.Status != ChargeStatus.Cancelled)
            .Where(c => c.DueDate < today)
            .ToListAsync(cancellationToken);

        return charges
            .GroupBy(c => c.UnitId)
            .Select(group =>
            {
                var first = group.First();

                decimal outstanding = group.Sum(c => c.OutstandingAmount);
                decimal lateCharges = group.Sum(c => LateChargeCalculator.Compute(
                    c.OutstandingAmount,
                    c.DueDate,
                    today,
                    condominium.Billing.LateFeeRate,
                    condominium.Billing.MonthlyInterestRate).Total);

                DateOnly oldest = group.Min(c => c.DueDate);

                return new DelinquentUnit(
                    group.Key,
                    first.Unit.FullIdentifier,
                    first.Payer?.Name,
                    first.Payer?.Email,
                    group.Count(),
                    outstanding,
                    lateCharges,
                    oldest,
                    today.DayNumber - oldest.DayNumber);
            })
            .OrderByDescending(u => u.OutstandingAmount)
            .ToList();
    }

    /// <summary>Monta o modelo do boleto de uma cobranca, para gerar o PDF.</summary>
    /// <summary>
    /// Monta o boleto de uma cobrança.
    /// </summary>
    /// <param name="chargeId">A cobrança.</param>
    /// <param name="onlyForPersonId">
    /// Quando preenchido, só devolve se a cobrança for desta pessoa. É o caso
    /// do morador baixando o próprio boleto; síndico e conselho passam nulo.
    /// </param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    /// <remarks>
    /// Cobrança de outra pessoa responde "não encontrada", e não "sem
    /// permissão", de propósito: a diferença entre as duas respostas conta a
    /// quem perguntou que aquele identificador existe.
    /// </remarks>
    public async Task<ChargeDocument> BuildDocumentAsync(
        Guid chargeId,
        Guid? onlyForPersonId = null,
        CancellationToken cancellationToken = default)
    {
        Charge charge = await LoadChargeGraph().FirstOrDefaultAsync(c => c.Id == chargeId, cancellationToken)
            ?? throw new KeyNotFoundException("Cobrança não encontrada.");

        if (onlyForPersonId is { } personId
            && !await IsChargeOfPersonAsync(charge, personId, cancellationToken))
        {
            throw new KeyNotFoundException("Cobrança não encontrada.");
        }

        return await BuildDocumentAsync(charge, cancellationToken);
    }

    /// <summary>
    /// A cobrança é desta pessoa? Mesma regra da lista "Minhas cobranças":
    /// vale ser o pagador ou ocupar a unidade hoje.
    /// </summary>
    private async Task<bool> IsChargeOfPersonAsync(
        Charge charge,
        Guid personId,
        CancellationToken cancellationToken)
    {
        if (charge.PayerPersonId == personId)
        {
            return true;
        }

        return await db.UnitOccupancies
            .Where(o => o.PersonId == personId && o.UnitId == charge.UnitId)
            .Where(o => o.EndedOn == null || o.EndedOn >= clock.Today)
            .AnyAsync(cancellationToken);
    }

    /// <summary>Mesmo modelo, acessado pelo link publico do e-mail.</summary>
    public async Task<ChargeDocument> BuildDocumentByTokenAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        DomainException.ThrowIf(string.IsNullOrWhiteSpace(token), "Link inválido.");

        Charge charge = await LoadChargeGraph()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.PublicToken == token, cancellationToken)
            ?? throw new KeyNotFoundException("Cobrança não encontrada ou link expirado.");

        return await BuildDocumentAsync(charge, cancellationToken);
    }

    private async Task<ChargeDocument> BuildDocumentAsync(Charge charge, CancellationToken cancellationToken)
    {
        Condominium condominium = await db.Condominiums
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstAsync(c => c.Id == charge.CondominiumId, cancellationToken);

        string? notes = charge.BillingCycleId is { } cycleId
            ? await db.BillingCycles
                .IgnoreQueryFilters()
                .Where(c => c.Id == cycleId)
                .Select(c => c.Notes)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        ChargeDto dto = ToDto(charge, condominium);

        return new ChargeDocument
        {
            CondominiumName = condominium.Name,
            CondominiumCnpj = condominium.Cnpj,
            CondominiumAddress = condominium.Address.ToString(),
            UnitIdentifier = dto.UnitIdentifier,
            PayerName = dto.PayerName,
            Competence = dto.Competence,
            DueDate = dto.DueDate,
            TotalAmount = dto.TotalAmount,
            PaidAmount = dto.PaidAmount,
            LateFee = dto.LateFee,
            Interest = dto.Interest,
            TotalDue = dto.Status == ChargeStatus.Paid ? 0m : dto.TotalWithLateCharges,
            DaysLate = dto.DaysLate,
            Items = dto.Items,
            PixPayload = dto.PixPayload,
            PublicUrl = options.Value.BuildChargeUrl(charge.PublicToken),
            Notes = notes,
            IsPaid = dto.Status == ChargeStatus.Paid,
        };
    }

    // --- Apoio ---

    private sealed record BillableUnit(
        Guid UnitId,
        string Identifier,
        decimal IdealFraction,
        decimal? AreaM2,
        Guid? PayerPersonId,
        string? PayerName,
        string? PayerEmail);

    private async Task<List<BillableUnit>> LoadBillableUnitsAsync(CancellationToken cancellationToken)
    {
        DateOnly today = clock.Today;

        var units = await db.Units
            .AsNoTracking()
            .Include(u => u.Block)
            .Where(u => u.IsActive)
            .OrderBy(u => u.Block!.Name)
            .ThenBy(u => u.Identifier)
            .ToListAsync(cancellationToken);

        var unitIds = units.Select(u => u.Id).ToList();

        // Responsavel pela cobranca de cada unidade: a ocupacao ativa marcada
        // como responsavel, ou o proprietario quando ninguem foi marcado.
        var responsibles = await db.UnitOccupancies
            .AsNoTracking()
            .Include(o => o.Person)
            .Where(o => unitIds.Contains(o.UnitId))
            .Where(o => o.EndedOn == null || o.EndedOn >= today)
            .ToListAsync(cancellationToken);

        var byUnit = responsibles
            .GroupBy(o => o.UnitId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(o => o.IsBillingResponsible)
                      .ThenBy(o => o.Relation)
                      .First());

        return units
            .Select(u =>
            {
                byUnit.TryGetValue(u.Id, out UnitOccupancy? occupancy);

                return new BillableUnit(
                    u.Id,
                    u.FullIdentifier,
                    u.IdealFraction,
                    u.AreaM2,
                    occupancy?.PersonId,
                    occupancy?.Person.Name,
                    occupancy?.Person.Email);
            })
            .ToList();
    }

    /// <summary>
    /// Distribui rateio e fundo de reserva pelas unidades. Cada valor e
    /// distribuido separadamente para que a soma de cada linha feche exata.
    /// </summary>
    /// <remarks>
    /// O consumo medido entra por fora do rateio: ele nao e dividido por
    /// fracao ideal, e somado ao total de cada unidade depois. Por isso chega
    /// aqui como um dicionario por unidade, e nao como um total a distribuir.
    /// </remarks>
    /// <summary>A cultura em que os valores do boleto sao lidos.</summary>
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private static List<ApportionmentPreviewLine> BuildShares(
        decimal apportionableTotal,
        decimal reserveFundTotal,
        List<BillableUnit> units,
        ApportionmentMethod method,
        IReadOnlyDictionary<Guid, MeterReading> metered)
    {
        var apportionmentUnits = units
            .Select(u => new ApportionmentUnit(u.UnitId, u.IdealFraction, u.AreaM2))
            .ToList();

        var condoFees = ApportionmentCalculator
            .Distribute(apportionableTotal, apportionmentUnits, method)
            .ToDictionary(s => s.UnitId, s => s.Amount);

        var reserveFunds = reserveFundTotal > 0
            ? ApportionmentCalculator
                .Distribute(reserveFundTotal, apportionmentUnits, method)
                .ToDictionary(s => s.UnitId, s => s.Amount)
            : [];

        return units
            .Select(u =>
            {
                decimal condoFee = condoFees.GetValueOrDefault(u.UnitId);
                decimal reserveFund = reserveFunds.GetValueOrDefault(u.UnitId);

                MeterReading? leitura = metered.GetValueOrDefault(u.UnitId);
                decimal consumo = leitura?.Amount ?? 0m;

                return new ApportionmentPreviewLine(
                    u.UnitId,
                    u.Identifier,
                    u.IdealFraction,
                    condoFee,
                    reserveFund,
                    condoFee + reserveFund + consumo,
                    u.PayerPersonId,
                    u.PayerName,
                    u.PayerEmail,
                    consumo,
                    leitura?.Consumption ?? 0m);
            })
            .ToList();
    }

    /// <summary>
    /// As leituras da competencia, por unidade. Fora do rateio de proposito:
    /// consumo individual nao se divide, se cobra de quem gastou.
    /// </summary>
    private async Task<Dictionary<Guid, MeterReading>> LoadMeteredAsync(
        Competence competence,
        CancellationToken cancellationToken)
        => await db.MeterReadings
            .AsNoTracking()
            .Where(r => r.Competence == competence)
            .ToDictionaryAsync(r => r.UnitId, cancellationToken);

    private async Task<Condominium> LoadCondominiumAsync(CancellationToken cancellationToken)
        => await db.Condominiums.AsNoTracking().FirstOrDefaultAsync(cancellationToken)
            ?? throw new DomainException("Nenhum condomínio ativo no contexto da requisição.");

    private async Task<Guid?> FindAccountIdAsync(string code, CancellationToken cancellationToken)
        => await db.LedgerAccounts
            .Where(a => a.Code == code)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<Guid?> FindBillingResponsibleAsync(Guid unitId, CancellationToken cancellationToken)
    {
        DateOnly today = clock.Today;

        return await db.UnitOccupancies
            .Where(o => o.UnitId == unitId)
            .Where(o => o.EndedOn == null || o.EndedOn >= today)
            .OrderByDescending(o => o.IsBillingResponsible)
            .ThenBy(o => o.Relation)
            .Select(o => (Guid?)o.PersonId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Monta o copia-e-cola do PIX da cobranca.
    /// </summary>
    /// <remarks>
    /// O txid vem do Id da cobranca, entao o PIX recebido no extrato pode ser
    /// ligado de volta a quem pagou. Sem chave PIX cadastrada devolve nulo:
    /// o condominio ainda pode cobrar por transferencia ou boleto do banco.
    /// </remarks>
    private static string? BuildPixPayload(Condominium condominium, Charge charge, decimal amount)
    {
        if (string.IsNullOrWhiteSpace(condominium.PixKey))
        {
            return null;
        }

        return BrCodeBuilder.Build(new PixCharge
        {
            Key = condominium.PixKey,
            ReceiverName = condominium.PixReceiverName ?? condominium.Name,
            ReceiverCity = condominium.PixReceiverCity ?? condominium.Address.City,
            Amount = amount,
            TransactionId = charge.Id.ToString("N")[..25],
            Description = $"Cond {charge.Competence}",
        });
    }

    private IQueryable<Charge> LoadChargeGraph() => db.Charges
        .AsNoTracking()
        .Include(c => c.Unit).ThenInclude(u => u.Block)
        .Include(c => c.Items.OrderBy(i => i.Sort))
        .Include(c => c.Payer);

    private ChargeDto ToDto(Charge charge, Condominium condominium)
    {
        LateCharge late = charge.Status is ChargeStatus.Paid or ChargeStatus.Cancelled
            ? LateCharge.None
            : LateChargeCalculator.Compute(
                charge.OutstandingAmount,
                charge.DueDate,
                clock.Today,
                condominium.Billing.LateFeeRate,
                condominium.Billing.MonthlyInterestRate);

        decimal totalWithLate = charge.OutstandingAmount + late.Total;

        // Vencida: o PIX guardado tem o valor de face, entao regenera com
        // multa e juros para o morador pagar o valor certo hoje.
        string? pix = late.Total > 0
            ? BuildPixPayload(condominium, charge, totalWithLate)
            : charge.PixPayload;

        return new ChargeDto(
            charge.Id,
            charge.UnitId,
            charge.Unit?.FullIdentifier ?? string.Empty,
            charge.Competence.ToString(),
            charge.DueDate,
            charge.TotalAmount,
            charge.PaidAmount,
            charge.OutstandingAmount,
            ResolveStatus(charge),
            charge.PaidOn,
            charge.PayerPersonId,
            charge.Payer?.Name,
            charge.Payer?.Email,
            charge.Payer?.Phone,
            pix,
            charge.PublicToken,
            late.DaysLate,
            late.Fine,
            late.Interest,
            totalWithLate,
            charge.Items
                .OrderBy(i => i.Sort)
                .Select(i => new ChargeItemDto(i.Id, i.Kind, i.Description, i.Amount))
                .ToList());
    }

    /// <summary>
    /// "Vencida" e derivado da data, nao gravado: se fosse coluna, dependeria
    /// de uma rotina noturna rodar para ficar correto.
    /// </summary>
    private ChargeStatus ResolveStatus(Charge charge) =>
        charge.Status is ChargeStatus.Open or ChargeStatus.PartiallyPaid && charge.DueDate < clock.Today
            ? ChargeStatus.Overdue
            : charge.Status;

    private static IQueryable<Charge> ApplyFilter(IQueryable<Charge> query, ChargeFilter filter, DateOnly today)
    {
        if (filter.Competence is { Length: > 0 } text && Competence.TryParse(text, out Competence competence))
        {
            query = query.Where(c => c.Competence == competence);
        }

        if (filter.UnitId is { } unitId)
        {
            query = query.Where(c => c.UnitId == unitId);
        }

        if (filter.BillingCycleId is { } cycleId)
        {
            query = query.Where(c => c.BillingCycleId == cycleId);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(c => c.Status == status);
        }

        if (filter.OnlyOverdue == true)
        {
            query = query.Where(c =>
                c.DueDate < today &&
                c.Status != ChargeStatus.Paid &&
                c.Status != ChargeStatus.Cancelled);
        }

        return query;
    }

    private static DateOnly DefaultDueDate(Competence competence, int dueDay)
    {
        // A taxa de uma competencia vence no mes seguinte. O dia e limitado ao
        // ultimo dia do mes, para "dia 31" nao estourar em fevereiro.
        Competence next = competence.Next();
        int day = Math.Min(dueDay, DateTime.DaysInMonth(next.Year, next.Month));

        return new DateOnly(next.Year, next.Month, day);
    }

    private static string GeneratePublicToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    private static System.Linq.Expressions.Expression<Func<BillingCycle, BillingCycleDto>> ToCycleDto() =>
        c => new BillingCycleDto(
            c.Id,
            c.Competence.ToString(),
            c.DueDate,
            c.Status,
            c.Method,
            c.ApportionableTotal,
            c.ReserveFundRate,
            c.ReserveFundTotal,
            c.ChargedTotal,
            c.Charges.Sum(ch => ch.PaidAmount),
            c.Charges.Count,
            c.Charges.Count(ch => ch.Status == ChargeStatus.Paid),
            c.Notes,
            c.ClosedAt,
            c.PublishedAt);
}
