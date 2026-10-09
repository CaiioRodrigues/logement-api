namespace Convivium.Domain.Payments;

using System.Globalization;
using Convivium.Domain.Common;

/// <summary>
/// A aritmetica do boleto bancario brasileiro: codigo de barras de 44 digitos,
/// linha digitavel de 47 e os digitos verificadores que ligam os dois.
/// </summary>
/// <remarks>
/// <para>
/// O layout e o mesmo em todo banco, definido pela Febraban. O codigo de barras
/// guarda, em ordem: banco (3), moeda (1), DV geral (1), fator de vencimento (4),
/// valor em centavos (10) e campo livre (25). Quem decide o campo livre e o banco
/// emissor — e a unica parte que muda de banco para banco.
/// </para>
/// <para>
/// Nada aqui emite boleto. Isto le, confere e remonta o que um banco devolveu:
/// se a conta nao fecha, o documento esta corrompido e o morador pagaria o valor
/// errado — ou pagaria certo numa conta errada. Por isso toda conversao refaz os
/// digitos verificadores em vez de confiar nos que vieram.
/// </para>
/// </remarks>
public static class BoletoLayout
{
    /// <summary>Data zero da contagem do fator de vencimento.</summary>
    /// <remarks>
    /// Combinada pela Febraban: 03/07/2000 e o fator 1000, o que coloca o dia
    /// zero em 07/10/1997. O campo tem 4 digitos, entao a contagem se esgotou
    /// no fator 9999, em 21/02/2025.
    /// </remarks>
    public static readonly DateOnly FactorEpoch = new(1997, 10, 7);

    /// <summary>Primeiro fator do ciclo apos a virada: 1000.</summary>
    public const int FactorCycleStart = 1000;

    /// <summary>Ultimo fator possivel: o campo tem 4 digitos.</summary>
    public const int FactorCycleEnd = 9999;

    /// <summary>Tamanho do ciclo de reinicio: de 1000 a 9999 sao 9000 dias.</summary>
    public const int FactorCycleLength = FactorCycleEnd - FactorCycleStart + 1;

    public const int BarcodeLength = 44;

    public const int DigitableLineLength = 47;

    /// <summary>Codigo da moeda real no boleto.</summary>
    public const char CurrencyReal = '9';

    private const int FreeFieldLength = 25;

    private const decimal MaxAmount = 99_999_999.99m;

    // --- Fator de vencimento ---

    /// <summary>
    /// Quantos dias separam o vencimento da data zero, em 4 digitos.
    /// </summary>
    /// <remarks>
    /// Passado o fator 9999, em 21/02/2025, a Febraban determinou reiniciar a
    /// contagem em 1000 e seguir dia a dia — e nao parar nem estourar o campo.
    /// Entao 22/02/2025 vale 1000 outra vez, e a proxima virada cai em 2049.
    /// Sem este reinicio, todo boleto emitido de 2025 em diante sairia com a
    /// data de vencimento errada no codigo de barras.
    /// </remarks>
    public static int DueDateFactor(DateOnly dueDate)
    {
        int days = dueDate.DayNumber - FactorEpoch.DayNumber;

        DomainException.ThrowIf(
            days < FactorCycleStart,
            "Vencimento anterior a 03/07/2000 não cabe no fator de vencimento do boleto.");

        return days <= FactorCycleEnd
            ? days
            : ((days - FactorCycleStart) % FactorCycleLength) + FactorCycleStart;
    }

    /// <summary>
    /// A data de vencimento que o fator representa, resolvida pelo ciclo mais
    /// proximo de <paramref name="reference"/>.
    /// </summary>
    /// <remarks>
    /// O fator sozinho e ambiguo depois da virada de 2025: 1594 pode ser
    /// 17/02/2002 ou 09/10/2026. Como os ciclos distam quase 25 anos, escolher
    /// o candidato mais perto de hoje acerta qualquer boleto em circulacao.
    /// </remarks>
    public static DateOnly DueDateFromFactor(int factor, DateOnly reference)
    {
        DomainException.ThrowIf(
            factor is < FactorCycleStart or > FactorCycleEnd,
            $"Fator de vencimento fora da faixa {FactorCycleStart}–{FactorCycleEnd}: {factor}.");

        int referenceDays = reference.DayNumber - FactorEpoch.DayNumber;
        int best = factor;

        for (int days = factor; days <= referenceDays + FactorCycleLength; days += FactorCycleLength)
        {
            if (Math.Abs(days - referenceDays) < Math.Abs(best - referenceDays))
            {
                best = days;
            }
        }

        return FactorEpoch.AddDays(best);
    }

    // --- Codigo de barras ---

    /// <summary>
    /// Monta o codigo de barras de 44 digitos a partir das suas partes.
    /// </summary>
    /// <param name="bankCode">Codigo do banco na camara de compensacao. Ex.: "077" para o Inter.</param>
    /// <param name="amount">Valor do titulo. Vai para o codigo em centavos.</param>
    /// <param name="dueDate">Vencimento, que viaja como fator de vencimento.</param>
    /// <param name="freeField">Campo livre de 25 digitos, no layout do banco emissor.</param>
    public static string BuildBarcode(string bankCode, decimal amount, DateOnly dueDate, string freeField)
    {
        string bank = Digits(bankCode, nameof(bankCode));
        string free = Digits(freeField, nameof(freeField));

        DomainException.ThrowIf(bank.Length != 3, "O código do banco tem 3 dígitos.");
        DomainException.ThrowIf(free.Length != FreeFieldLength, $"O campo livre tem {FreeFieldLength} dígitos.");
        DomainException.ThrowIf(amount < 0m, "Valor do boleto negativo.");
        DomainException.ThrowIf(amount > MaxAmount, $"O boleto não comporta valores acima de {MaxAmount:N2}.");

        long cents = decimal.ToInt64(decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));

        string withoutCheckDigit =
            bank +
            CurrencyReal +
            DueDateFactor(dueDate).ToString("0000", CultureInfo.InvariantCulture) +
            cents.ToString("0000000000", CultureInfo.InvariantCulture) +
            free;

        return string.Concat(withoutCheckDigit[..4], BarcodeCheckDigit(withoutCheckDigit), withoutCheckDigit[4..]);
    }

    /// <summary>
    /// O DV geral, que mora na 5a posicao do codigo de barras.
    /// </summary>
    /// <param name="barcodeWithoutCheckDigit">Os 43 digitos do codigo de barras sem o DV.</param>
    /// <remarks>
    /// Modulo 11 com pesos de 2 a 9 ciclicos da direita para a esquerda. A
    /// Febraban manda usar 1 quando o resultado daria 0, 10 ou 11 — e por isso
    /// que o DV geral nunca e zero.
    /// </remarks>
    public static char BarcodeCheckDigit(string barcodeWithoutCheckDigit)
    {
        string digits = Digits(barcodeWithoutCheckDigit, nameof(barcodeWithoutCheckDigit));

        DomainException.ThrowIf(
            digits.Length != BarcodeLength - 1,
            $"O cálculo do DV geral precisa dos {BarcodeLength - 1} dígitos restantes do código de barras.");

        int sum = 0;
        int weight = 2;

        for (int i = digits.Length - 1; i >= 0; i--)
        {
            sum += (digits[i] - '0') * weight;
            weight = weight == 9 ? 2 : weight + 1;
        }

        int checkDigit = 11 - (sum % 11);

        return (char)('0' + (checkDigit > 9 ? 1 : checkDigit));
    }

    // --- Linha digitavel ---

    /// <summary>
    /// Converte o codigo de barras na linha digitavel de 47 digitos.
    /// </summary>
    /// <remarks>
    /// A linha digitavel nao e o codigo de barras embaralhado por capricho: ela
    /// reparte o mesmo conteudo em cinco campos e da a cada um dos tres
    /// primeiros um DV de modulo 10. Assim o caixa que digita errado um digito
    /// recebe a recusa na hora, em vez de pagar o titulo de outra pessoa.
    /// </remarks>
    public static string DigitableLine(string barcode)
    {
        string code = NormalizeBarcode(barcode);

        string bankAndCurrency = code[..4];
        string generalCheckDigit = code[4..5];
        string factorAndAmount = code[5..19];
        string freeField = code[19..];

        string field1 = bankAndCurrency + freeField[..5];
        string field2 = freeField[5..15];
        string field3 = freeField[15..];

        return
            field1 + Mod10CheckDigit(field1) +
            field2 + Mod10CheckDigit(field2) +
            field3 + Mod10CheckDigit(field3) +
            generalCheckDigit +
            factorAndAmount;
    }

    /// <summary>
    /// O caminho inverso: remonta o codigo de barras a partir da linha digitavel.
    /// </summary>
    /// <remarks>
    /// Serve para quando o banco devolve so a linha digitavel — o codigo de
    /// barras ainda e necessario para desenhar as barras na ficha de compensacao.
    /// </remarks>
    public static string BarcodeFromDigitableLine(string digitableLine)
    {
        string line = NormalizeDigitableLine(digitableLine);

        string barcode =
            line[..4] +
            line[32..33] +
            line[33..] +
            line[4..9] +
            line[10..20] +
            line[21..31];

        DomainException.ThrowIf(
            barcode[4] != BarcodeCheckDigit(string.Concat(barcode[..4], barcode[5..])),
            "Linha digitável inválida: o dígito verificador geral não fecha.");

        return barcode;
    }

    /// <summary>
    /// A linha digitavel como ela aparece impressa, com pontos e espacos.
    /// </summary>
    /// <example>00190.00009 01234.567004 56789.012345 6 78900000012345</example>
    public static string FormatDigitableLine(string digitableLine)
    {
        string line = NormalizeDigitableLine(digitableLine);

        return string.Join(
            ' ',
            $"{line[..5]}.{line[5..10]}",
            $"{line[10..15]}.{line[15..21]}",
            $"{line[21..26]}.{line[26..32]}",
            line[32..33],
            line[33..]);
    }

    // --- Leitura ---

    /// <summary>
    /// Le um boleto a partir do codigo de barras ou da linha digitavel, de
    /// qualquer um dos dois jeitos, e confere todos os digitos verificadores.
    /// </summary>
    /// <param name="value">44 ou 47 digitos, com ou sem pontuacao.</param>
    /// <param name="reference">Data usada para desfazer a ambiguidade do fator de vencimento.</param>
    public static BankSlip Parse(string value, DateOnly reference)
    {
        string digits = Digits(value, nameof(value));

        string barcode = digits.Length switch
        {
            BarcodeLength => NormalizeBarcode(digits),
            DigitableLineLength => BarcodeFromDigitableLine(digits),
            _ => throw new DomainException(
                $"Um boleto tem {BarcodeLength} dígitos no código de barras ou " +
                $"{DigitableLineLength} na linha digitável; recebi {digits.Length}."),
        };

        int factor = int.Parse(barcode[5..9], CultureInfo.InvariantCulture);
        long cents = long.Parse(barcode[9..19], CultureInfo.InvariantCulture);

        return new BankSlip
        {
            Barcode = barcode,
            DigitableLine = DigitableLine(barcode),
            BankCode = barcode[..3],
            DueDate = DueDateFromFactor(factor, reference),
            Amount = cents / 100m,
            FreeField = barcode[19..],
        };
    }

    /// <summary>Le sem lancar: devolve nulo quando o texto nao e um boleto valido.</summary>
    public static BankSlip? TryParse(string? value, DateOnly reference)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Parse(value, reference);
        }
        catch (DomainException)
        {
            return null;
        }
    }

    // --- Apoio ---

    /// <summary>
    /// Modulo 10 da Febraban: pesos 2 e 1 alternados da direita para a
    /// esquerda, somando os algarismos de cada produto que passa de 9.
    /// </summary>
    private static char Mod10CheckDigit(string field)
    {
        int sum = 0;
        int weight = 2;

        for (int i = field.Length - 1; i >= 0; i--)
        {
            int product = (field[i] - '0') * weight;
            sum += product > 9 ? product - 9 : product;
            weight = weight == 2 ? 1 : 2;
        }

        int checkDigit = (10 - (sum % 10)) % 10;

        return (char)('0' + checkDigit);
    }

    private static string NormalizeBarcode(string barcode)
    {
        string code = Digits(barcode, nameof(barcode));

        DomainException.ThrowIf(
            code.Length != BarcodeLength,
            $"O código de barras do boleto tem {BarcodeLength} dígitos; recebi {code.Length}.");

        DomainException.ThrowIf(
            code[3] != CurrencyReal,
            "Código de barras inválido: o boleto não está em reais.");

        DomainException.ThrowIf(
            code[4] != BarcodeCheckDigit(string.Concat(code[..4], code[5..])),
            "Código de barras inválido: o dígito verificador geral não fecha.");

        return code;
    }

    private static string NormalizeDigitableLine(string digitableLine)
    {
        string line = Digits(digitableLine, nameof(digitableLine));

        DomainException.ThrowIf(
            line.Length != DigitableLineLength,
            $"A linha digitável tem {DigitableLineLength} dígitos; recebi {line.Length}.");

        // Os tres primeiros campos da linha digitavel, cada um com o seu DV
        // logo depois: 9 digitos, 10 e 10.
        (int Start, int Length)[] fields = [(0, 9), (10, 10), (21, 10)];

        foreach ((int start, int length) in fields)
        {
            DomainException.ThrowIf(
                line[start + length] != Mod10CheckDigit(line.Substring(start, length)),
                "Linha digitável inválida: um dos dígitos verificadores de campo não fecha.");
        }

        return line;
    }

    /// <summary>Guarda so os algarismos, para aceitar o valor pontuado ou cru.</summary>
    private static string Digits(string? value, string parameterName)
    {
        DomainException.ThrowIf(string.IsNullOrWhiteSpace(value), $"Informe {parameterName}.");

        string digits = new(value.Where(char.IsAsciiDigit).ToArray());

        DomainException.ThrowIf(digits.Length == 0, $"Sem dígitos em {parameterName}.");

        return digits;
    }
}
