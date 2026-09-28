namespace Convivium.Application.Accountability;

/// <summary>
/// Balancete de um mes: de onde veio e para onde foi o dinheiro.
/// </summary>
/// <remarks>
/// Regime de caixa, pela data em que o dinheiro se moveu — e nao pela
/// competencia contabil do lancamento. E o unico jeito de a conta fechar
/// contra o extrato bancario, que e o documento que o conselho confere. Uma
/// conta de agosto paga em setembro aparece em setembro, e e assim que o
/// sindico explica na assembleia.
/// </remarks>
public sealed record MonthlyStatement(
    string Competence,
    DateOnly From,
    DateOnly To,
    string CondominiumName,
    string? Cnpj,
    string Address,

    /// <summary>Saldo somado de todas as contas no dia anterior ao periodo.</summary>
    decimal OpeningBalance,
    decimal TotalIncome,
    decimal TotalExpense,

    /// <summary>Receitas menos despesas. Negativo significa mes no vermelho.</summary>
    decimal Result,
    decimal ClosingBalance,

    IReadOnlyList<StatementLine> Income,
    IReadOnlyList<StatementLine> Expenses,
    IReadOnlyList<StatementAccountBalance> Accounts,
    IReadOnlyList<StatementEntry> Entries,

    /// <summary>Contas do condominio ainda nao pagas no fim do periodo.</summary>
    IReadOnlyList<PendingItem> Payables,

    /// <summary>Cobrancas ainda em aberto no fim do periodo.</summary>
    IReadOnlyList<PendingItem> Receivables,

    /// <summary>Saldo das contas carimbadas como fundo de reserva.</summary>
    ReserveFundBalance ReserveFund,

    DelinquencySummary Delinquency,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Uma pendencia no fim do periodo: conta a pagar ou cobranca a receber.
/// </summary>
/// <remarks>
/// O saldo sozinho engana. Ter R$ 16 mil em caixa com R$ 20 mil vencendo e
/// uma situacao bem diferente de ter os mesmos R$ 16 mil sem dever nada, e o
/// balancete precisa dizer qual das duas e.
/// </remarks>
public sealed record PendingItem(
    string Description,

    /// <summary>Fornecedor, na conta a pagar; unidade, na conta a receber.</summary>
    string? Counterpart,
    DateOnly DueDate,
    decimal Amount,

    /// <summary>Dias de atraso no fim do periodo. Zero quando ainda nao venceu.</summary>
    int DaysLate);

/// <summary>
/// O fundo de reserva em destaque, e nao diluido entre as contas bancarias.
/// </summary>
/// <remarks>
/// E a primeira pergunta de qualquer assembleia, e a lei trata este dinheiro
/// como separado: fundo de reserva tem destinacao propria, definida em
/// convencao, e nao se confunde com o caixa de operacao.
/// </remarks>
public sealed record ReserveFundBalance(
    decimal Opening,
    decimal In,
    decimal Out,
    decimal Closing,

    /// <summary>Fatia do saldo total do condominio que esta no fundo, de 0 a 1.</summary>
    decimal ShareOfTotal);

/// <summary>Quanto entrou ou saiu por conta do plano de contas.</summary>
public sealed record StatementLine(
    string Code,
    string Name,
    decimal Amount,
    int Count,

    /// <summary>Fatia do total de receitas ou de despesas, de 0 a 1.</summary>
    decimal Share);

/// <summary>Movimento e saldo de uma conta bancaria no periodo.</summary>
public sealed record StatementAccountBalance(
    string Name,
    bool IsReserveFund,
    decimal Opening,
    decimal In,
    decimal Out,
    decimal Closing);

/// <summary>Um lancamento do periodo, para o anexo do movimento.</summary>
public sealed record StatementEntry(
    DateOnly Date,
    string Description,
    string AccountCode,
    string AccountName,
    string BankAccountName,
    string? DocumentNumber,
    bool IsIncome,
    decimal Amount,
    bool Reconciled);

/// <summary>O que ficou em aberto no fim do periodo.</summary>
public sealed record DelinquencySummary(
    int Units,
    decimal Outstanding,
    decimal LateCharges);

/// <summary>Gera o balancete mensal em PDF.</summary>
public interface IStatementRenderer
{
    byte[] Render(MonthlyStatement statement);
}
