namespace Convivium.Tests.Documents;

using Convivium.Domain.Payments;
using Convivium.Infrastructure.Documents;

/// <summary>
/// As barras que a leitora do caixa le. O teste decodifica o desenho de
/// volta para algarismos com uma tabela propria: se o codificador trocasse
/// um padrao, o boleto sairia bonito e ilegivel.
/// </summary>
public class Interleaved2Of5Tests
{
    /// <summary>Tabela ITF reescrita aqui, independente da do codificador.</summary>
    private static readonly Dictionary<string, char> Decode = new()
    {
        ["11331"] = '0', ["31113"] = '1', ["13113"] = '2', ["33111"] = '3', ["11313"] = '4',
        ["31311"] = '5', ["13311"] = '6', ["11133"] = '7', ["31131"] = '8', ["13131"] = '9',
    };

    private static readonly string Barcode =
        BoletoLayout.BuildBarcode("077", 1188.24m, new DateOnly(2026, 10, 20), "0000000000000000000012345");

    /// <summary>
    /// 44 digitos dao 405 modulos: com 103 mm de largura, cada modulo fica
    /// com 0,254 mm — a medida que as leitoras dos caixas esperam.
    /// </summary>
    [Fact]
    public void CodigoDoBoletoTem405Modulos()
    {
        IReadOnlyList<int> widths = Interleaved2Of5.Widths(Barcode);

        widths.Sum().ShouldBe(405);
        widths.Count.ShouldBe(4 + (44 * 5) + 3);
    }

    [Fact]
    public void GuardasDeInicioEFim()
    {
        IReadOnlyList<int> widths = Interleaved2Of5.Widths(Barcode);

        widths.Take(4).ShouldBe([1, 1, 1, 1]);
        widths.TakeLast(3).ShouldBe([3, 1, 1]);
    }

    /// <summary>Barras carregam o primeiro digito do par; espacos, o segundo.</summary>
    [Fact]
    public void DesenhoVoltaAosMesmosAlgarismos()
    {
        IReadOnlyList<int> widths = Interleaved2Of5.Widths(Barcode);
        var digits = new System.Text.StringBuilder();

        for (int start = 4; start < widths.Count - 3; start += 10)
        {
            string bars = string.Concat(Enumerable.Range(0, 5).Select(k => widths[start + (2 * k)]));
            string spaces = string.Concat(Enumerable.Range(0, 5).Select(k => widths[start + (2 * k) + 1]));

            digits.Append(Decode[bars]).Append(Decode[spaces]);
        }

        digits.ToString().ShouldBe(Barcode);
    }

    [Fact]
    public void CadaDigitoTemDoisElementosLargos()
    {
        IReadOnlyList<int> widths = Interleaved2Of5.Widths("0123456789");

        for (int start = 4; start < widths.Count - 3; start += 10)
        {
            Enumerable.Range(0, 5).Count(k => widths[start + (2 * k)] == 3).ShouldBe(2);
            Enumerable.Range(0, 5).Count(k => widths[start + (2 * k) + 1] == 3).ShouldBe(2);
        }
    }

    [Theory]
    [InlineData("123")]
    [InlineData("12a4")]
    public void SoAceitaQuantidadeParDeAlgarismos(string digits) =>
        Should.Throw<ArgumentException>(() => Interleaved2Of5.Widths(digits));

    [Fact]
    public void SvgDesenhaUmaBarraPorElementoPar()
    {
        string svg = Interleaved2Of5.Svg(Barcode);

        svg.ShouldContain("viewBox=\"0 0 405 50\"");
        System.Text.RegularExpressions.Regex.Count(svg, "<rect ").ShouldBe((227 + 1) / 2);
    }
}
