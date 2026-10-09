namespace Convivium.Infrastructure.Banking;

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Convivium.Application.Abstractions;
using Convivium.Application.Banking;
using Convivium.Domain.Common;
using Convivium.Domain.Payments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BoletoProviderKind = Convivium.Domain.Condominiums.BoletoProvider;

/// <summary>
/// Emite boletos pela API de cobrança v3 do Banco Inter.
/// </summary>
/// <remarks>
/// <para>
/// O fluxo do Inter é assíncrono: a emissão devolve um <c>codigoSolicitacao</c>
/// e só depois o título ganha código de barras. Por isso emitir e consultar
/// são passos separados, e uma cobrança recém-emitida fica em
/// <see cref="BoletoState.Pending"/> por alguns segundos.
/// </para>
/// <para>
/// Tudo que vem do banco passa por <see cref="BoletoLayout"/> antes de ser
/// aceito, e o valor e o vencimento gravados no código de barras são
/// conferidos contra o que foi pedido. Um título com o valor errado seria pago
/// errado, e a diferença só apareceria na conciliação do mês seguinte.
/// </para>
/// </remarks>
public sealed class InterBoletoProvider(
    IBankHttpClientSource pool,
    IOptions<InterOptions> options,
    IClock clock,
    ILogger<InterBoletoProvider> logger) : IBoletoProvider
{
    private const string CobrancaPath = "cobranca/v3/cobrancas";

    /// <summary>Margem para nao usar um token que expira no meio da chamada.</summary>
    private static readonly TimeSpan TokenSafetyMargin = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, CachedToken> tokens = new();

    public BoletoProviderKind Provider => BoletoProviderKind.Inter;

    public async Task VerifyAsync(
        BankCredentials credentials,
        CancellationToken cancellationToken = default) =>
        await GetTokenAsync(credentials, forceRefresh: true, cancellationToken);

    public async Task<BoletoRegistration> IssueAsync(
        BoletoIssueRequest request,
        BankCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using JsonDocument response = await SendAsync(
            HttpMethod.Post,
            CobrancaPath,
            credentials,
            JsonContent.Create(BuildEmissionBody(request)),
            cancellationToken,
            createsTitle: true);

        string externalId = FindString(response.RootElement, "codigoSolicitacao")
            ?? throw new BoletoProviderException(
                "O Inter aceitou a emissão mas não devolveu o código da solicitação, " +
                "então não há como acompanhar este boleto. Consulte a cobrança no " +
                "internet banking antes de emitir de novo, para não duplicar o título.");

        logger.LogInformation(
            "Boleto {OwnNumber} solicitado ao Inter: {ExternalId}.",
            request.OwnNumber,
            externalId);

        // A emissao e assincrona; a primeira consulta costuma ja trazer o
        // codigo de barras, e quando nao traz o titulo fica pendente.
        return await QueryAsync(externalId, credentials, request, cancellationToken);
    }

    public Task<BoletoRegistration> QueryAsync(
        string externalId,
        BankCredentials credentials,
        CancellationToken cancellationToken = default) =>
        QueryAsync(externalId, credentials, expected: null, cancellationToken);

    public async Task CancelAsync(
        string externalId,
        string reason,
        BankCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        using JsonDocument _ = await SendAsync(
            HttpMethod.Post,
            $"{CobrancaPath}/{Uri.EscapeDataString(externalId)}/cancelar",
            credentials,
            JsonContent.Create(new { motivoCancelamento = CancellationReason(reason) }),
            cancellationToken);

        logger.LogInformation("Boleto {ExternalId} cancelado no Inter.", externalId);
    }

    public async Task<byte[]> DownloadPdfAsync(
        string externalId,
        BankCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        using JsonDocument response = await SendAsync(
            HttpMethod.Get,
            $"{CobrancaPath}/{Uri.EscapeDataString(externalId)}/pdf",
            credentials,
            content: null,
            cancellationToken);

        string? base64 = FindString(response.RootElement, "pdf");

        if (string.IsNullOrWhiteSpace(base64))
        {
            throw new BoletoProviderException("O Inter não devolveu o PDF deste boleto.");
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException exception)
        {
            throw new BoletoProviderException("O PDF devolvido pelo Inter veio corrompido.", exception);
        }
    }

    // --- Consulta e leitura do titulo ---

    private async Task<BoletoRegistration> QueryAsync(
        string externalId,
        BankCredentials credentials,
        BoletoIssueRequest? expected,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        using JsonDocument response = await SendAsync(
            HttpMethod.Get,
            $"{CobrancaPath}/{Uri.EscapeDataString(externalId)}",
            credentials,
            content: null,
            cancellationToken);

        return ReadRegistration(externalId, response.RootElement, expected);
    }

    private BoletoRegistration ReadRegistration(
        string externalId,
        JsonElement root,
        BoletoIssueRequest? expected)
    {
        string? situation = FindString(root, "cobranca.situacao", "situacao");
        BoletoState state = MapState(situation);

        string? digitableLine = FindString(root, "boleto.linhaDigitavel", "linhaDigitavel");
        string? barcode = FindString(root, "boleto.codigoBarras", "codigoBarras");

        BankSlip? slip = ReadSlip(barcode ?? digitableLine, expected, situation);

        // O banco pode dizer "a receber" antes de publicar o codigo de barras.
        // Sem codigo de barras nao ha boleto pagavel, entao o estado real ainda
        // e pendente — e a tela precisa dizer isso ao sindico.
        if (slip is null && state is BoletoState.Registered)
        {
            state = BoletoState.Pending;
        }

        return new BoletoRegistration
        {
            ExternalId = externalId,
            State = state,
            Slip = slip,
            OurNumber = FindString(root, "boleto.nossoNumero", "nossoNumero"),
            PixPayload = FindString(root, "pix.pixCopiaECola", "pixCopiaECola"),
            PaidAmount = FindDecimal(root, "cobranca.valorTotalRecebido", "valorTotalRecebido"),
            PaidOn = FindDate(root, "cobranca.dataSituacao", "dataSituacao") is { } date
                && state is BoletoState.Paid
                ? date
                : null,
        };
    }

    /// <summary>
    /// Confere digito a digito o que o banco devolveu e recusa o que nao bate
    /// com o que foi pedido.
    /// </summary>
    private BankSlip? ReadSlip(string? value, BoletoIssueRequest? expected, string? situation)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        BankSlip slip;

        try
        {
            slip = BoletoLayout.Parse(value, clock.Today);
        }
        catch (DomainException exception)
        {
            throw new BoletoProviderException(
                $"O código de barras devolvido pelo Inter não é válido: {exception.Message} " +
                "O boleto não foi aceito aqui para não ser cobrado errado.",
                exception);
        }

        if (expected is null)
        {
            return slip;
        }

        if (slip.Amount != decimal.Round(expected.Amount, 2, MidpointRounding.AwayFromZero))
        {
            throw new BoletoProviderException(
                $"O boleto registrado veio com valor de R$ {slip.Amount:N2}, e a cobrança " +
                $"é de R$ {expected.Amount:N2}. O título não foi aproveitado; " +
                $"cancele-o no internet banking antes de emitir de novo. ({situation})");
        }

        if (slip.DueDate != expected.DueDate)
        {
            throw new BoletoProviderException(
                $"O boleto registrado venceu em {slip.DueDate:dd/MM/yyyy}, e a cobrança " +
                $"vence em {expected.DueDate:dd/MM/yyyy}. O título não foi aproveitado; " +
                $"cancele-o no internet banking antes de emitir de novo. ({situation})");
        }

        return slip;
    }

    /// <summary>
    /// Traduz a situacao do Inter. Um valor desconhecido vira pendente e sai no
    /// log: tratar o que nao se entende como "pago" ou "cancelado" seria pior
    /// do que admitir que ainda nao se sabe.
    /// </summary>
    private BoletoState MapState(string? situation)
    {
        switch (situation?.Trim().ToUpperInvariant())
        {
            case "EM_PROCESSAMENTO":
                return BoletoState.Pending;

            case "A_RECEBER":
            case "ATRASADO":
                return BoletoState.Registered;

            case "RECEBIDO":
            case "MARCADO_RECEBIDO":
                return BoletoState.Paid;

            case "CANCELADO":
            case "FALHA_EMISSAO":
                return BoletoState.Cancelled;

            case "EXPIRADO":
                return BoletoState.Expired;

            default:
                logger.LogWarning(
                    "Situação de boleto não reconhecida no retorno do Inter: {Situation}.",
                    situation ?? "(ausente)");

                return BoletoState.Pending;
        }
    }

    // --- Montagem do pedido ---

    private static object BuildEmissionBody(BoletoIssueRequest request)
    {
        BoletoPayer payer = request.Payer;
        string document = Digits(payer.Document);

        DomainException.ThrowIf(
            document.Length is not (11 or 14),
            $"O pagador {payer.Name} está sem CPF ou CNPJ. O banco não registra " +
            "boleto sem o documento de quem paga.");

        var body = new Dictionary<string, object?>
        {
            ["seuNumero"] = request.OwnNumber,
            ["valorNominal"] = decimal.Round(request.Amount, 2, MidpointRounding.AwayFromZero),
            ["dataVencimento"] = request.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["numDiasAgenda"] = Math.Clamp(request.DaysOnAgenda, 0, 60),
            ["pagador"] = new Dictionary<string, object?>
            {
                ["cpfCnpj"] = document,
                ["tipoPessoa"] = document.Length == 11 ? "FISICA" : "JURIDICA",
                ["nome"] = Trim(payer.Name, 100),
                ["endereco"] = Trim(payer.Street, 90),
                ["numero"] = Trim(payer.Number, 10),
                ["complemento"] = Trim(payer.Complement, 30),
                ["bairro"] = Trim(payer.District, 60),
                ["cidade"] = Trim(payer.City, 60),
                ["uf"] = Trim(payer.State, 2)?.ToUpperInvariant(),
                ["cep"] = Digits(payer.ZipCode),
                ["email"] = Trim(payer.Email, 80),
            },
        };

        if (request.LateFeeRate > 0m)
        {
            body["multa"] = new
            {
                codigo = "PERCENTUAL",
                taxa = decimal.Round(request.LateFeeRate * 100m, 2, MidpointRounding.AwayFromZero),
            };
        }

        if (request.MonthlyInterestRate > 0m)
        {
            body["mora"] = new
            {
                codigo = "TAXAMENSAL",
                taxa = decimal.Round(request.MonthlyInterestRate * 100m, 2, MidpointRounding.AwayFromZero),
            };
        }

        if (request.Instructions.Count > 0)
        {
            // O campo de mensagem do Inter sao cinco linhas nomeadas, nao uma lista.
            var message = new Dictionary<string, object?>();

            for (int i = 0; i < Math.Min(request.Instructions.Count, 5); i++)
            {
                message[$"linha{i + 1}"] = Trim(request.Instructions[i], 78);
            }

            body["mensagem"] = message;
        }

        return body;
    }

    /// <summary>
    /// Motivo do cancelamento, no vocabulario do banco. Qualquer coisa fora da
    /// lista vira "ACERTOS", que e o motivo generico.
    /// </summary>
    private static string CancellationReason(string reason) =>
        reason.Trim().ToUpperInvariant() switch
        {
            "APEDIDODOCLIENTE" or "A_PEDIDO_DO_CLIENTE" => "APEDIDODOCLIENTE",
            "PAGODIRETOAOCLIENTE" or "PAGO_DIRETO_AO_CLIENTE" => "PAGODIRETOAOCLIENTE",
            "SUBSTITUICAO" => "SUBSTITUICAO",
            "FALTADESOLUCAO" or "FALTA_DE_SOLUCAO" => "FALTADESOLUCAO",
            _ => "ACERTOS",
        };

    // --- Transporte ---

    /// <param name="createsTitle">
    /// A chamada registra um titulo. Se ela falhar depois de sair daqui, nao
    /// da para saber se o banco registrou — e o erro precisa dizer isso.
    /// </param>
    private async Task<JsonDocument> SendAsync(
        HttpMethod method,
        string path,
        BankCredentials credentials,
        HttpContent? content,
        CancellationToken cancellationToken,
        bool createsTitle = false)
    {
        HttpResponseMessage response = await SendOnceAsync(
            method, path, credentials, content, forceRefreshToken: false, createsTitle, cancellationToken);

        // Um 401 depois de um token em cache quer dizer token revogado do lado
        // do banco: vale uma segunda tentativa com token novo antes de desistir.
        if (response.StatusCode is HttpStatusCode.Unauthorized && content is null)
        {
            response.Dispose();

            response = await SendOnceAsync(
                method, path, credentials, content: null, forceRefreshToken: true, createsTitle, cancellationToken);
        }

        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Um 5xx numa emissao nao garante que nada foi gravado: o banco
                // pode ter registrado o titulo e falhado so ao responder.
                bool unknown = createsTitle && (int)response.StatusCode >= 500;

                throw new BoletoProviderException(
                    DescribeFailure(response.StatusCode, body),
                    innerException: null,
                    outcomeUnknown: unknown);
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return JsonDocument.Parse("{}");
            }

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException exception)
            {
                throw new BoletoProviderException(
                    "O Inter respondeu algo que não é JSON. Pode ser indisponibilidade " +
                    "momentânea do banco; tente de novo em alguns minutos.",
                    exception);
            }
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method,
        string path,
        BankCredentials credentials,
        HttpContent? content,
        bool forceRefreshToken,
        bool createsTitle,
        CancellationToken cancellationToken)
    {
        string token = await GetTokenAsync(credentials, forceRefreshToken, cancellationToken);

        using var message = new HttpRequestMessage(method, path) { Content = content };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (!string.IsNullOrWhiteSpace(credentials.CheckingAccount))
        {
            message.Headers.Add("x-conta-corrente", Digits(credentials.CheckingAccount));
        }

        try
        {
            return await pool.Get(credentials).SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new BoletoProviderException(
                createsTitle
                    ? "A conexão com o Banco Inter caiu durante a emissão. O boleto pode " +
                      "ter sido registrado: confira no internet banking antes de emitir de novo."
                    : "Não consegui falar com o Banco Inter. Confira a conexão do servidor " +
                      "e se o certificado do condomínio ainda está válido.",
                exception,
                outcomeUnknown: createsTitle);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BoletoProviderException(
                createsTitle
                    ? "O Banco Inter não respondeu a tempo à emissão. O boleto pode ter " +
                      "sido registrado: confira no internet banking antes de emitir de novo."
                    : "O Banco Inter demorou demais para responder. Tente de novo em alguns minutos.",
                exception,
                outcomeUnknown: createsTitle);
        }
    }

    /// <summary>
    /// Traduz a falha do banco para algo que o sindico consiga agir, sem
    /// esconder a mensagem original — que costuma dizer qual campo recusou.
    /// </summary>
    private static string DescribeFailure(HttpStatusCode status, string body)
    {
        string detail = ExtractErrorDetail(body);

        string explanation = status switch
        {
            HttpStatusCode.Unauthorized =>
                "O Inter recusou as credenciais do condomínio. Confira o client id, o " +
                "client secret e se o certificado não venceu.",

            HttpStatusCode.Forbidden =>
                "As credenciais funcionam, mas falta liberar o escopo de cobrança " +
                "(boleto-cobranca.read e boleto-cobranca.write) na aplicação do Inter.",

            HttpStatusCode.NotFound =>
                "O Inter não encontrou este boleto. Ele pode ter sido cancelado " +
                "direto no internet banking.",

            HttpStatusCode.TooManyRequests =>
                "O Inter limitou as chamadas por excesso de requisições. " +
                "Aguarde um minuto e emita o restante do lote.",

            >= HttpStatusCode.InternalServerError =>
                "O Inter está com instabilidade. Nenhuma cobrança foi perdida; " +
                "tente de novo em alguns minutos.",

            _ => "O Inter recusou a operação.",
        };

        return detail.Length == 0 ? explanation : $"{explanation} O banco disse: {detail}";
    }

    private static string ExtractErrorDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);

            string? detail =
                FindString(document.RootElement, "detail", "title", "message", "error_description");

            // Erros de validacao vem numa lista de violacoes com o campo recusado.
            if (document.RootElement.TryGetProperty("violacoes", out JsonElement violations)
                && violations.ValueKind is JsonValueKind.Array)
            {
                string[] reasons = [.. violations
                    .EnumerateArray()
                    .Select(v => $"{FindString(v, "propriedade")}: {FindString(v, "razao")}".Trim(' ', ':'))
                    .Where(r => r.Length > 0)];

                if (reasons.Length > 0)
                {
                    detail = string.Join("; ", [detail ?? string.Empty, .. reasons]).Trim(' ', ';');
                }
            }

            return detail ?? Trim(body, 300) ?? string.Empty;
        }
        catch (JsonException)
        {
            return Trim(body, 300) ?? string.Empty;
        }
    }

    // --- Token ---

    private sealed record CachedToken(string Value, DateTimeOffset ExpiresAt);

    private async Task<string> GetTokenAsync(
        BankCredentials credentials,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.Now;

        string key = TokenKey(credentials);

        if (!forceRefresh
            && tokens.TryGetValue(key, out CachedToken? cached)
            && cached.ExpiresAt > now + TokenSafetyMargin)
        {
            return cached.Value;
        }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = credentials.ClientId,
            ["client_secret"] = credentials.ClientSecret,
            ["scope"] = options.Value.Scope,
            ["grant_type"] = "client_credentials",
        });

        HttpResponseMessage response;

        try
        {
            response = await pool.Get(credentials).PostAsync("oauth/v2/token", form, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new BoletoProviderException(
                "Não consegui falar com o Banco Inter para autenticar. Confira a " +
                "conexão do servidor e a validade do certificado do condomínio.",
                exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BoletoProviderException(
                "O Banco Inter demorou demais para autenticar. Tente de novo em alguns minutos.",
                exception);
        }

        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new BoletoProviderException(DescribeFailure(response.StatusCode, body));
            }

            using JsonDocument document = JsonDocument.Parse(body);

            string accessToken = FindString(document.RootElement, "access_token")
                ?? throw new BoletoProviderException("O Inter autenticou mas não devolveu o token.");

            int lifetime = (int?)FindDecimal(document.RootElement, "expires_in") ?? 3_600;

            tokens[key] = new CachedToken(accessToken, now.AddSeconds(lifetime));

            return accessToken;
        }
    }

    /// <summary>
    /// A chave do cache leva um resumo das proprias credenciais: trocar o
    /// client secret faz o token antigo deixar de ser encontrado, sem depender
    /// de alguem lembrar de limpar o cache.
    /// </summary>
    private static string TokenKey(BankCredentials credentials) =>
        credentials.CacheKey + "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{credentials.Sandbox}|{credentials.ClientId}|{credentials.ClientSecret}")));

    // --- Leitura tolerante do JSON ---

    /// <summary>
    /// Procura o valor em cada caminho, na ordem, e devolve o primeiro que
    /// existir.
    /// </summary>
    /// <remarks>
    /// Aceitar mais de um caminho nao e preguica: o mesmo dado ja apareceu
    /// aninhado em <c>boleto</c> e solto na raiz em versoes diferentes da API,
    /// e um campo que mudou de lugar nao pode derrubar a emissao de um
    /// condominio inteiro.
    /// </remarks>
    private static string? FindString(JsonElement root, params string[] paths)
    {
        foreach (string path in paths)
        {
            if (Resolve(root, path) is { ValueKind: JsonValueKind.String } element
                && element.GetString() is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    private static decimal? FindDecimal(JsonElement root, params string[] paths)
    {
        foreach (string path in paths)
        {
            switch (Resolve(root, path))
            {
                case { ValueKind: JsonValueKind.Number } number:
                    return number.GetDecimal();

                case { ValueKind: JsonValueKind.String } text
                    when decimal.TryParse(
                        text.GetString(),
                        NumberStyles.Number,
                        CultureInfo.InvariantCulture,
                        out decimal parsed):
                    return parsed;
            }
        }

        return null;
    }

    private static DateOnly? FindDate(JsonElement root, params string[] paths) =>
        FindString(root, paths) is { } text
        && DateOnly.TryParse(text[..Math.Min(text.Length, 10)], CultureInfo.InvariantCulture, out DateOnly date)
            ? date
            : null;

    private static JsonElement? Resolve(JsonElement root, string path)
    {
        JsonElement current = root;

        foreach (string segment in path.Split('.'))
        {
            if (current.ValueKind is not JsonValueKind.Object
                || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind is JsonValueKind.Null ? null : current;
    }

    // --- Apoio ---

    private static string Digits(string? value) =>
        new((value ?? string.Empty).Where(char.IsAsciiDigit).ToArray());

    private static string? Trim(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim() is { } trimmed && trimmed.Length <= maxLength
                ? trimmed
                : value.Trim()[..maxLength];
}
