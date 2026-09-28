namespace Convivium.Infrastructure.Documents;

using System.Globalization;
using Convivium.Application.Accountability;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

/// <summary>
/// Desenha o balancete mensal em PDF, no formato que se leva para a assembleia.
/// </summary>
/// <remarks>
/// A ordem das secoes segue a pergunta que o condomino faz, nessa ordem:
/// quanto tinha, quanto entrou, quanto saiu, quanto sobrou, onde esta, e quem
/// nao pagou. O movimento detalhado vem por ultimo, como anexo: quem quer
/// conferir lancamento por lancamento vira a pagina, quem nao quer le so a
/// primeira.
/// </remarks>
public sealed class StatementPdfRenderer : IStatementRenderer
{
    private static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");

    private const string BodyFont = "Lato";

    private const string Ink = "#1a1a1a";
    private const string Muted = "#6b7280";
    private const string Line = "#e5e7eb";
    private const string Accent = "#0f766e";
    private const string Danger = "#b91c1c";

    public byte[] Render(MonthlyStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(1.5f, Unit.Centimetre);
            page.DefaultTextStyle(text => text.FontSize(9.5f).FontColor(Ink).FontFamily(BodyFont));

            page.Header().Element(header => ComposeHeader(header, statement));
            page.Content().Element(content => ComposeContent(content, statement));
            page.Footer().Element(ComposeFooter);
        })).GeneratePdf();
    }

    private static void ComposeHeader(IContainer container, MonthlyStatement s)
    {
        container.PaddingBottom(12).Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Column(info =>
                {
                    info.Item().Text(s.CondominiumName).FontSize(15).SemiBold().FontColor(Accent);

                    if (!string.IsNullOrWhiteSpace(s.Cnpj))
                    {
                        info.Item().Text($"CNPJ {FormatCnpj(s.Cnpj)}").FontSize(8).FontColor(Muted);
                    }

                    info.Item().Text(s.Address).FontSize(8).FontColor(Muted);
                });

                row.ConstantItem(170).AlignRight().Column(badge =>
                {
                    badge.Item().AlignRight().Text("PRESTAÇÃO DE CONTAS")
                        .FontSize(8).SemiBold().FontColor(Muted).LetterSpacing(0.08f);

                    badge.Item().PaddingTop(2).AlignRight().Text(MesPorExtenso(s.From))
                        .FontSize(13).SemiBold().FontColor(Ink);

                    badge.Item().AlignRight()
                        .Text($"{s.From:dd/MM/yyyy} a {s.To:dd/MM/yyyy}")
                        .FontSize(8).FontColor(Muted);
                });
            });

            column.Item().PaddingTop(8).LineHorizontal(1).LineColor(Line);
        });
    }

    private static void ComposeContent(IContainer container, MonthlyStatement s)
    {
        container.Column(column =>
        {
            column.Spacing(14);

            column.Item().Element(box => Resumo(box, s));

            if (s.Warnings.Count > 0)
            {
                column.Item().Element(box => Avisos(box, s));
            }

            column.Item().Row(row =>
            {
                row.RelativeItem().Element(cell => Movimentos(cell, "Receitas", s.Income, s.TotalIncome));
                row.ConstantItem(16);
                row.RelativeItem().Element(cell => Movimentos(cell, "Despesas", s.Expenses, s.TotalExpense));
            });

            column.Item().Element(box => Contas(box, s));
            column.Item().Element(box => Inadimplencia(box, s));

            if (s.Entries.Count > 0)
            {
                column.Item().PageBreak();
                column.Item().Element(box => Anexo(box, s));
            }
        });
    }

    /// <summary>
    /// A conta que o condomino confere de cabeca: tinha, entrou, saiu, ficou.
    /// </summary>
    private static void Resumo(IContainer container, MonthlyStatement s) =>
        container.Background("#f9fafb").Border(1).BorderColor(Line).Padding(14).Row(row =>
        {
            row.RelativeItem().Element(cell => Numero(cell, "Saldo anterior", s.OpeningBalance));
            row.RelativeItem().Element(cell => Numero(cell, "Receitas", s.TotalIncome, Accent));
            row.RelativeItem().Element(cell => Numero(cell, "Despesas", s.TotalExpense, Danger));
            row.RelativeItem().Element(cell => Numero(
                cell, "Resultado do mês", s.Result, s.Result < 0 ? Danger : Accent));
            row.RelativeItem().Element(cell => Numero(
                cell, "Saldo final", s.ClosingBalance, s.ClosingBalance < 0 ? Danger : Ink, destaque: true));
        });

    private static void Avisos(IContainer container, MonthlyStatement s) =>
        container.Background("#fffbeb").Border(1).BorderColor("#fde68a").Padding(10).Column(column =>
        {
            column.Item().Text("Observações").FontSize(8.5f).SemiBold().FontColor("#92400e");

            foreach (string aviso in s.Warnings)
            {
                column.Item().PaddingTop(3).Text($"• {aviso}").FontSize(8.5f).FontColor("#78350f");
            }
        });

    private static void Movimentos(
        IContainer container,
        string titulo,
        IReadOnlyList<StatementLine> linhas,
        decimal total) =>
        container.Column(column =>
        {
            column.Item().Text(titulo).FontSize(11).SemiBold().FontColor(Ink);

            if (linhas.Count == 0)
            {
                column.Item().PaddingTop(6).Text($"Nenhuma {titulo.TrimEnd('s').ToLower(Brazil)} no período.")
                    .FontSize(9).FontColor(Muted);
                return;
            }

            column.Item().PaddingTop(6).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn();
                    columns.ConstantColumn(72);
                    columns.ConstantColumn(38);
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeaderCell).Text("Conta");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Valor (R$)");
                    header.Cell().Element(HeaderCell).AlignRight().Text("%");
                });

                foreach (StatementLine linha in linhas)
                {
                    table.Cell().Element(BodyCell).Column(celula =>
                    {
                        celula.Item().Text(linha.Name).FontSize(9);
                        celula.Item().Text($"{linha.Code} · {linha.Count} lançamento(s)")
                            .FontSize(7.5f).FontColor(Muted);
                    });

                    table.Cell().Element(BodyCell).AlignRight().AlignMiddle()
                        .Text(Money(linha.Amount)).FontSize(9);

                    table.Cell().Element(BodyCell).AlignRight().AlignMiddle()
                        .Text($"{linha.Share * 100:0}%").FontSize(8).FontColor(Muted);
                }
            });

            column.Item().PaddingTop(6).Row(row =>
            {
                row.RelativeItem().Text("Total").FontSize(9).SemiBold();
                row.ConstantItem(72).AlignRight().Text(Money(total)).FontSize(9.5f).SemiBold();
                row.ConstantItem(38);
            });
        });

    /// <summary>
    /// Onde o dinheiro esta parado. E a secao que o conselho cruza com o extrato.
    /// </summary>
    private static void Contas(IContainer container, MonthlyStatement s) =>
        container.Column(column =>
        {
            column.Item().Text("Saldos por conta").FontSize(11).SemiBold();

            column.Item().PaddingTop(6).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn();
                    columns.ConstantColumn(82);
                    columns.ConstantColumn(82);
                    columns.ConstantColumn(82);
                    columns.ConstantColumn(90);
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeaderCell).Text("Conta");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Anterior");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Entradas");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Saídas");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Saldo final");
                });

                foreach (StatementAccountBalance conta in s.Accounts)
                {
                    table.Cell().Element(BodyCell).Text(text =>
                    {
                        text.Span(conta.Name).FontSize(9);

                        if (conta.IsReserveFund)
                        {
                            text.Span("  fundo de reserva").FontSize(7.5f).FontColor(Accent);
                        }
                    });

                    table.Cell().Element(BodyCell).AlignRight().Text(Money(conta.Opening)).FontSize(9);
                    table.Cell().Element(BodyCell).AlignRight().Text(Money(conta.In)).FontSize(9);
                    table.Cell().Element(BodyCell).AlignRight().Text(Money(conta.Out)).FontSize(9);
                    table.Cell().Element(BodyCell).AlignRight()
                        .Text(Money(conta.Closing)).FontSize(9).SemiBold();
                }
            });
        });

    private static void Inadimplencia(IContainer container, MonthlyStatement s) =>
        container.Column(column =>
        {
            column.Item().Text("Inadimplência").FontSize(11).SemiBold();

            if (s.Delinquency.Units == 0)
            {
                column.Item().PaddingTop(4)
                    .Text("Nenhuma cobrança vencida em aberto no fim do período.")
                    .FontSize(9).FontColor(Muted);
                return;
            }

            column.Item().PaddingTop(4).Text(text =>
            {
                text.Span($"{s.Delinquency.Units} unidade(s)").FontSize(9).SemiBold();
                text.Span(" com ").FontSize(9).FontColor(Muted);
                text.Span($"R$ {Money(s.Delinquency.Outstanding)}").FontSize(9).SemiBold().FontColor(Danger);
                text.Span(" em aberto, mais ").FontSize(9).FontColor(Muted);
                text.Span($"R$ {Money(s.Delinquency.LateCharges)}").FontSize(9).SemiBold();
                text.Span(" de multa e juros apurados até ").FontSize(9).FontColor(Muted);
                text.Span($"{s.To:dd/MM/yyyy}").FontSize(9).FontColor(Muted);
                text.Span(".").FontSize(9).FontColor(Muted);
            });
        });

    /// <summary>Lancamento por lancamento, para quem quiser conferir.</summary>
    private static void Anexo(IContainer container, MonthlyStatement s) =>
        container.Column(column =>
        {
            column.Item().Text("Movimento do período").FontSize(11).SemiBold();
            column.Item().PaddingTop(2)
                .Text("Todos os lançamentos do caixa, na ordem em que o dinheiro se moveu.")
                .FontSize(8).FontColor(Muted);

            column.Item().PaddingTop(8).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(48);
                    columns.RelativeColumn();
                    columns.ConstantColumn(96);
                    columns.ConstantColumn(80);
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeaderCell).Text("Data");
                    header.Cell().Element(HeaderCell).Text("Histórico");
                    header.Cell().Element(HeaderCell).Text("Conta");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Valor (R$)");
                });

                foreach (StatementEntry lancamento in s.Entries)
                {
                    table.Cell().Element(BodyCell)
                        .Text($"{lancamento.Date:dd/MM}").FontSize(8.5f).FontColor(Muted);

                    table.Cell().Element(BodyCell).Column(celula =>
                    {
                        celula.Item().Text(lancamento.Description).FontSize(8.5f);

                        // Conta bancaria e documento nao cabem em coluna propria
                        // sem espremer o historico, e sao o que se procura quando
                        // um lancamento e questionado.
                        string rodape = lancamento.BankAccountName;

                        if (!string.IsNullOrWhiteSpace(lancamento.DocumentNumber))
                        {
                            rodape += $" · doc. {lancamento.DocumentNumber}";
                        }

                        if (!lancamento.Reconciled)
                        {
                            rodape += " · não conferido";
                        }

                        celula.Item().Text(rodape).FontSize(7).FontColor(Muted);
                    });

                    table.Cell().Element(BodyCell)
                        .Text(lancamento.AccountName).FontSize(7.5f).FontColor(Muted);

                    // O sinal na frente evita a duvida de qual coluna era qual
                    // quando alguem le so o anexo, solto do resto.
                    table.Cell().Element(BodyCell).AlignRight()
                        .Text((lancamento.IsIncome ? "+" : "−") + Money(lancamento.Amount))
                        .FontSize(8.5f).FontColor(lancamento.IsIncome ? Accent : Ink);
                }
            });
        });

    private static void ComposeFooter(IContainer container) =>
        container.PaddingTop(10).BorderTop(1).BorderColor(Line).PaddingTop(6).Row(row =>
        {
            row.RelativeItem().Text(text =>
            {
                text.Span("Emitido em ").FontSize(7.5f).FontColor(Muted);
                text.Span(DateTime.Now.ToString("dd/MM/yyyy 'às' HH:mm", Brazil))
                    .FontSize(7.5f).FontColor(Muted);
                text.Span("  ·  Documento gerado pela Logement Administradora.")
                    .FontSize(7.5f).FontColor(Muted);
            });

            row.ConstantItem(90).AlignRight().Text(text =>
            {
                text.Span("Página ").FontSize(7.5f).FontColor(Muted);
                text.CurrentPageNumber().FontSize(7.5f).FontColor(Muted);
                text.Span(" de ").FontSize(7.5f).FontColor(Muted);
                text.TotalPages().FontSize(7.5f).FontColor(Muted);
            });
        });

    private static void Numero(
        IContainer container,
        string rotulo,
        decimal valor,
        string cor = Ink,
        bool destaque = false) =>
        container.Column(column =>
        {
            column.Item().Text(rotulo)
                .FontSize(7.5f).SemiBold().FontColor(Muted).LetterSpacing(0.06f);

            column.Item().PaddingTop(2).Text($"R$ {Money(valor)}")
                .FontSize(destaque ? 13 : 11.5f).SemiBold().FontColor(cor);
        });

    private static IContainer HeaderCell(IContainer container) => container
        .BorderBottom(1).BorderColor(Ink).PaddingBottom(4)
        .DefaultTextStyle(text => text.FontSize(7.5f).SemiBold().FontColor(Muted).LetterSpacing(0.06f));

    private static IContainer BodyCell(IContainer container) => container
        .BorderBottom(1).BorderColor(Line).PaddingVertical(5);

    private static string Money(decimal value) => value.ToString("N2", Brazil);

    /// <summary>"setembro de 2026", com a inicial maiuscula.</summary>
    private static string MesPorExtenso(DateOnly date)
    {
        string texto = date.ToString("MMMM 'de' yyyy", Brazil);
        return char.ToUpper(texto[0], Brazil) + texto[1..];
    }

    private static string FormatCnpj(string digits) =>
        digits.Length == 14
            ? $"{digits[..2]}.{digits[2..5]}.{digits[5..8]}/{digits[8..12]}-{digits[12..]}"
            : digits;
}
