namespace Convivium.Tests.Documents;

using Convivium.Application.Billing;
using Convivium.Domain.Payments;
using Convivium.Infrastructure.Documents;
using UglyToad.PdfPig;

/// <summary>
/// O boleto com ficha de compensacao precisa sair inteiro, nos dois
/// formatos, e com a linha digitavel legivel como texto no PDF.
/// </summary>
public class ChargePdfRendererTests
{
    private static readonly DateOnly DueDate = new(2026, 10, 20);

    private static readonly string Barcode =
        BoletoLayout.BuildBarcode("077", 1188.24m, DueDate, "0000000000000000000012345");

    public ChargePdfRendererTests() =>
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

    /// <summary>Texto da pagina sem espacos: o extrator nao garante onde eles caem.</summary>
    private static string Flat(string text) => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static ChargeDocument Document(bool withSlip = true, int items = 6) => new()
    {
        CondominiumName = "Residencial Convivium",
        CondominiumCnpj = "12345678000195",
        CondominiumAddress = "Rua dos Timbiras, 100 - Centro, Belo Horizonte/MG - CEP 30140061",
        UnitIdentifier = "Bloco A - 104",
        PayerName = "Diego Dias",
        Competence = "09/2026",
        DueDate = DueDate,
        TotalAmount = 1188.24m,
        TotalDue = 1188.24m,
        Items = [.. Enumerable.Range(1, items).Select(i =>
            new ChargeItemDto(Guid.NewGuid(), Domain.Billing.ChargeItemKind.CondoFee, $"Rateio item {i}", 198.04m))],
        PixPayload = "00020101021226BANCOINTERPIX0000000000000000000000000000000000000000",
        PublicUrl = "https://logement.exemplo/boleto/abc",
        Slip = withSlip
            ? new BankSlipDocument
            {
                BankName = "Banco Inter",
                BankCode = "077-9",
                DigitableLine = BoletoLayout.FormatDigitableLine(BoletoLayout.DigitableLine(Barcode)),
                Barcode = Barcode,
                OurNumber = "00012345",
                DocumentNumber = "2609-BLOCOA104",
                BeneficiaryName = "Condomínio Residencial Convivium",
                BeneficiaryDocument = "12345678000195",
                PayerName = "Diego Dias",
                PayerDocument = "52998224725",
                PayerAddress = "Bloco A - 104 — Rua dos Timbiras, 100 - Centro, Belo Horizonte/MG - CEP 30140061",
                Amount = 1188.24m,
                DueDate = DueDate,
                Instructions =
                [
                    "Taxa condominial 09/2026 - Bloco A - 104",
                    "Após o vencimento: multa de 2% e juros de 1% ao mês.",
                ],
            }
            : null,
    };

    /// <summary>
    /// Seis itens — taxa, fundo, agua, gas e dois rateios — e o caso cheio
    /// mais comum, e ainda precisa caber numa folha so com a ficha.
    /// </summary>
    [Fact]
    public void PdfComFichaTrazALinhaDigitavelComoTexto()
    {
        byte[] pdf = new ChargePdfRenderer().Render(Document());

        using PdfDocument parsed = PdfDocument.Open(pdf);
        string text = Flat(string.Join(" ", parsed.GetPages().Select(p => p.Text)));
        string digits = new(text.Where(char.IsAsciiDigit).ToArray());

        parsed.NumberOfPages.ShouldBe(1);
        text.ShouldContain("FichadeCompensação", Case.Insensitive, customMessage: text);
        digits.ShouldContain(BoletoLayout.DigitableLine(Barcode));
        text.ShouldContain("077-9");
    }

    /// <summary>Muitos itens empurram a ficha para a pagina seguinte, mas inteira.</summary>
    [Fact]
    public void FichaNaoECortadaEntrePaginas()
    {
        byte[] pdf = new ChargePdfRenderer().Render(Document(items: 30));

        using PdfDocument parsed = PdfDocument.Open(pdf);
        var pages = parsed.GetPages().Select(p => Flat(p.Text)).ToList();

        pages.Count(p => p.Contains("Recorte", StringComparison.Ordinal)).ShouldBe(1);
        pages.Single(p => p.Contains("Recorte", StringComparison.Ordinal))
            .ShouldContain("FichadeCompensação", Case.Insensitive);
    }

    [Fact]
    public void ImagemDoWhatsAppComFichaSaiInteira()
    {
        byte[] png = new ChargePdfRenderer().RenderImage(Document());

        png.Take(4).ShouldBe(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
    }

    /// <summary>Sem boleto registrado, o documento continua o aviso de antes.</summary>
    [Fact]
    public void SemBoletoContinuaSendoAvisoDeCobranca()
    {
        byte[] pdf = new ChargePdfRenderer().Render(Document(withSlip: false));

        using PdfDocument parsed = PdfDocument.Open(pdf);
        string text = Flat(string.Join(" ", parsed.GetPages().Select(p => p.Text)));

        text.ShouldContain("AVISODECOBRANÇA", Case.Insensitive);
        text.ShouldNotContain("Compensação", Case.Insensitive);
    }
}
