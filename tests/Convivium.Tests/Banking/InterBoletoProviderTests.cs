namespace Convivium.Tests.Banking;

using System.Net;
using System.Text;
using System.Text.Json;
using Convivium.Application.Abstractions;
using Convivium.Application.Banking;
using Convivium.Domain.Payments;
using Convivium.Infrastructure.Banking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Como o adaptador le e escreve o Inter. O transporte e trocado por um
/// falso que grava cada pedido e responde o que o teste mandar.
/// </summary>
/// <remarks>
/// A referencia oficial da API nao estava acessivel quando o adaptador foi
/// escrito, entao estes testes fixam o contrato que foi reconstruido e,
/// principalmente, o que acontece quando a resposta foge dele.
/// </remarks>
public class InterBoletoProviderTests
{
    private static readonly DateOnly DueDate = new(2026, 10, 20);

    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeBank bank = new();

    private InterBoletoProvider Provider() => new(
        bank,
        Options.Create(new InterOptions()),
        new FixedClock(Now),
        NullLogger<InterBoletoProvider>.Instance);

    private static BankCredentials Credentials(string? account = "123456") => new()
    {
        CacheKey = "condominio-1",
        Sandbox = true,
        ClientId = "app",
        ClientSecret = "segredo",
        Certificate = [1, 2, 3],
        CheckingAccount = account,
    };

    private static BoletoIssueRequest Request(decimal amount = 941.56m) => new()
    {
        OwnNumber = "2609-A101",
        Amount = amount,
        DueDate = DueDate,
        DaysOnAgenda = 30,
        LateFeeRate = 0.02m,
        MonthlyInterestRate = 0.01m,
        Instructions = ["Taxa condominial 09/2026 - A 101", "Após o vencimento: multa de 2%"],
        Payer = new BoletoPayer
        {
            Name = "Ana Souza",
            Document = "529.982.247-25",
            Street = "Rua dos Timbiras",
            Number = "100",
            Complement = "A 101",
            District = "Centro",
            City = "Belo Horizonte",
            State = "mg",
            ZipCode = "30140-061",
            Email = "ana@exemplo.local",
        },
    };

    private static string Barcode(decimal amount, DateOnly dueDate) =>
        BoletoLayout.BuildBarcode("077", amount, dueDate, "0000000000000000000012345");

    private static string Title(string situation, string? barcode, bool nested = true, decimal? received = null)
    {
        var cobranca = new Dictionary<string, object?> { ["situacao"] = situation, ["seuNumero"] = "2609-A101" };

        if (received is not null)
        {
            cobranca["valorTotalRecebido"] = received;
            cobranca["dataSituacao"] = "2026-10-15";
        }

        var body = new Dictionary<string, object?> { ["cobranca"] = cobranca };

        if (barcode is not null)
        {
            if (nested)
            {
                body["boleto"] = new { nossoNumero = "00012345", codigoBarras = barcode };
            }
            else
            {
                body["codigoBarras"] = barcode;
                body["nossoNumero"] = "00012345";
            }

            body["pix"] = new { pixCopiaECola = "00020101PIXDOINTER" };
        }

        return JsonSerializer.Serialize(body);
    }

    // --- Emissao ---

    [Fact]
    public async Task EmissaoMandaOPedidoNoFormatoDoInter()
    {
        bank.On("POST", "cobranca/v3/cobrancas", HttpStatusCode.OK, """{"codigoSolicitacao":"sol-1"}""");
        bank.On("GET", "cobranca/v3/cobrancas/sol-1", HttpStatusCode.OK, Title("A_RECEBER", Barcode(941.56m, DueDate)));

        await Provider().IssueAsync(Request(), Credentials());

        FakeBank.Recorded emission = bank.Requests.Single(r => r.Method == "POST" && r.Path == "cobranca/v3/cobrancas");
        using JsonDocument json = JsonDocument.Parse(emission.Body);
        JsonElement root = json.RootElement;

        root.GetProperty("seuNumero").GetString().ShouldBe("2609-A101");
        root.GetProperty("valorNominal").GetDecimal().ShouldBe(941.56m);
        root.GetProperty("dataVencimento").GetString().ShouldBe("2026-10-20");
        root.GetProperty("numDiasAgenda").GetInt32().ShouldBe(30);

        JsonElement payer = root.GetProperty("pagador");
        payer.GetProperty("cpfCnpj").GetString().ShouldBe("52998224725");
        payer.GetProperty("tipoPessoa").GetString().ShouldBe("FISICA");
        payer.GetProperty("cep").GetString().ShouldBe("30140061");
        payer.GetProperty("uf").GetString().ShouldBe("MG");

        // Percentuais: 0.02 no sistema vira 2 no Inter.
        root.GetProperty("multa").GetProperty("codigo").GetString().ShouldBe("PERCENTUAL");
        root.GetProperty("multa").GetProperty("taxa").GetDecimal().ShouldBe(2m);
        root.GetProperty("mora").GetProperty("codigo").GetString().ShouldBe("TAXAMENSAL");
        root.GetProperty("mora").GetProperty("taxa").GetDecimal().ShouldBe(1m);

        root.GetProperty("mensagem").GetProperty("linha1").GetString().ShouldStartWith("Taxa condominial");
        root.GetProperty("mensagem").GetProperty("linha2").GetString().ShouldStartWith("Após o vencimento");
    }

    /// <summary>
    /// Corpo "chunked", sem Content-Length, e recusado ou lido vazio por
    /// gateway de banco. O pedido precisa sair com o tamanho no cabecalho.
    /// </summary>
    [Fact]
    public async Task EmissaoSaiComTamanhoDoCorpoNoCabecalho()
    {
        bank.On("POST", "cobranca/v3/cobrancas", HttpStatusCode.OK, """{"codigoSolicitacao":"sol-1"}""");
        bank.On("GET", "cobranca/v3/cobrancas/sol-1", HttpStatusCode.OK, Title("A_RECEBER", Barcode(941.56m, DueDate)));

        await Provider().IssueAsync(Request(), Credentials());

        FakeBank.Recorded emission = bank.Requests.Single(r => r.Method == "POST" && r.Path == "cobranca/v3/cobrancas");
        emission.ContentLength.ShouldBe(Encoding.UTF8.GetByteCount(emission.Body));
    }

    [Fact]
    public async Task EmissaoDevolveOBoletoConferido()
    {
        string barcode = Barcode(941.56m, DueDate);
        bank.On("POST", "cobranca/v3/cobrancas", HttpStatusCode.OK, """{"codigoSolicitacao":"sol-1"}""");
        bank.On("GET", "cobranca/v3/cobrancas/sol-1", HttpStatusCode.OK, Title("A_RECEBER", barcode));

        BoletoRegistration result = await Provider().IssueAsync(Request(), Credentials());

        result.ExternalId.ShouldBe("sol-1");
        result.State.ShouldBe(BoletoState.Registered);
        result.Slip!.Barcode.ShouldBe(barcode);
        result.Slip.DigitableLine.ShouldBe(BoletoLayout.DigitableLine(barcode));
        result.OurNumber.ShouldBe("00012345");
        result.PixPayload.ShouldBe("00020101PIXDOINTER");
    }

    [Fact]
    public async Task ContaCorrenteVaiNoCabecalhoSoComDigitos()
    {
        bank.On("POST", "cobranca/v3/cobrancas", HttpStatusCode.OK, """{"codigoSolicitacao":"sol-1"}""");
        bank.On("GET", "cobranca/v3/cobrancas/sol-1", HttpStatusCode.OK, Title("A_RECEBER", Barcode(941.56m, DueDate)));

        await Provider().IssueAsync(Request(), Credentials(account: "12345-6"));

        bank.Requests.Where(r => r.Path.StartsWith("cobranca", StringComparison.Ordinal))
            .ShouldAllBe(r => r.Account == "123456");
    }

    /// <summary>
    /// O ponto mais importante do adaptador: um titulo registrado com valor
    /// diferente do pedido e recusado e sai marcado, com o identificador,
    /// para quem chamou cancelar o titulo no banco.
    /// </summary>
    [Fact]
    public async Task ValorDivergenteERecusadoComOIdentificadorDoTitulo()
    {
        bank.On("POST", "cobranca/v3/cobrancas", HttpStatusCode.OK, """{"codigoSolicitacao":"sol-1"}""");
        bank.On("GET", "cobranca/v3/cobrancas/sol-1", HttpStatusCode.OK, Title("A_RECEBER", Barcode(951.56m, DueDate)));

        BoletoProviderException error = await Should.ThrowAsync<BoletoProviderException>(() =>
            Provider().IssueAsync(Request(941.56m), Credentials()));

        error.SlipMismatch.ShouldBeTrue();
        error.ExternalId.ShouldBe("sol-1");
        error.Message.ShouldContain("951,56");
        error.Message.ShouldContain("941,56");
    }

    [Fact]
    public async Task VencimentoDivergenteERecusado()
    {
        bank.On("POST", "cobranca/v3/cobrancas", HttpStatusCode.OK, """{"codigoSolicitacao":"sol-1"}""");
        bank.On("GET", "cobranca/v3/cobrancas/sol-1", HttpStatusCode.OK, Title("A_RECEBER", Barcode(941.56m, DueDate.AddDays(1))));

        BoletoProviderException error = await Should.ThrowAsync<BoletoProviderException>(() =>
            Provider().IssueAsync(Request(), Credentials()));

        error.SlipMismatch.ShouldBeTrue();
        error.Message.ShouldContain("21/10/2026");
    }

    [Fact]
    public async Task CodigoDeBarrasCorrompidoERecusado()
    {
        char[] corrupted = Barcode(941.56m, DueDate).ToCharArray();
        corrupted[30] = corrupted[30] == '9' ? '8' : (char)(corrupted[30] + 1);

        bank.On("POST", "cobranca/v3/cobrancas", HttpStatusCode.OK, """{"codigoSolicitacao":"sol-1"}""");
        bank.On("GET", "cobranca/v3/cobrancas/sol-1", HttpStatusCode.OK, Title("A_RECEBER", new string(corrupted)));

        BoletoProviderException error = await Should.ThrowAsync<BoletoProviderException>(() =>
            Provider().IssueAsync(Request(), Credentials()));

        error.SlipMismatch.ShouldBeTrue();
    }

    /// <summary>
    /// A emissao passou — ha codigo de solicitacao — e so a consulta seguinte
    /// caiu. O titulo existe: virar erro liberaria uma segunda emissao.
    /// </summary>
    [Fact]
    public async Task ConsultaQueFalhaDepoisDaEmissaoDeixaPendente()
    {
        bank.On("POST", "cobranca/v3/cobrancas", HttpStatusCode.OK, """{"codigoSolicitacao":"sol-1"}""");
        bank.On("GET", "cobranca/v3/cobrancas/sol-1", HttpStatusCode.ServiceUnavailable, "");

        BoletoRegistration result = await Provider().IssueAsync(Request(), Credentials());

        result.ExternalId.ShouldBe("sol-1");
        result.State.ShouldBe(BoletoState.Pending);
        result.Slip.ShouldBeNull();
    }

    /// <summary>5xx numa emissao nao garante que nada foi gravado no banco.</summary>
    [Fact]
    public async Task ErroDoServidorNaEmissaoTemDesfechoIncerto()
    {
        bank.On("POST", "cobranca/v3/cobrancas", HttpStatusCode.BadGateway, "");

        BoletoProviderException error = await Should.ThrowAsync<BoletoProviderException>(() =>
            Provider().IssueAsync(Request(), Credentials()));

        error.OutcomeUnknown.ShouldBeTrue();
    }

    [Fact]
    public async Task ConexaoQueCaiNaEmissaoTemDesfechoIncerto()
    {
        bank.Throw("POST", "cobranca/v3/cobrancas", new HttpRequestException("connection reset"));

        BoletoProviderException error = await Should.ThrowAsync<BoletoProviderException>(() =>
            Provider().IssueAsync(Request(), Credentials()));

        error.OutcomeUnknown.ShouldBeTrue();
        error.Message.ShouldContain("internet banking");
    }

    /// <summary>Recusa clara: nada foi gravado, e o motivo do banco chega ao sindico.</summary>
    [Fact]
    public async Task RecusaDoBancoTrazOCampoRecusado()
    {
        bank.On("POST", "cobranca/v3/cobrancas", HttpStatusCode.BadRequest, """
            {"title":"Bad Request","detail":"Requisição inválida",
             "violacoes":[{"propriedade":"pagador.cep","razao":"CEP inválido"}]}
            """);

        BoletoProviderException error = await Should.ThrowAsync<BoletoProviderException>(() =>
            Provider().IssueAsync(Request(), Credentials()));

        error.OutcomeUnknown.ShouldBeFalse();
        error.Message.ShouldContain("pagador.cep: CEP inválido");
    }

    [Fact]
    public async Task PagadorSemCpfNemChegaAoBanco()
    {
        BoletoIssueRequest request = Request() with { Payer = Request().Payer with { Document = "" } };

        await Should.ThrowAsync<Convivium.Domain.Common.DomainException>(() =>
            Provider().IssueAsync(request, Credentials()));

        bank.Requests.ShouldNotContain(r => r.Path == "cobranca/v3/cobrancas");
    }

    // --- Consulta ---

    [Theory]
    [InlineData("EM_PROCESSAMENTO", false, BoletoState.Pending)]
    [InlineData("A_RECEBER", true, BoletoState.Registered)]
    [InlineData("ATRASADO", true, BoletoState.Registered)]
    [InlineData("RECEBIDO", true, BoletoState.Paid)]
    [InlineData("MARCADO_RECEBIDO", true, BoletoState.Paid)]
    [InlineData("CANCELADO", true, BoletoState.Cancelled)]
    [InlineData("EXPIRADO", true, BoletoState.Expired)]
    public async Task SituacaoDoInterViraEstadoDoBoleto(string situation, bool withBarcode, BoletoState expected)
    {
        bank.On("GET", "cobranca/v3/cobrancas/sol-1", HttpStatusCode.OK,
            Title(situation, withBarcode ? Barcode(941.56m, DueDate) : null));

        (await Provider().QueryAsync("sol-1", Credentials())).State.ShouldBe(expected);
    }

    /// <summary>
    /// "A receber" sem codigo de barras ainda nao e um boleto pagavel, e a
    /// tela nao pode dizer ao sindico que esta pronto.
    /// </summary>
    [Fact]
    public async Task ARecebeSemCodigoDeBarrasContinuaPendente()
    {
        bank.On("GET", "cobranca/v3/cobrancas/sol-1", HttpStatusCode.OK, Title("A_RECEBER", barcode: null));

        (await Provider().QueryAsync("sol-1", Credentials())).State.ShouldBe(BoletoState.Pending);
    }

    /// <summary>Situacao desconhecida vira pendente — nunca "pago" nem "cancelado" por chute.</summary>
    [Fact]
    public async Task SituacaoDesconhecidaFicaPendente()
    {
        bank.On("GET", "cobranca/v3/cobrancas/sol-1", HttpStatusCode.OK, Title("ALGO_NOVO", Barcode(941.56m, DueDate)));

        (await Provider().QueryAsync("sol-1", Credentials())).State.ShouldBe(BoletoState.Pending);
    }

    /// <summary>O mesmo dado ja apareceu aninhado e solto na raiz em versoes da API.</summary>
    [Fact]
    public async Task CodigoDeBarrasNaRaizTambemELido()
    {
        string barcode = Barcode(941.56m, DueDate);
        bank.On("GET", "cobranca/v3/cobrancas/sol-1", HttpStatusCode.OK, Title("A_RECEBER", barcode, nested: false));

        BoletoRegistration result = await Provider().QueryAsync("sol-1", Credentials());

        result.Slip!.Barcode.ShouldBe(barcode);
        result.OurNumber.ShouldBe("00012345");
    }

    /// <summary>Banco que devolve so a linha digitavel: o codigo de barras e remontado dela.</summary>
    [Fact]
    public async Task SoLinhaDigitavelTambemServe()
    {
        string barcode = Barcode(941.56m, DueDate);
        string body = JsonSerializer.Serialize(new
        {
            cobranca = new { situacao = "A_RECEBER" },
            boleto = new { linhaDigitavel = BoletoLayout.FormatDigitableLine(BoletoLayout.DigitableLine(barcode)) },
        });
        bank.On("GET", "cobranca/v3/cobrancas/sol-1", HttpStatusCode.OK, body);

        (await Provider().QueryAsync("sol-1", Credentials())).Slip!.Barcode.ShouldBe(barcode);
    }

    [Fact]
    public async Task PagamentoTrazValorEData()
    {
        bank.On("GET", "cobranca/v3/cobrancas/sol-1", HttpStatusCode.OK,
            Title("RECEBIDO", Barcode(941.56m, DueDate), received: 960.39m));

        BoletoRegistration result = await Provider().QueryAsync("sol-1", Credentials());

        result.State.ShouldBe(BoletoState.Paid);
        result.PaidAmount.ShouldBe(960.39m);
        result.PaidOn.ShouldBe(new DateOnly(2026, 10, 15));
    }

    // --- Token ---

    [Fact]
    public async Task TokenPedidoComClientCredentialsEReaproveitado()
    {
        bank.On("GET", "cobranca/v3/cobrancas/sol-1", HttpStatusCode.OK, Title("A_RECEBER", Barcode(941.56m, DueDate)));
        InterBoletoProvider provider = Provider();

        await provider.QueryAsync("sol-1", Credentials());
        await provider.QueryAsync("sol-1", Credentials());

        FakeBank.Recorded token = bank.Requests.Single(r => r.Path == "oauth/v2/token");
        token.Body.ShouldContain("grant_type=client_credentials");
        token.Body.ShouldContain("client_id=app");
        token.Body.ShouldContain("scope=boleto-cobranca.read+boleto-cobranca.write");

        bank.Requests.Where(r => r.Path.StartsWith("cobranca", StringComparison.Ordinal))
            .ShouldAllBe(r => r.Authorization == "Bearer token-1");
    }

    /// <summary>Trocar o client secret nao pode continuar usando o token das credenciais antigas.</summary>
    [Fact]
    public async Task SegredoNovoPedeTokenNovo()
    {
        bank.On("GET", "cobranca/v3/cobrancas/sol-1", HttpStatusCode.OK, Title("A_RECEBER", Barcode(941.56m, DueDate)));
        InterBoletoProvider provider = Provider();

        await provider.QueryAsync("sol-1", Credentials());
        await provider.QueryAsync("sol-1", Credentials() with { ClientSecret = "segredo-novo" });

        bank.Requests.Count(r => r.Path == "oauth/v2/token").ShouldBe(2);
    }

    /// <summary>Token revogado do lado do banco: uma nova tentativa com token novo.</summary>
    [Fact]
    public async Task ConsultaCom401TentaDeNovoComTokenNovo()
    {
        bank.OnSequence("GET", "cobranca/v3/cobrancas/sol-1",
            (HttpStatusCode.Unauthorized, ""),
            (HttpStatusCode.OK, Title("A_RECEBER", Barcode(941.56m, DueDate))));

        (await Provider().QueryAsync("sol-1", Credentials())).State.ShouldBe(BoletoState.Registered);

        bank.Requests.Count(r => r.Path == "oauth/v2/token").ShouldBe(2);
    }

    [Fact]
    public async Task CredencialRecusadaExplicaOQueConferir()
    {
        bank.On("POST", "oauth/v2/token", HttpStatusCode.Unauthorized, """{"title":"Unauthorized"}""");

        BoletoProviderException error = await Should.ThrowAsync<BoletoProviderException>(() =>
            Provider().VerifyAsync(Credentials()));

        error.Message.ShouldContain("client secret");
    }

    // --- Cancelamento e PDF ---

    [Fact]
    public async Task CancelamentoMandaOMotivoNoVocabularioDoBanco()
    {
        bank.On("POST", "cobranca/v3/cobrancas/sol-1/cancelar", HttpStatusCode.Accepted, "");

        await Provider().CancelAsync("sol-1", "qualquer coisa", Credentials());

        bank.Requests.Single(r => r.Path.EndsWith("/cancelar", StringComparison.Ordinal))
            .Body.ShouldBe("""{"motivoCancelamento":"ACERTOS"}""");
    }

    [Fact]
    public async Task PdfVemDoBase64()
    {
        byte[] pdf = Encoding.ASCII.GetBytes("%PDF-1.4 teste");
        bank.On("GET", "cobranca/v3/cobrancas/sol-1/pdf", HttpStatusCode.OK,
            JsonSerializer.Serialize(new { pdf = Convert.ToBase64String(pdf) }));

        (await Provider().DownloadPdfAsync("sol-1", Credentials())).ShouldBe(pdf);
    }

    // --- Apoio ---

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; } = now;
    }

    /// <summary>Banco de mentira: grava os pedidos e responde o que foi combinado.</summary>
    private sealed class FakeBank : HttpMessageHandler, IBankHttpClientSource
    {
        private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> routes = [];

        private int tokens;

        public List<Recorded> Requests { get; } = [];

        public sealed record Recorded(
            string Method,
            string Path,
            string Body,
            long? ContentLength,
            string? Authorization,
            string? Account);

        public void On(string method, string path, HttpStatusCode status, string body) =>
            OnSequence(method, path, (status, body));

        public void OnSequence(string method, string path, params (HttpStatusCode Status, string Body)[] responses)
        {
            var queue = new Queue<Func<HttpResponseMessage>>();

            foreach ((HttpStatusCode status, string body) in responses)
            {
                queue.Enqueue(() => new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            routes[$"{method} {path}"] = queue;
        }

        public void Throw(string method, string path, Exception exception) =>
            routes[$"{method} {path}"] = new Queue<Func<HttpResponseMessage>>([() => throw exception]);

        public HttpClient Get(BankCredentials credentials) =>
            new(this, disposeHandler: false) { BaseAddress = new Uri("https://inter.teste/") };

        public void Invalidate(string cacheKey)
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath.TrimStart('/');
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new Recorded(
                request.Method.Method,
                path,
                body,
                request.Content?.Headers.ContentLength,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("x-conta-corrente", out var values) ? values.Single() : null));

            string key = $"{request.Method.Method} {path}";

            if (routes.TryGetValue(key, out Queue<Func<HttpResponseMessage>>? queue) && queue.Count > 0)
            {
                // A ultima resposta da fila se repete, para chamadas extras.
                return queue.Count == 1 ? queue.Peek()() : queue.Dequeue()();
            }

            if (path == "oauth/v2/token")
            {
                tokens++;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"access_token":"token-{{tokens}}","expires_in":3600}"""),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };
        }
    }
}
