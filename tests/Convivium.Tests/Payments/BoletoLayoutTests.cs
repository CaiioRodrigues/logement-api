namespace Convivium.Tests.Payments;

using System.Globalization;
using Convivium.Domain.Common;
using Convivium.Domain.Payments;

/// <summary>
/// A aritmetica do boleto nao perdoa: um digito trocado e o banco recusa o
/// pagamento, ou — pior — aceita o titulo de outra pessoa.
/// </summary>
/// <remarks>
/// Os dois algoritmos de digito verificador estao reimplementados aqui de
/// proposito, direto do enunciado da Febraban. Se o teste chamasse o mesmo
/// codigo do <see cref="BoletoLayout"/>, um erro no algoritmo passaria nos
/// dois lados e o teste ficaria verde defendendo um boleto quebrado.
/// </remarks>
public class BoletoLayoutTests
{
    /// <summary>Codigo do Banco Inter na camara de compensacao.</summary>
    private const string Inter = "077";

    /// <summary>Um campo livre qualquer de 25 digitos, so para fechar o tamanho.</summary>
    private const string FreeField = "1234567890123456789012345";

    // --- Fator de vencimento ---

    /// <summary>
    /// Os marcos publicos da contagem. 03/07/2000 valendo 1000 e o que fixa o
    /// dia zero em 07/10/1997, e 21/02/2025 e o dia em que o campo de 4 digitos
    /// se esgotou.
    /// </summary>
    [Theory]
    [InlineData(2000, 7, 3, 1000)]
    [InlineData(2000, 7, 4, 1001)]
    [InlineData(2025, 2, 21, 9999)]
    public void FatorDeVencimentoSegueOsMarcosDaFebraban(int year, int month, int day, int expected) =>
        BoletoLayout.DueDateFactor(new DateOnly(year, month, day)).ShouldBe(expected);

    /// <summary>
    /// A virada de 2025: esgotado o 9999, a contagem recomeca em 1000 e segue
    /// dia a dia. Um sistema que nao trate isso emite, de 22/02/2025 em diante,
    /// boleto com a data de vencimento errada dentro do codigo de barras.
    /// </summary>
    [Theory]
    [InlineData(2025, 2, 22, 1000)]
    [InlineData(2025, 2, 23, 1001)]
    [InlineData(2026, 10, 9, 1594)]
    public void FatorDeVencimentoReiniciaEmMilApos21DeFevereiroDe2025(
        int year,
        int month,
        int day,
        int expected) =>
        BoletoLayout.DueDateFactor(new DateOnly(year, month, day)).ShouldBe(expected);

    /// <summary>
    /// Mantida a regra, o campo se esgota de novo em 13/10/2049 — a data que a
    /// propria Febraban anunciou como a proxima virada. Serve de conferencia
    /// independente da aritmetica do ciclo.
    /// </summary>
    [Fact]
    public void ProximoEsgotamentoDoFatorCaiEm2049()
    {
        var turnover = new DateOnly(2049, 10, 13);

        BoletoLayout.DueDateFactor(turnover).ShouldBe(9999);
        BoletoLayout.DueDateFactor(turnover.AddDays(1)).ShouldBe(1000);
    }

    /// <summary>O fator nunca cabe em 4 digitos fora da faixa 1000–9999.</summary>
    [Fact]
    public void FatorDeVencimentoFicaSempreEmQuatroDigitos()
    {
        var start = new DateOnly(2025, 1, 1);

        for (int offset = 0; offset < 365 * 30; offset++)
        {
            BoletoLayout.DueDateFactor(start.AddDays(offset)).ShouldBeInRange(1000, 9999);
        }
    }

    [Fact]
    public void VencimentoAnteriorAContagemNaoCabeNoBoleto() =>
        Should.Throw<DomainException>(() => BoletoLayout.DueDateFactor(new DateOnly(1999, 1, 1)));

    /// <summary>
    /// Lendo de volta, o fator e ambiguo: 1594 e 2002 ou 2026. Resolver pelo
    /// ciclo mais proximo da data de referencia acerta todo boleto em
    /// circulacao, porque os ciclos distam quase 25 anos.
    /// </summary>
    [Fact]
    public void FatorVoltaAVirarDataPeloCicloMaisProximo()
    {
        BoletoLayout.DueDateFromFactor(1594, new DateOnly(2026, 10, 1))
            .ShouldBe(new DateOnly(2026, 10, 9));

        BoletoLayout.DueDateFromFactor(1594, new DateOnly(2002, 1, 1))
            .ShouldBe(new DateOnly(2002, 2, 17));
    }

    /// <summary>Ida e volta em todo vencimento plausivel de uma cobranca.</summary>
    [Fact]
    public void FatorEDataSaoOMesmoDadoNosDoisSentidos()
    {
        var reference = new DateOnly(2026, 10, 9);

        for (int offset = -400; offset <= 400; offset++)
        {
            DateOnly dueDate = reference.AddDays(offset);

            BoletoLayout.DueDateFromFactor(BoletoLayout.DueDateFactor(dueDate), reference)
                .ShouldBe(dueDate);
        }
    }

    // --- Codigo de barras ---

    /// <summary>
    /// O codigo de barras montado tem 44 digitos nas posicoes que a Febraban
    /// definiu: banco, moeda, DV geral, fator, valor em centavos e campo livre.
    /// </summary>
    [Fact]
    public void CodigoDeBarrasMontaOsCamposNasPosicoesCertas()
    {
        string barcode = BoletoLayout.BuildBarcode(Inter, 1153.15m, new DateOnly(2026, 10, 9), FreeField);

        barcode.Length.ShouldBe(44);
        barcode[..3].ShouldBe(Inter);
        barcode[3].ShouldBe('9');
        barcode[4].ShouldBe(Mod11(string.Concat(barcode[..4], barcode[5..])));
        barcode[5..9].ShouldBe("1594");
        barcode[9..19].ShouldBe("0000115315");
        barcode[19..].ShouldBe(FreeField);
    }

    /// <summary>
    /// O valor vai em centavos, inteiros. Meio centavo nao existe no codigo de
    /// barras, e arredondar para baixo faria o condominio receber menos do que
    /// cobrou.
    /// </summary>
    [Theory]
    [InlineData(0.01, "0000000001")]
    [InlineData(1153.15, "0000115315")]
    [InlineData(1153.155, "0000115316")]
    [InlineData(99999999.99, "9999999999")]
    public void ValorVaiEmCentavosArredondadoParaCima(decimal amount, string expected) =>
        BoletoLayout.BuildBarcode(Inter, amount, new DateOnly(2026, 10, 9), FreeField)[9..19]
            .ShouldBe(expected);

    [Fact]
    public void ValorAcimaDoQueOCampoComportaERecusado() =>
        Should.Throw<DomainException>(() =>
            BoletoLayout.BuildBarcode(Inter, 100_000_000m, new DateOnly(2026, 10, 9), FreeField));

    /// <summary>
    /// O DV geral confere com o modulo 11 calculado a parte, em todo valor de
    /// campo livre — inclusive nos casos em que a regra manda usar 1.
    /// </summary>
    [Fact]
    public void DigitoVerificadorGeralFechaPeloModulo11()
    {
        for (int i = 0; i < 500; i++)
        {
            string free = (i * 7_919).ToString("0000000000000000000000000", CultureInfo.InvariantCulture)[^25..];
            string barcode = BoletoLayout.BuildBarcode(Inter, 10m + i, new DateOnly(2026, 10, 9), free);

            barcode[4].ShouldBe(Mod11(string.Concat(barcode[..4], barcode[5..])));
        }
    }

    /// <summary>A Febraban manda usar 1 quando o modulo 11 daria 0, 10 ou 11.</summary>
    [Fact]
    public void DigitoVerificadorGeralNuncaEZeroNemDez()
    {
        for (int i = 0; i < 2_000; i++)
        {
            string free = (i * 104_729).ToString("0000000000000000000000000", CultureInfo.InvariantCulture)[^25..];
            char checkDigit = BoletoLayout.BuildBarcode(Inter, 1m + i, new DateOnly(2026, 10, 9), free)[4];

            checkDigit.ShouldBeInRange('1', '9');
        }
    }

    // --- Linha digitavel ---

    /// <summary>
    /// A linha digitavel reparte o mesmo conteudo em cinco campos: banco e
    /// moeda com o inicio do campo livre, dois pedacos do campo livre, o DV
    /// geral sozinho e, no fim, fator e valor.
    /// </summary>
    [Fact]
    public void LinhaDigitavelRepartreOCodigoDeBarrasEmCincoCampos()
    {
        string barcode = BoletoLayout.BuildBarcode(Inter, 1153.15m, new DateOnly(2026, 10, 9), FreeField);
        string line = BoletoLayout.DigitableLine(barcode);

        line.Length.ShouldBe(47);

        string field1 = "077912345";
        string field2 = "6789012345";
        string field3 = "6789012345";

        line.ShouldBe(
            field1 + Mod10(field1) +
            field2 + Mod10(field2) +
            field3 + Mod10(field3) +
            barcode[4] +
            "15940000115315");
    }

    /// <summary>
    /// Os tres DVs de campo fecham pelo modulo 10 calculado a parte. Eles
    /// existem para o aplicativo recusar na hora quem digitou errado.
    /// </summary>
    [Fact]
    public void DigitosVerificadoresDeCampoFechamPeloModulo10()
    {
        for (int i = 0; i < 500; i++)
        {
            string free = (i * 7_919).ToString("0000000000000000000000000", CultureInfo.InvariantCulture)[^25..];
            string line = BoletoLayout.DigitableLine(
                BoletoLayout.BuildBarcode(Inter, 10m + i, new DateOnly(2026, 10, 9), free));

            line[9].ShouldBe(Mod10(line[..9]));
            line[20].ShouldBe(Mod10(line[10..20]));
            line[31].ShouldBe(Mod10(line[21..31]));
        }
    }

    /// <summary>Ida e volta: a linha digitavel guarda o codigo de barras inteiro.</summary>
    [Fact]
    public void CodigoDeBarrasELinhaDigitavelSaoOMesmoDado()
    {
        for (int i = 0; i < 500; i++)
        {
            string free = (i * 15_485_863).ToString("0000000000000000000000000", CultureInfo.InvariantCulture)[^25..];
            string barcode = BoletoLayout.BuildBarcode(Inter, 1m + (i * 1.37m), new DateOnly(2026, 10, 9), free);

            BoletoLayout.BarcodeFromDigitableLine(BoletoLayout.DigitableLine(barcode)).ShouldBe(barcode);
        }
    }

    [Fact]
    public void LinhaDigitavelImpressaSaiPontuadaEmCincoBlocos()
    {
        string barcode = BoletoLayout.BuildBarcode(Inter, 1153.15m, new DateOnly(2026, 10, 9), FreeField);
        string formatted = BoletoLayout.FormatDigitableLine(BoletoLayout.DigitableLine(barcode));

        formatted.ShouldMatch(@"^\d{5}\.\d{5} \d{5}\.\d{6} \d{5}\.\d{6} \d \d{14}$");

        new string(formatted.Where(char.IsAsciiDigit).ToArray())
            .ShouldBe(BoletoLayout.DigitableLine(barcode));
    }

    // --- Leitura e conferencia ---

    /// <summary>
    /// Ler aceita os dois formatos, com ou sem pontuacao, e devolve o valor e o
    /// vencimento que estao de fato dentro do codigo — nao o que o papel diz.
    /// </summary>
    [Fact]
    public void LeituraDevolveValorEVencimentoGravadosNoCodigo()
    {
        var dueDate = new DateOnly(2026, 10, 9);
        var reference = new DateOnly(2026, 10, 1);
        string barcode = BoletoLayout.BuildBarcode(Inter, 1153.15m, dueDate, FreeField);
        string line = BoletoLayout.DigitableLine(barcode);

        foreach (string value in new[] { barcode, line, BoletoLayout.FormatDigitableLine(line) })
        {
            BankSlip slip = BoletoLayout.Parse(value, reference);

            slip.Barcode.ShouldBe(barcode);
            slip.DigitableLine.ShouldBe(line);
            slip.BankCode.ShouldBe(Inter);
            slip.Amount.ShouldBe(1153.15m);
            slip.DueDate.ShouldBe(dueDate);
            slip.FreeField.ShouldBe(FreeField);
        }
    }

    /// <summary>
    /// Um digito trocado tem de ser recusado. E para isso que os verificadores
    /// existem: um boleto corrompido manda o morador pagar o valor errado.
    /// </summary>
    [Fact]
    public void UmDigitoTrocadoInvalidaOCodigo()
    {
        string barcode = BoletoLayout.BuildBarcode(Inter, 1153.15m, new DateOnly(2026, 10, 9), FreeField);
        var reference = new DateOnly(2026, 10, 1);

        int rejected = 0;

        for (int position = 0; position < barcode.Length; position++)
        {
            char[] tampered = barcode.ToCharArray();
            tampered[position] = tampered[position] == '9' ? '8' : (char)(tampered[position] + 1);

            if (BoletoLayout.TryParse(new string(tampered), reference) is null)
            {
                rejected++;
            }
        }

        // O modulo 11 pega a troca de um digito em qualquer posicao.
        rejected.ShouldBe(barcode.Length);
    }

    [Fact]
    public void LinhaDigitavelComDigitoDeCampoErradoERecusada()
    {
        string line = BoletoLayout.DigitableLine(
            BoletoLayout.BuildBarcode(Inter, 1153.15m, new DateOnly(2026, 10, 9), FreeField));

        char[] tampered = line.ToCharArray();
        tampered[9] = tampered[9] == '9' ? '8' : (char)(tampered[9] + 1);

        Should.Throw<DomainException>(() =>
            BoletoLayout.BarcodeFromDigitableLine(new string(tampered)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("sem dígito nenhum")]
    public void TextoQueNaoEBoletoNaoViraBoleto(string value) =>
        BoletoLayout.TryParse(value, new DateOnly(2026, 10, 9)).ShouldBeNull();

    /// <summary>Boleto em outra moeda nao e boleto brasileiro de cobranca.</summary>
    [Fact]
    public void CodigoDeBarrasForaDoRealERecusado()
    {
        string barcode = BoletoLayout.BuildBarcode(Inter, 1153.15m, new DateOnly(2026, 10, 9), FreeField);
        char[] tampered = barcode.ToCharArray();
        tampered[3] = '8';

        Should.Throw<DomainException>(() =>
            BoletoLayout.Parse(new string(tampered), new DateOnly(2026, 10, 9)));
    }

    // --- Codigo do banco ---

    /// <summary>
    /// Os codigos impressos no cabecalho de qualquer boleto em circulacao: se
    /// a regra estivesse errada, algum destes sairia diferente do papel.
    /// </summary>
    [Theory]
    [InlineData("001", "001-9")]
    [InlineData("033", "033-7")]
    [InlineData("077", "077-9")]
    [InlineData("104", "104-0")]
    [InlineData("237", "237-2")]
    [InlineData("341", "341-7")]
    [InlineData("756", "756-0")]
    public void CodigoDoBancoSaiComODigitoDoCabecalho(string bank, string expected) =>
        BoletoLayout.BankCodeWithCheckDigit(bank).ShouldBe(expected);

    // --- Os dois algoritmos, reescritos do enunciado da Febraban ---

    /// <summary>Modulo 11, pesos 2 a 9 ciclicos da direita para a esquerda; 0, 10 e 11 viram 1.</summary>
    private static char Mod11(string digits)
    {
        int sum = 0;
        int weight = 2;

        foreach (char digit in digits.Reverse())
        {
            sum += (digit - '0') * weight;
            weight = weight == 9 ? 2 : weight + 1;
        }

        int result = 11 - (sum % 11);

        return (char)('0' + (result is 0 or 10 or 11 ? 1 : result));
    }

    /// <summary>Modulo 10, pesos 2 e 1 alternados da direita; produto acima de 9 soma os algarismos.</summary>
    private static char Mod10(string digits)
    {
        int sum = 0;
        int weight = 2;

        foreach (char digit in digits.Reverse())
        {
            int product = (digit - '0') * weight;
            sum += product >= 10 ? (product / 10) + (product % 10) : product;
            weight = weight == 2 ? 1 : 2;
        }

        int result = 10 - (sum % 10);

        return (char)('0' + (result == 10 ? 0 : result));
    }
}
