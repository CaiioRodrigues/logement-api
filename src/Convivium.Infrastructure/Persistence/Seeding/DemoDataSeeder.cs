namespace Convivium.Infrastructure.Persistence.Seeding;

using System.Globalization;
using System.Text;
using Convivium.Application.Abstractions;
using Convivium.Domain.Billing;
using Convivium.Domain.Common;
using Convivium.Domain.Condominiums;
using Convivium.Domain.Expenses;
using Convivium.Domain.Finance;
using Convivium.Domain.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Popula o banco com um condominio de demonstracao completo, para que o
/// convivium-web tenha graficos e listas com conteudo desde o primeiro run.
/// </summary>
/// <remarks>
/// So roda em Development e so quando o banco esta vazio.
/// </remarks>
public sealed class DemoDataSeeder(
    ConviviumDbContext db,
    IPasswordHasher passwordHasher,
    IClock clock,
    ILogger<DemoDataSeeder> logger)
{
    public const string DemoPassword = "Convivium@123";

    /// <summary>Semente fixa: o demo sai igual em toda maquina, o que ajuda a comparar bugs.</summary>
    private readonly Random _random = new(20260914);

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        // Confere o condominio de demonstracao pelo nome, e nao se existe algum
        // condominio: rodando sob demanda pelo hub, quem ja tem o predio de
        // verdade cadastrado continua podendo gerar o de mentira ao lado.
        bool jaExiste = await db.Condominiums
            .IgnoreQueryFilters()
            .AnyAsync(c => c.Name == DemoName, cancellationToken);

        if (jaExiste)
        {
            logger.LogInformation("Condominio de demonstracao ja existe; seed ignorado.");
            return;
        }

        logger.LogInformation("Populando condominio de demonstracao...");

        Condominium condominium = CreateCondominium();

        // O CNPJ e unico no banco. Se um condominio de verdade ja usa este
        // numero ficticio, a demonstracao abre mao dele em vez de derrubar a
        // requisicao: e dado de mentira, e o resto do cenario continua valendo.
        bool cnpjEmUso = await db.Condominiums
            .IgnoreQueryFilters()
            .AnyAsync(c => c.Cnpj == condominium.Cnpj, cancellationToken);

        if (cnpjEmUso)
        {
            logger.LogInformation(
                "CNPJ {Cnpj} ja esta em uso; a demonstracao sera criada sem CNPJ.",
                condominium.Cnpj);

            condominium.Cnpj = null;
        }

        db.Condominiums.Add(condominium);

        var accounts = ChartOfAccountsTemplate.BuildFor(condominium.Id);
        db.LedgerAccounts.AddRange(accounts);
        var byCode = accounts.ToDictionary(a => a.Code, StringComparer.Ordinal);

        var (checking, reserve) = CreateBankAccounts(condominium.Id);
        db.BankAccounts.AddRange(checking, reserve);

        var units = CreateBlocksAndUnits(condominium.Id);
        var people = CreatePeople(condominium.Id, units);

        var suppliers = CreateSuppliers(condominium.Id);
        db.Suppliers.AddRange(suppliers);

        SeedFinancialHistory(condominium, byCode, checking, reserve, suppliers);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Demo pronto: {Condominium}, {Units} unidades, {People} pessoas. Login: {Email} / {Password}",
            condominium.Name, units.Count, people, "sindico@convivium.local", DemoPassword);
    }

    /// <summary>Nome do condominio de demonstracao. E por ele que o seed sabe se ja rodou.</summary>
    public const string DemoName = "Residencial Modelo";

    private static Condominium CreateCondominium() => new()
    {
        Name = DemoName,
        LegalName = "Condomínio do Edifício Residencial Modelo",
        Cnpj = "12345678000195",
        Address = new Address
        {
            Street = "Rua dos Timbiras",
            Number = "1420",
            District = "Lourdes",
            City = "Belo Horizonte",
            State = "MG",
            ZipCode = "30140061",
        },
        Billing = new BillingSettings
        {
            DueDay = 10,
            ReserveFundRate = 0.10m,
            LateFeeRate = 0.02m,
            MonthlyInterestRate = 0.01m,
            DefaultApportionmentMethod = ApportionmentMethod.IdealFraction,
        },
        PixKey = "12345678000195",
        PixKeyType = Domain.Condominiums.PixKeyType.Cnpj,
        PixReceiverName = "COND RESID CONVIVIUM",
        PixReceiverCity = "BELO HORIZONTE",
    };

    private static (BankAccount Checking, BankAccount Reserve) CreateBankAccounts(Guid condominiumId)
    {
        var checking = new BankAccount
        {
            CondominiumId = condominiumId,
            Name = "Conta Corrente",
            Kind = BankAccountKind.Checking,
            BankCode = "341",
            Agency = "1234",
            AccountNumber = "56789-0",
            OpeningBalance = 18_500.00m,
            OpeningDate = new DateOnly(2026, 1, 1),
        };

        var reserve = new BankAccount
        {
            CondominiumId = condominiumId,
            Name = "Fundo de Reserva",
            Kind = BankAccountKind.Investment,
            BankCode = "341",
            Agency = "1234",
            AccountNumber = "56789-1",
            OpeningBalance = 47_200.00m,
            OpeningDate = new DateOnly(2026, 1, 1),
            IsReserveFund = true,
        };

        return (checking, reserve);
    }

    /// <summary>
    /// Dois blocos de 12 unidades. A fracao ideal sai da area privativa e a
    /// ultima unidade absorve a diferenca de arredondamento, para que a soma
    /// feche exatamente em 1 — como exige a convencao de condominio.
    /// </summary>
    private List<Unit> CreateBlocksAndUnits(Guid condominiumId)
    {
        var blocks = new[] { "Bloco A", "Bloco B" }
            .Select(name => new Block { CondominiumId = condominiumId, Name = name })
            .ToList();

        db.Blocks.AddRange(blocks);

        var units = new List<Unit>();

        foreach (Block block in blocks)
        {
            for (int floor = 1; floor <= 3; floor++)
            {
                for (int position = 1; position <= 4; position++)
                {
                    // As pontas (01 e 04) sao as maiores; as do meio, menores.
                    decimal area = position is 1 or 4 ? 68.40m : 54.20m;

                    units.Add(new Unit
                    {
                        CondominiumId = condominiumId,
                        Block = block,
                        Identifier = $"{floor}0{position}",
                        Floor = floor,
                        Kind = UnitKind.Apartment,
                        AreaM2 = area,
                    });
                }
            }
        }

        decimal totalArea = units.Sum(u => u.AreaM2!.Value);
        decimal accumulated = 0m;

        for (int i = 0; i < units.Count; i++)
        {
            if (i == units.Count - 1)
            {
                units[i].IdealFraction = 1m - accumulated;
                break;
            }

            decimal fraction = Math.Round(units[i].AreaM2!.Value / totalArea, 8, MidpointRounding.ToZero);
            units[i].IdealFraction = fraction;
            accumulated += fraction;
        }

        db.Units.AddRange(units);
        return units;
    }

    private int CreatePeople(Guid condominiumId, List<Unit> units)
    {
        string hash = passwordHasher.Hash(DemoPassword);

        var staff = new (string Name, string Email, MembershipRole Role)[]
        {
            ("Helena Prado", "sindico@convivium.local", MembershipRole.Manager),
            ("Rogério Tavares", "conselho@convivium.local", MembershipRole.CouncilMember),
            ("Marcos Vinícius Alves", "zelador@convivium.local", MembershipRole.Caretaker),
        };

        foreach ((string name, string email, MembershipRole role) in staff)
        {
            var person = new Person { Name = name, Email = email, PasswordHash = hash };
            db.People.Add(person);
            db.Memberships.Add(new Membership
            {
                CondominiumId = condominiumId,
                Person = person,
                Role = role,
                StartedOn = new DateOnly(2026, 1, 1),
            });
        }

        string[] firstNames =
        [
            "Ana", "Bruno", "Carla", "Diego", "Eduarda", "Felipe", "Gabriela", "Henrique",
            "Isabela", "João", "Larissa", "Mateus", "Natália", "Otávio", "Patrícia", "Rafael",
            "Sabrina", "Thiago", "Vanessa", "Wagner", "Yasmin", "André", "Beatriz", "Caio",
        ];

        string[] lastNames =
        [
            "Almeida", "Barbosa", "Carvalho", "Dias", "Esteves", "Ferreira", "Gomes", "Henriques",
            "Iglesias", "Justino", "Kunz", "Lima", "Moreira", "Nunes", "Oliveira", "Pereira",
            "Queiroz", "Ribeiro", "Santos", "Teixeira", "Uchôa", "Vieira", "Werneck", "Xavier",
        ];

        int created = staff.Length;

        for (int i = 0; i < units.Count; i++)
        {
            Unit unit = units[i];
            string name = $"{firstNames[i % firstNames.Length]} {lastNames[i % lastNames.Length]}";
            string slug = ParaSlugDeEmail(name);

            // O primeiro morador tem senha para servir de login de teste do papel Resident.
            bool canSignIn = i == 0;

            var person = new Person
            {
                Name = name,
                Email = canSignIn ? "morador@convivium.local" : $"{slug}@exemplo.local",
                Phone = $"3199{_random.Next(1000000, 9999999)}",
                PasswordHash = canSignIn ? hash : null,
            };

            db.People.Add(person);

            db.Memberships.Add(new Membership
            {
                CondominiumId = condominiumId,
                Person = person,
                Role = MembershipRole.Resident,
                StartedOn = new DateOnly(2026, 1, 1),
            });

            db.UnitOccupancies.Add(new UnitOccupancy
            {
                CondominiumId = condominiumId,
                Unit = unit,
                Person = person,
                Relation = OccupancyRelation.Owner,
                IsBillingResponsible = true,
                StartedOn = new DateOnly(2026, 1, 1),
            });

            created++;
        }

        return created;
    }

    /// <summary>
    /// "João Henriques" -> "joao.henriques". Remove a acentuação porque um
    /// e-mail com "ã" e "ç" nem sempre e aceito pelos servidores de destino.
    /// </summary>
    private static string ParaSlugDeEmail(string name)
    {
        string semAcento = string.Concat(
            name.Normalize(NormalizationForm.FormD)
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark));

        return semAcento.Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace(' ', '.');
    }

    private static List<Supplier> CreateSuppliers(Guid condominiumId) =>
    [
        new() { CondominiumId = condominiumId, Name = "CEMIG Distribuição S.A.", Document = "06981180000116" },
        new() { CondominiumId = condominiumId, Name = "COPASA MG", Document = "17281106000103" },
        new() { CondominiumId = condominiumId, Name = "Atlas Elevadores Ltda", Document = "11222333000181" },
        new() { CondominiumId = condominiumId, Name = "Brilho Serviços de Limpeza ME", Document = "22333444000181" },
        new() { CondominiumId = condominiumId, Name = "Predial Administradora", Document = "33444555000181" },
        new() { CondominiumId = condominiumId, Name = "Verde Vivo Jardinagem", Document = "44555666000181" },
        new() { CondominiumId = condominiumId, Name = "Seguradora Horizonte", Document = "55666777000181" },
    ];

    /// <summary>
    /// Gera seis meses de despesas pagas e as receitas correspondentes, para que
    /// os graficos de evolucao e de gasto por categoria tenham serie historica.
    /// </summary>
    private void SeedFinancialHistory(
        Condominium condominium,
        Dictionary<string, LedgerAccount> accounts,
        BankAccount checking,
        BankAccount reserve,
        List<Supplier> suppliers)
    {
        Supplier cemig = suppliers[0];
        Supplier copasa = suppliers[1];
        Supplier elevator = suppliers[2];
        Supplier cleaning = suppliers[3];
        Supplier admin = suppliers[4];
        Supplier garden = suppliers[5];
        Supplier insurer = suppliers[6];

        // (conta, descricao, fornecedor, valor base) — o valor varia por mes.
        var recurring = new (string Code, string Description, Supplier? Supplier, decimal Base)[]
        {
            ("5.1.01", "Folha de pagamento - porteiros e zelador", null, 9_800m),
            ("5.1.02", "INSS e FGTS sobre a folha", null, 3_430m),
            ("5.1.03", "Vale transporte e vale alimentação", null, 1_260m),
            ("5.2.01", "Energia elétrica das áreas comuns", cemig, 2_380m),
            ("5.2.02", "Água e esgoto", copasa, 2_010m),
            ("5.3.01", "Manutenção preventiva dos elevadores", elevator, 890m),
            ("5.3.03", "Jardinagem quinzenal", garden, 350m),
            ("5.4.02", "Limpeza e conservação", cleaning, 2_400m),
            ("5.4.03", "Taxa de administração", admin, 780m),
            ("5.5", "Material de limpeza e consumo", null, 420m),
            ("5.6.01", "Seguro predial obrigatório", insurer, 312m),
        };

        Competence current = Competence.From(clock.Today);

        for (int monthsAgo = 5; monthsAgo >= 0; monthsAgo--)
        {
            Competence competence = current.AddMonths(-monthsAgo);

            // O mes corrente ainda esta em aberto: as contas existem, mas nao foram pagas.
            bool closed = monthsAgo > 0;
            decimal monthTotal = 0m;

            foreach ((string code, string description, Supplier? supplier, decimal baseAmount) in recurring)
            {
                // Variacao de ate 12% para cima ou para baixo, para o grafico nao sair reto.
                decimal variation = 1m + ((_random.Next(-120, 121)) / 1000m);
                decimal amount = Math.Round(baseAmount * variation, 2);
                monthTotal += amount;

                LedgerAccount account = accounts[code];
                var dueDate = new DateOnly(competence.Year, competence.Month, 15);

                var expense = new Expense
                {
                    CondominiumId = condominium.Id,
                    Supplier = supplier,
                    LedgerAccount = account,
                    Description = description,
                    Competence = competence,
                    DueDate = dueDate,
                    Amount = amount,
                    IsApportionable = account.IsApportionable,
                    Status = closed ? ExpenseStatus.Paid : ExpenseStatus.Pending,
                    PaidOn = closed ? dueDate : null,
                };

                db.Expenses.Add(expense);

                if (closed)
                {
                    var entry = new LedgerEntry
                    {
                        CondominiumId = condominium.Id,
                        BankAccount = checking,
                        LedgerAccount = account,
                        Direction = EntryDirection.Out,
                        Amount = amount,
                        Date = dueDate,
                        Competence = competence,
                        Description = description,
                        ExpenseId = expense.Id,
                        ReconciledAt = clock.Now,
                    };

                    db.LedgerEntries.Add(entry);
                    expense.LedgerEntryId = entry.Id;
                }
            }

            if (!closed)
            {
                continue;
            }

            // Receita: a arrecadacao cobre as despesas com uma folga pequena,
            // e o fundo de reserva entra na conta separada.
            var receiptDate = new DateOnly(competence.Year, competence.Month, 10);
            decimal condoFee = Math.Round(monthTotal * 1.03m, 2);
            decimal reserveFund = Math.Round(condoFee * condominium.Billing.ReserveFundRate, 2);

            db.LedgerEntries.Add(new LedgerEntry
            {
                CondominiumId = condominium.Id,
                BankAccount = checking,
                LedgerAccount = accounts["4.1"],
                Direction = EntryDirection.In,
                Amount = condoFee,
                Date = receiptDate,
                Competence = competence,
                Description = $"Arrecadação de taxa condominial - {competence}",
                ReconciledAt = clock.Now,
            });

            db.LedgerEntries.Add(new LedgerEntry
            {
                CondominiumId = condominium.Id,
                BankAccount = reserve,
                LedgerAccount = accounts["4.2"],
                Direction = EntryDirection.In,
                Amount = reserveFund,
                Date = receiptDate,
                Competence = competence,
                Description = $"Fundo de reserva - {competence}",
                ReconciledAt = clock.Now,
            });
        }
    }
}
