namespace Convivium.Infrastructure.Documents;

using System.Globalization;
using System.Text;

/// <summary>
/// Desenha o codigo de barras do boleto: Interleaved 2 of 5 (ITF), o padrao
/// que a Febraban adotou para os 44 digitos.
/// </summary>
/// <remarks>
/// <para>
/// Cada digito vira cinco elementos, dois largos e tres finos. Os digitos
/// andam em pares: o primeiro do par vira as barras, o segundo os espacos
/// entre elas — dai o "intercalado". Por isso a quantidade de digitos
/// precisa ser par.
/// </para>
/// <para>
/// Elemento largo vale tres finos. Com 44 digitos sao 405 modulos: 4 da
/// guarda inicial, 18 por par e 5 da guarda final. Na largura padrao de
/// 103 mm, cada modulo fica com 0,254 mm, que e o que as leitoras dos
/// caixas esperam.
/// </para>
/// </remarks>
public static class Interleaved2Of5
{
    public const int Narrow = 1;
    public const int Wide = 3;

    /// <summary>Elementos de cada digito: n = fino, w = largo.</summary>
    private static readonly string[] Patterns =
    [
        "nnwwn", // 0
        "wnnnw", // 1
        "nwnnw", // 2
        "wwnnn", // 3
        "nnwnw", // 4
        "wnwnn", // 5
        "nwwnn", // 6
        "nnnww", // 7
        "wnnwn", // 8
        "nwnwn", // 9
    ];

    /// <summary>
    /// Larguras dos elementos em modulos, alternando barra e espaco, sempre
    /// comecando por barra.
    /// </summary>
    public static IReadOnlyList<int> Widths(string digits)
    {
        ArgumentException.ThrowIfNullOrEmpty(digits);

        if (digits.Length % 2 != 0 || !digits.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("O ITF codifica uma quantidade par de algarismos.", nameof(digits));
        }

        // Guarda inicial: barra, espaco, barra, espaco, todos finos.
        var widths = new List<int>(4 + (digits.Length * 5) + 3) { Narrow, Narrow, Narrow, Narrow };

        for (int i = 0; i < digits.Length; i += 2)
        {
            string bars = Patterns[digits[i] - '0'];
            string spaces = Patterns[digits[i + 1] - '0'];

            for (int element = 0; element < 5; element++)
            {
                widths.Add(bars[element] == 'w' ? Wide : Narrow);
                widths.Add(spaces[element] == 'w' ? Wide : Narrow);
            }
        }

        // Guarda final: barra larga, espaco fino, barra fina.
        widths.AddRange([Wide, Narrow, Narrow]);

        return widths;
    }

    /// <summary>
    /// SVG com uma unidade por modulo. Quem desenha escala para 103 mm de
    /// largura; as proporcoes entre barras ficam exatas em qualquer tamanho.
    /// </summary>
    public static string Svg(string digits, int height = 50)
    {
        IReadOnlyList<int> widths = Widths(digits);
        int total = widths.Sum();

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture,
            $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {total} {height}" preserveAspectRatio="none" shape-rendering="crispEdges">""");

        int x = 0;

        for (int i = 0; i < widths.Count; i++)
        {
            if (i % 2 == 0)
            {
                svg.Append(CultureInfo.InvariantCulture,
                    $"""<rect x="{x}" y="0" width="{widths[i]}" height="{height}" fill="#000"/>""");
            }

            x += widths[i];
        }

        svg.Append("</svg>");

        return svg.ToString();
    }
}
