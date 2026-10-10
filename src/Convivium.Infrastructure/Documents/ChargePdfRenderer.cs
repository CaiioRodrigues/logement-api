namespace Convivium.Infrastructure.Documents;

using System.Globalization;
using Convivium.Application.Billing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

/// <summary>
/// Desenha o boleto de cobranca: o mesmo layout sai em PDF ou em PNG.
/// </summary>
/// <remarks>
/// O documento e montado a partir de um <see cref="ChargeDocument"/> ja
/// resolvido: nao consulta banco nem calcula encargos, so formata.
/// </remarks>
public sealed class ChargePdfRenderer : IChargeDocumentRenderer
{
    private static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>
    /// Lato vem embutida no QuestPDF, entao o PDF sai identico em qualquer
    /// servidor, sem depender de fonte instalada no sistema operacional.
    /// </summary>
    private const string BodyFont = "Lato";

    private const string Ink = "#1a1a1a";
    private const string Muted = "#6b7280";
    private const string Line = "#e5e7eb";
    private const string Accent = "#0f766e";
    private const string Danger = "#b91c1c";
    private const string Success = "#15803d";

    public byte[] Render(ChargeDocument document) => Compose(document, fitToContent: false).GeneratePdf();

    public byte[] RenderImage(ChargeDocument document)
    {
        // 150 DPI: o QR Code do PIX sai com modulo grosso o bastante para ser
        // lido da tela de um celular por outro celular, que e como boleto
        // mandado no WhatsApp costuma ser pago. Abaixo disso a camera erra.
        var settings = new ImageGenerationSettings
        {
            ImageFormat = ImageFormat.Png,
            RasterDpi = 150,
        };

        // Altura acompanhando o conteudo, e nao A4 fixo: o boleto ocupa um
        // terco da folha, e numa imagem os dois tercos em branco viram
        // miniatura ilegivel na conversa. So a primeira pagina — mandar um
        // album no lugar de um boleto confunde mais do que ajuda.
        return Compose(document, fitToContent: true).GenerateImages(settings).First();
    }

    /// <summary>
    /// Monta o documento. Em <paramref name="fitToContent"/> a pagina cresce
    /// junto com o conteudo, em vez de ocupar uma A4 inteira.
    /// </summary>
    private static Document Compose(ChargeDocument document, bool fitToContent)
    {
        ArgumentNullException.ThrowIfNull(document);

        return Document.Create(container => container.Page(page =>
        {
            if (fitToContent)
            {
                page.ContinuousSize(PageSizes.A4.Width);
            }
            else
            {
                page.Size(PageSizes.A4);
            }

            page.Margin(1.6f, Unit.Centimetre);
            page.DefaultTextStyle(text => text.FontSize(10).FontColor(Ink).FontFamily(BodyFont));

            page.Header().Element(header => ComposeHeader(header, document));
            page.Content().Element(content => ComposeContent(content, document));

            // O rodape traz "pagina X de Y" e a altura da folha, que nao
            // existem numa pagina continua — e a imagem ja vai acompanhada da
            // mensagem com o mesmo link.
            if (!fitToContent)
            {
                page.Footer().Element(footer => ComposeFooter(footer, document));
            }
        }));
    }

    private static void ComposeHeader(IContainer container, ChargeDocument document)
    {
        container.PaddingBottom(14).Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Column(info =>
                {
                    info.Item().Text(document.CondominiumName)
                        .FontSize(16).SemiBold().FontColor(Accent);

                    if (!string.IsNullOrWhiteSpace(document.CondominiumCnpj))
                    {
                        info.Item().Text($"CNPJ {FormatCnpj(document.CondominiumCnpj)}")
                            .FontSize(8.5f).FontColor(Muted);
                    }

                    info.Item().Text(document.CondominiumAddress).FontSize(8.5f).FontColor(Muted);
                });

                row.ConstantItem(130).AlignRight().Column(badge =>
                {
                    badge.Item().AlignRight().Text(document.Slip is null ? "AVISO DE COBRANÇA" : "BOLETO BANCÁRIO")
                        .FontSize(8).SemiBold().FontColor(Muted).LetterSpacing(0.08f);

                    (string label, string color) = document switch
                    {
                        { IsPaid: true } => ("PAGO", Success),
                        { IsOverdue: true } doc => ($"VENCIDO HÁ {doc.DaysLate} DIA(S)", Danger),
                        _ => ("EM ABERTO", Accent),
                    };

                    badge.Item().PaddingTop(4).AlignRight()
                        .Background(color).PaddingVertical(3).PaddingHorizontal(8)
                        .Text(label).FontSize(8).SemiBold().FontColor(Colors.White);
                });
            });

            column.Item().PaddingTop(10).LineHorizontal(1).LineColor(Line);
        });
    }

    private static void ComposeContent(IContainer container, ChargeDocument document)
    {
        container.Column(column =>
        {
            // Com ficha de compensacao a folha fica mais cheia; espacamento
            // menor mantem cobranca e ficha na mesma pagina para o caso comum.
            column.Spacing(document.Slip is null ? 16 : 10);

            // Identificacao
            column.Item().Row(row =>
            {
                row.RelativeItem().Element(cell => Field(cell, "Unidade", document.UnitIdentifier));
                row.RelativeItem().Element(cell =>
                    Field(cell, "Responsável", document.PayerName ?? "Não informado"));
                row.ConstantItem(90).Element(cell => Field(cell, "Competência", document.Competence));
                row.ConstantItem(90).Element(cell => Field(
                    cell,
                    "Vencimento",
                    document.DueDate.ToString("dd/MM/yyyy", Brazil),
                    document.IsOverdue ? Danger : Ink));
            });

            // Itens
            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn();
                    columns.ConstantColumn(110);
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeaderCell).Text("Descrição");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Valor (R$)");
                });

                Func<IContainer, IContainer> bodyCell = document.Slip is null ? BodyCell : CompactBodyCell;

                foreach (ChargeItemDto item in document.Items)
                {
                    table.Cell().Element(bodyCell).Text(item.Description);
                    table.Cell().Element(bodyCell).AlignRight().Text(Money(item.Amount));
                }
            });

            // Totais
            column.Item().AlignRight().Width(260).Column(totals =>
            {
                totals.Item().Element(cell => TotalRow(cell, "Subtotal", document.TotalAmount));

                if (document.PaidAmount > 0)
                {
                    totals.Item().Element(cell =>
                        TotalRow(cell, "Já recebido", -document.PaidAmount, Success));
                }

                if (document.LateFee > 0)
                {
                    totals.Item().Element(cell => TotalRow(cell, "Multa por atraso", document.LateFee, Danger));
                }

                if (document.Interest > 0)
                {
                    totals.Item().Element(cell =>
                        TotalRow(cell, $"Juros de mora ({document.DaysLate} dias)", document.Interest, Danger));
                }

                totals.Item().PaddingTop(6).BorderTop(1).BorderColor(Ink).PaddingTop(6).Row(row =>
                {
                    row.RelativeItem().Text(document.IsPaid ? "TOTAL PAGO" : "TOTAL A PAGAR")
                        .FontSize(11).SemiBold();

                    row.ConstantItem(110).AlignRight()
                        .Text(Money(document.IsPaid ? document.TotalAmount : document.TotalDue))
                        .FontSize(13).Bold().FontColor(document.IsPaid ? Success : Accent);
                });
            });

            // Linha digitavel logo abaixo do total: e o que o morador copia
            // para pagar no aplicativo do banco quando nao usa o PIX.
            if (!document.IsPaid && document.Slip is { } slipLine)
            {
                column.Item().Element(cell => ComposeDigitableLine(cell, slipLine));
            }

            // PIX
            if (!document.IsPaid && !string.IsNullOrWhiteSpace(document.PixPayload))
            {
                column.Item().Element(cell => ComposePix(cell, document.PixPayload, fromBank: document.Slip is not null));
            }

            if (!string.IsNullOrWhiteSpace(document.Notes))
            {
                column.Item().Background("#f9fafb").Padding(10).Column(notes =>
                {
                    notes.Item().Text("Observações").FontSize(8).SemiBold().FontColor(Muted);
                    notes.Item().PaddingTop(2).Text(document.Notes).FontSize(9);
                });
            }

            if (document.IsOverdue)
            {
                column.Item().Text(text =>
                {
                    text.DefaultTextStyle(style => style.FontSize(8).FontColor(Muted));
                    text.Span(
                        "Encargos calculados até a data de emissão deste aviso, conforme o " +
                        "Código Civil, art. 1.336, § 1º, e a convenção do condomínio. " +
                        "O valor se altera a cada dia de atraso.");
                });
            }

            // A ficha vai inteira na mesma pagina: cortada ao meio, a leitora
            // do caixa nao acha o codigo de barras.
            if (!document.IsPaid && document.Slip is { } slip)
            {
                column.Item().ShowEntire().Element(cell => ComposeSlip(cell, slip));
            }
        });
    }

    private static void ComposeDigitableLine(IContainer container, BankSlipDocument slip)
    {
        container.Background("#f9fafb").Border(1).BorderColor(Line).PaddingVertical(7).PaddingHorizontal(10).Column(column =>
        {
            column.Item().Text($"Linha digitável — {slip.BankName}")
                .FontSize(7.5f).SemiBold().FontColor(Muted).LetterSpacing(0.06f);

            column.Item().PaddingTop(3).Text(slip.DigitableLine)
                .FontSize(12.5f).SemiBold().LetterSpacing(0.03f);
        });
    }

    /// <summary>
    /// A ficha de compensacao: a parte do boleto que o caixa le. Segue o
    /// desenho de sempre — cabecalho com banco e linha digitavel, grade de
    /// campos, pagador e o codigo de barras embaixo — porque e o desenho que
    /// operador de caixa e aplicativo de banco reconhecem.
    /// </summary>
    private static void ComposeSlip(IContainer container, BankSlipDocument slip)
    {
        container.Column(column =>
        {
            // Linha de corte
            column.Item().PaddingTop(4).PaddingBottom(8).Row(row =>
            {
                row.AutoItem().AlignMiddle().Text("Recorte aqui").FontSize(7).FontColor(Muted);
                row.RelativeItem().PaddingLeft(6).AlignMiddle().LineHorizontal(0.75f).LineColor(Muted);
            });

            // Cabecalho: banco | codigo | linha digitavel
            column.Item().BorderBottom(1.5f).BorderColor(Ink).PaddingBottom(3).Row(row =>
            {
                row.ConstantItem(110).AlignBottom().Text(slip.BankName).FontSize(13).Bold();

                row.ConstantItem(56).BorderLeft(1.5f).BorderRight(1.5f).BorderColor(Ink)
                    .AlignCenter().AlignBottom().Text(slip.BankCode).FontSize(13).Bold();

                row.RelativeItem().AlignRight().AlignBottom().Text(slip.DigitableLine)
                    .FontSize(10.5f).SemiBold();
            });

            column.Item().BorderLeft(0.75f).BorderColor(Muted).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(3);
                    columns.RelativeColumn(1);
                });

                table.Cell().Element(SlipCell).Element(cell =>
                    SlipField(cell, "Local de pagamento", "Pagável em qualquer banco ou lotérica"));
                table.Cell().Element(SlipCell).Element(cell =>
                    SlipField(cell, "Vencimento", slip.DueDate.ToString("dd/MM/yyyy", Brazil), bold: true));

                string beneficiary = string.IsNullOrWhiteSpace(slip.BeneficiaryDocument)
                    ? slip.BeneficiaryName
                    : $"{slip.BeneficiaryName} — CNPJ {FormatCnpj(slip.BeneficiaryDocument)}";

                table.Cell().Element(SlipCell).Element(cell => SlipField(cell, "Beneficiário", beneficiary));
                table.Cell().Element(SlipCell).Element(cell => SlipField(cell, "Nosso número", slip.OurNumber ?? "—"));

                table.Cell().Element(SlipCell).Element(cell =>
                    SlipField(cell, "Nº do documento", slip.DocumentNumber ?? "—"));
                table.Cell().Element(SlipCell).Element(cell =>
                    SlipField(cell, "(=) Valor do documento", Money(slip.Amount), bold: true));

                table.Cell().RowSpan(2).Element(SlipCell).Column(instructions =>
                {
                    instructions.Item().Text("Instruções (responsabilidade do beneficiário)")
                        .FontSize(6.5f).FontColor(Muted);

                    foreach (string line in slip.Instructions)
                    {
                        instructions.Item().PaddingTop(1.5f).Text(line).FontSize(8);
                    }
                });
                table.Cell().Element(SlipCell).Element(cell => SlipField(cell, "(+) Mora / Multa", string.Empty));
                table.Cell().Element(SlipCell).Element(cell => SlipField(cell, "(=) Valor cobrado", string.Empty));

                table.Cell().ColumnSpan(2).Element(SlipCell).Column(payer =>
                {
                    payer.Item().Text("Pagador").FontSize(6.5f).FontColor(Muted);

                    string document = string.IsNullOrWhiteSpace(slip.PayerDocument)
                        ? string.Empty
                        : $" — {FormatDocument(slip.PayerDocument)}";

                    payer.Item().PaddingTop(1).Text($"{slip.PayerName}{document}").FontSize(8.5f).SemiBold();
                    payer.Item().Text(slip.PayerAddress).FontSize(8);
                });
            });

            // Codigo de barras: 103 mm x 13 mm, com margem de silencio dos
            // dois lados para a leitora achar onde comeca.
            column.Item().PaddingTop(8).Row(row =>
            {
                // FitArea: o desenho tem a proporcao dos 405 modulos e cabe
                // inteiro na caixa, sem pedir mais largura do que os 103 mm.
                row.ConstantItem(292, Unit.Point).Height(37).Svg(Interleaved2Of5.Svg(slip.Barcode)).FitArea();

                row.RelativeItem().AlignRight().AlignBottom().Column(label =>
                {
                    label.Item().AlignRight().Text("Autenticação mecânica").FontSize(6.5f).FontColor(Muted);
                    label.Item().AlignRight().Text("Ficha de Compensação").FontSize(8).SemiBold();
                });
            });
        });
    }

    private static IContainer SlipCell(IContainer container) => container
        .BorderBottom(0.75f).BorderRight(0.75f).BorderColor(Muted)
        .PaddingVertical(3).PaddingHorizontal(4);

    private static void SlipField(IContainer container, string label, string value, bool bold = false)
        => container.Column(column =>
        {
            column.Item().Text(label).FontSize(6.5f).FontColor(Muted);

            var text = column.Item().PaddingTop(1).Text(value.Length == 0 ? " " : value).FontSize(8.5f);

            if (bold)
            {
                text.Bold();
            }
        });

    private static void ComposePix(IContainer container, string payload, bool fromBank)
    {
        container.Border(1).BorderColor(Line).Padding(fromBank ? 10 : 12).Row(row =>
        {
            // 90 pt ainda da modulo grosso para a camera; a ficha embaixo
            // precisa do espaco que sobra.
            row.ConstantItem(fromBank ? 90 : 108).Image(QrCodeRenderer.Png(payload)).FitArea();

            row.RelativeItem().PaddingLeft(14).Column(column =>
            {
                column.Item().Text("Pague com PIX").FontSize(12).SemiBold().FontColor(Accent);

                column.Item().PaddingTop(2).Text(
                    "Abra o aplicativo do seu banco, escolha PIX, aponte para o QR Code " +
                    "ou use o código copia e cola abaixo." +
                    (fromBank ? " O pagamento por PIX dá baixa neste boleto na hora." : string.Empty))
                    .FontSize(8.5f).FontColor(Muted);

                column.Item().PaddingTop(8).Text("Copia e cola")
                    .FontSize(7.5f).SemiBold().FontColor(Muted).LetterSpacing(0.06f);

                // Sem fonte monoespacada de proposito: QuestPDF nao usa fontes do
                // sistema por padrao, e exigir uma que talvez nao exista no servidor
                // faria a geracao do boleto falhar em producao. O codigo do PIX e
                // feito para copiar e colar, nao para ler caractere a caractere.
                column.Item().PaddingTop(3).Background("#f3f4f6").Padding(6)
                    .Text(payload).FontSize(6.8f).LineHeight(1.4f).LetterSpacing(0.02f);
            });
        });
    }

    private static void ComposeFooter(IContainer container, ChargeDocument document)
    {
        container.PaddingTop(10).BorderTop(1).BorderColor(Line).PaddingTop(6).Row(row =>
        {
            row.RelativeItem().Column(column =>
            {
                column.Item().Text($"Emitido em {DateTime.Now.ToString("dd/MM/yyyy 'às' HH:mm", Brazil)}")
                    .FontSize(7.5f).FontColor(Muted);

                if (!string.IsNullOrWhiteSpace(document.PublicUrl))
                {
                    column.Item().Text($"Consulte on-line: {document.PublicUrl}")
                        .FontSize(7.5f).FontColor(Muted);
                }
            });

            row.ConstantItem(130).AlignRight().AlignBottom()
                .Text(text =>
                {
                    text.DefaultTextStyle(style => style.FontSize(7.5f).FontColor(Muted));
                    text.Span("Página ");
                    text.CurrentPageNumber();
                    text.Span(" de ");
                    text.TotalPages();
                });
        });
    }

    // --- Auxiliares de layout ---

    private static void Field(IContainer container, string label, string value, string color = Ink)
        => container.Column(column =>
        {
            column.Item().Text(label).FontSize(7.5f).SemiBold().FontColor(Muted).LetterSpacing(0.06f);
            column.Item().PaddingTop(1).Text(value).FontSize(10.5f).SemiBold().FontColor(color);
        });

    private static void TotalRow(IContainer container, string label, decimal value, string color = Ink)
        => container.PaddingVertical(1.5f).Row(row =>
        {
            row.RelativeItem().Text(label).FontSize(9).FontColor(Muted);
            row.ConstantItem(110).AlignRight().Text(Money(value)).FontSize(9.5f).FontColor(color);
        });

    private static IContainer HeaderCell(IContainer container) => container
        .BorderBottom(1).BorderColor(Ink).PaddingBottom(5)
        .DefaultTextStyle(text => text.FontSize(8).SemiBold().FontColor(Muted).LetterSpacing(0.06f));

    private static IContainer BodyCell(IContainer container) => container
        .BorderBottom(1).BorderColor(Line).PaddingVertical(6);

    private static IContainer CompactBodyCell(IContainer container) => container
        .BorderBottom(1).BorderColor(Line).PaddingVertical(3.5f);

    private static string Money(decimal value) => value.ToString("N2", Brazil);

    /// <summary>CPF ou CNPJ pontuado; outro tamanho sai como veio.</summary>
    private static string FormatDocument(string digits) => digits.Length switch
    {
        11 => $"CPF {digits[..3]}.{digits[3..6]}.{digits[6..9]}-{digits[9..]}",
        14 => $"CNPJ {FormatCnpj(digits)}",
        _ => digits,
    };

    /// <summary>"12345678000195" -> "12.345.678/0001-95".</summary>
    private static string FormatCnpj(string digits) =>
        digits.Length == 14
            ? $"{digits[..2]}.{digits[2..5]}.{digits[5..8]}/{digits[8..12]}-{digits[12..]}"
            : digits;
}
