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

            // Lancamento por lancamento, numerado, nas duas colunas. E o
            // formato que o condomino brasileiro reconhece de qualquer
            // balancete, e o que ele procura quando questiona uma conta.
            column.Item().Element(box => CreditosEDebitos(box, s));

            column.Item().PageBreak();

            // Daqui em diante, a leitura de quem analisa: o mesmo dinheiro
            // agrupado por conta, onde ele esta, e o que ficou pendente.
            column.Item().Row(row =>
            {
                row.RelativeItem().Element(cell => Movimentos(cell, "Receitas", s.Income, s.TotalIncome));
                row.ConstantItem(16);
                row.RelativeItem().Element(cell => Movimentos(cell, "Despesas", s.Expenses, s.TotalExpense));
            });

            column.Item().Element(box => Contas(box, s));
            column.Item().Element(box => Fundo(box, s));
            column.Item().Element(box => Pendencias(box, s));
            column.Item().Element(Confidencialidade);
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

    /// <summary>
    /// O fundo de reserva em destaque, e nao diluido entre as contas.
    /// </summary>
    /// <remarks>
    /// E a primeira pergunta de qualquer assembleia, e a lei trata este
    /// dinheiro como separado: tem destinacao propria, definida em convencao,
    /// e nao se confunde com o caixa de operacao.
    /// </remarks>
    private static void Fundo(IContainer container, MonthlyStatement s)
    {
        ReserveFundBalance f = s.ReserveFund;

        if (f.Opening == 0 && f.Closing == 0 && f.In == 0)
        {
            return;
        }

        container.Background("#f0fdfa").Border(1).BorderColor("#99f6e4").Padding(12).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text("Fundo de reserva")
                    .FontSize(9).SemiBold().FontColor(Accent);

                col.Item().PaddingTop(2).Text(text =>
                {
                    text.Span($"R$ {Money(f.Closing)}").FontSize(15).SemiBold().FontColor(Ink);
                    text.Span($"   {f.ShareOfTotal * 100:0}% do saldo do condomínio")
                        .FontSize(8).FontColor(Muted);
                });
            });

            row.ConstantItem(300).AlignRight().Text(text =>
            {
                text.Span("Anterior ").FontSize(8).FontColor(Muted);
                text.Span($"R$ {Money(f.Opening)}").FontSize(8.5f);
                text.Span("   ·   Entrou ").FontSize(8).FontColor(Muted);
                text.Span($"R$ {Money(f.In)}").FontSize(8.5f).FontColor(Accent);
                text.Span("   ·   Saiu ").FontSize(8).FontColor(Muted);
                text.Span($"R$ {Money(f.Out)}").FontSize(8.5f).FontColor(f.Out > 0 ? Danger : Ink);
            });
        });
    }

    /// <summary>
    /// O que o condominio deve e o que tem a receber no fim do periodo.
    /// </summary>
    /// <remarks>
    /// Sem isto o saldo engana: R$ 16 mil em caixa com R$ 20 mil vencendo e
    /// uma situacao bem diferente dos mesmos R$ 16 mil sem dever nada. As
    /// duas colunas juntas sao o que permite ao conselho decidir se aprova
    /// uma despesa nova.
    /// </remarks>
    private static void Pendencias(IContainer container, MonthlyStatement s) =>
        container.Row(row =>
        {
            row.RelativeItem().Element(cell => ListaDePendencias(
                cell, "Contas a pagar", s.Payables, "Fornecedor", Danger));

            row.ConstantItem(16);

            row.RelativeItem().Element(cell => ListaDePendencias(
                cell, "Contas a receber", s.Receivables, "Unidade", Accent, s.Delinquency));
        });

    private static void ListaDePendencias(
        IContainer container,
        string titulo,
        IReadOnlyList<PendingItem> itens,
        string rotuloDaContraparte,
        string cor,
        DelinquencySummary? inadimplencia = null) =>
        container.Column(column =>
        {
            column.Item().Row(cabecalho =>
            {
                cabecalho.RelativeItem().Text(titulo).FontSize(11).SemiBold();
                cabecalho.ConstantItem(90).AlignRight()
                    .Text($"R$ {Money(itens.Sum(i => i.Amount))}")
                    .FontSize(11).SemiBold().FontColor(itens.Count > 0 ? cor : Muted);
            });

            if (itens.Count == 0)
            {
                column.Item().PaddingTop(4).Text("Nada pendente no fim do período.")
                    .FontSize(8.5f).FontColor(Muted);
                return;
            }

            column.Item().PaddingTop(6).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn();
                    columns.ConstantColumn(44);
                    columns.ConstantColumn(72);
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeaderCell).Text(rotuloDaContraparte);
                    header.Cell().Element(HeaderCell).Text("Venc.");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Valor (R$)");
                });

                foreach (PendingItem item in itens)
                {
                    table.Cell().Element(BodyCell).Column(celula =>
                    {
                        celula.Item().Text(item.Counterpart ?? "Não informado").FontSize(8.5f);
                        celula.Item().Text(item.Description).FontSize(7).FontColor(Muted);
                    });

                    table.Cell().Element(BodyCell).Column(celula =>
                    {
                        celula.Item().Text($"{item.DueDate:dd/MM}").FontSize(8);

                        // Dias de atraso na data do balancete, e nao de hoje:
                        // o documento precisa dizer sempre a mesma coisa.
                        if (item.DaysLate > 0)
                        {
                            celula.Item().Text($"{item.DaysLate}d").FontSize(7).FontColor(Danger);
                        }
                    });

                    table.Cell().Element(BodyCell).AlignRight().AlignMiddle()
                        .Text(Money(item.Amount)).FontSize(8.5f);
                }
            });

            // Multa e juros vivem aqui, e nao numa secao propria: sao a mesma
            // divida vista de outro angulo, e separa-las rendia uma secao de
            // duas linhas sozinha no fim do documento.
            if (inadimplencia is { Units: > 0 } atraso)
            {
                column.Item().PaddingTop(5).Text(text =>
                {
                    text.Span($"{atraso.Units} unidade(s) em atraso, com ")
                        .FontSize(7.5f).FontColor(Muted);
                    text.Span($"R$ {Money(atraso.LateCharges)}").FontSize(7.5f).SemiBold();
                    text.Span(" de multa e juros apurados até a data do balancete.")
                        .FontSize(7.5f).FontColor(Muted);
                });
            }
        });

    /// <summary>
    /// Creditos e debitos, um por linha, numerados, em duas colunas.
    /// </summary>
    /// <remarks>
    /// <para>
    /// E o formato tradicional do balancete de condominio, e nao e capricho:
    /// e por ele que o condomino acha a conta que quer questionar. Agrupar
    /// por conta contabil responde "no que foi o dinheiro"; so a lista
    /// responde "quanto foi pago ao eletricista no dia 12".
    /// </para>
    /// <para>
    /// As duas colunas sao independentes — a numeracao de cada lado comeca do
    /// 1 e nao ha relacao entre a linha 3 da esquerda e a da direita.
    /// </para>
    /// </remarks>
    private static void CreditosEDebitos(IContainer container, MonthlyStatement s) =>
        container.Row(row =>
        {
            row.RelativeItem().Element(cell => Coluna(
                cell, "Créditos", s.Entries.Where(e => e.IsIncome).ToList(), s.TotalIncome, Accent));

            row.ConstantItem(18);

            row.RelativeItem().Element(cell => Coluna(
                cell, "Débitos", s.Entries.Where(e => !e.IsIncome).ToList(), s.TotalExpense, Ink));
        });

    private static void Coluna(
        IContainer container,
        string titulo,
        IReadOnlyList<StatementEntry> lancamentos,
        decimal total,
        string cor) =>
        container.Column(column =>
        {
            column.Item().BorderBottom(1).BorderColor(Ink).PaddingBottom(4).Row(cabecalho =>
            {
                cabecalho.RelativeItem().Text(titulo)
                    .FontSize(10).SemiBold().FontColor(cor).LetterSpacing(0.04f);
                cabecalho.ConstantItem(70).AlignRight().Text("Valor (R$)")
                    .FontSize(7.5f).SemiBold().FontColor(Muted).LetterSpacing(0.06f);
            });

            if (lancamentos.Count == 0)
            {
                column.Item().PaddingTop(6)
                    .Text($"Nenhum {titulo.TrimEnd('s').ToLower(Brazil)} no período.")
                    .FontSize(8.5f).FontColor(Muted);
                return;
            }

            int numero = 0;

            foreach (StatementEntry lancamento in lancamentos)
            {
                numero++;

                column.Item().BorderBottom(1).BorderColor(Line).PaddingVertical(4).Row(linha =>
                {
                    linha.ConstantItem(16).Text($"{numero}")
                        .FontSize(7.5f).FontColor(Muted);

                    linha.RelativeItem().Column(celula =>
                    {
                        celula.Item().Text(lancamento.Description).FontSize(8.5f);

                        // Data, conta e documento em subtexto: e o que se
                        // procura quando um lancamento e questionado, e nao
                        // cabe em coluna propria sem espremer o historico.
                        string detalhe = $"{lancamento.Date:dd/MM} · {lancamento.BankAccountName}";

                        if (!string.IsNullOrWhiteSpace(lancamento.DocumentNumber))
                        {
                            detalhe += $" · doc. {lancamento.DocumentNumber}";
                        }

                        if (!lancamento.Reconciled)
                        {
                            detalhe += " · não conferido";
                        }

                        celula.Item().Text(detalhe).FontSize(6.5f).FontColor(Muted);
                    });

                    linha.ConstantItem(70).AlignRight().AlignMiddle()
                        .Text(Money(lancamento.Amount)).FontSize(8.5f);
                });
            }

            column.Item().PaddingTop(6).Row(rodape =>
            {
                rodape.ConstantItem(16);
                rodape.RelativeItem().Text("TOTAL")
                    .FontSize(8.5f).SemiBold().LetterSpacing(0.04f);
                rodape.ConstantItem(70).AlignRight().Text(Money(total))
                    .FontSize(10).SemiBold().FontColor(cor);
            });
        });

    /// <summary>
    /// A ressalva que todo balancete de condominio carrega.
    /// </summary>
    /// <remarks>
    /// O documento traz nome de unidade e valor em aberto. E prestacao de
    /// contas, nao lista de devedores para circular em grupo de WhatsApp — e
    /// expor condomino inadimplente ja rendeu condenacao por dano moral.
    /// </remarks>
    private static void Confidencialidade(IContainer container) =>
        // ShowEntire para a nota nunca partir no meio: uma frase sobre
        // constranger condomino cortada ao meio da pagina diz o contrario do
        // que pretende. Curta o bastante para caber junto do resto.
        container.ShowEntire().Text(
            "Documento destinado aos condôminos e ao conselho fiscal. Não é permitida a " +
            "divulgação a terceiros nem o uso como forma de constrangimento ou exposição " +
            "de qualquer condômino.")
            .FontSize(7).FontColor(Muted);

    private static void ComposeFooter(IContainer container) =>
        container.PaddingTop(10).BorderTop(1).BorderColor(Line).PaddingTop(6).Row(row =>
        {
            row.RelativeItem().Text(text =>
            {
                text.Span("Emitido em ").FontSize(7.5f).FontColor(Muted);
                text.Span(DateTime.Now.ToString("dd/MM/yyyy 'às' HH:mm", Brazil))
                    .FontSize(7.5f).FontColor(Muted);
                text.Span("  ·  Documento gerado pelo sistema de gestão do condomínio.")
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
