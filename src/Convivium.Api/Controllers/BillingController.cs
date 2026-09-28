namespace Convivium.Api.Controllers;

using Convivium.Api.Auth;
using Convivium.Application.Billing;
using Convivium.Application.Common;
using Convivium.Domain.Billing;
using Convivium.Domain.Common;
using Convivium.Domain.People;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>Rateio mensal e cobrancas por unidade.</summary>
[Route("api/cobrancas")]
[Authorize(Policy = ConviviumPolicies.Council)]
public sealed class BillingController(BillingService billing) : ApiControllerBase
{
    // --- Ciclo de rateio ---

    /// <summary>
    /// Simula o rateio da competencia sem gravar nada. Use para conferir o
    /// valor da cota antes de fechar.
    /// </summary>
    [HttpGet("previa")]
    [ProducesResponseType<ApportionmentPreview>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApportionmentPreview>> Preview(
        [FromQuery] string competence,
        [FromQuery] ApportionmentMethod? method,
        CancellationToken cancellationToken)
        => Ok(await billing.PreviewAsync(Competence.Parse(competence), method, cancellationToken));

    [HttpGet("ciclos")]
    [ProducesResponseType<IReadOnlyList<BillingCycleDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BillingCycleDto>>> ListCycles(
        CancellationToken cancellationToken)
        => Ok(await billing.ListCyclesAsync(cancellationToken));

    [HttpGet("ciclos/{id:guid}")]
    [ProducesResponseType<BillingCycleDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BillingCycleDto>> GetCycle(Guid id, CancellationToken cancellationToken)
        => Ok(await billing.GetCycleAsync(id, cancellationToken));

    /// <summary>Abre a competencia em rascunho.</summary>
    [HttpPost("ciclos")]
    [Authorize(Policy = ConviviumPolicies.Manager)]
    [ProducesResponseType<BillingCycleDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BillingCycleDto>> OpenCycle(
        [FromBody] OpenBillingCycleRequest request,
        CancellationToken cancellationToken)
    {
        BillingCycleDto cycle = await billing.OpenCycleAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetCycle), new { id = cycle.Id }, cycle);
    }

    /// <summary>
    /// Congela os valores da competencia e gera uma cobranca por unidade.
    /// Depois disso, alterar despesas do mes nao muda mais o que foi cobrado.
    /// </summary>
    [HttpPost("ciclos/{id:guid}/fechar")]
    [Authorize(Policy = ConviviumPolicies.Manager)]
    [ProducesResponseType<BillingCycleDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BillingCycleDto>> CloseCycle(Guid id, CancellationToken cancellationToken)
        => Ok(await billing.CloseCycleAsync(id, Tenant.PersonId, cancellationToken));

    /// <summary>Gera o PIX de cada cobranca e libera o acesso dos moradores.</summary>
    [HttpPost("ciclos/{id:guid}/publicar")]
    [Authorize(Policy = ConviviumPolicies.Manager)]
    [ProducesResponseType<BillingCycleDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BillingCycleDto>> PublishCycle(Guid id, CancellationToken cancellationToken)
        => Ok(await billing.PublishAsync(id, cancellationToken));

    // --- Cobrancas ---

    [HttpGet]
    [ProducesResponseType<PagedResult<ChargeDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<ChargeDto>>> List(
        [FromQuery] ChargeFilter filter,
        [FromQuery] PageRequest page,
        CancellationToken cancellationToken)
        => Ok(await billing.ListChargesAsync(filter, page, cancellationToken));

    /// <summary>Unidades inadimplentes, com multa e juros ja apurados ate hoje.</summary>
    [HttpGet("inadimplencia")]
    [ProducesResponseType<IReadOnlyList<DelinquentUnit>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DelinquentUnit>>> Delinquency(
        CancellationToken cancellationToken)
        => Ok(await billing.GetDelinquencyAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ChargeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChargeDto>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await billing.GetChargeAsync(id, cancellationToken));

    /// <summary>Registra o recebimento e gera a entrada correspondente no caixa.</summary>
    [HttpPost("{id:guid}/receber")]
    [Authorize(Policy = ConviviumPolicies.Finance)]
    [ProducesResponseType<ChargeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ChargeDto>> RegisterPayment(
        Guid id,
        [FromBody] RegisterPaymentRequest request,
        CancellationToken cancellationToken)
        => Ok(await billing.RegisterPaymentAsync(id, request, Tenant.PersonId, cancellationToken));

    /// <summary>Desfaz o ultimo recebimento e remove a entrada do caixa.</summary>
    [HttpPost("{id:guid}/estornar-recebimento")]
    [Authorize(Policy = ConviviumPolicies.Finance)]
    [ProducesResponseType<ChargeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ChargeDto>> ReversePayment(
        Guid id,
        CancellationToken cancellationToken)
        => Ok(await billing.ReversePaymentAsync(id, cancellationToken));

    /// <summary>Cobranca avulsa: reserva de salao, multa por infracao, segunda via.</summary>
    [HttpPost("avulsa")]
    [Authorize(Policy = ConviviumPolicies.Finance)]
    [ProducesResponseType<ChargeDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ChargeDto>> CreateExtra(
        [FromBody] CreateExtraChargeRequest request,
        CancellationToken cancellationToken)
    {
        ChargeDto charge = await billing.CreateExtraChargeAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = charge.Id }, charge);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = ConviviumPolicies.Manager)]
    [ProducesResponseType<ChargeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ChargeDto>> Cancel(Guid id, CancellationToken cancellationToken)
        => Ok(await billing.CancelChargeAsync(id, cancellationToken));

    internal static string BuildFileName(ChargeDocument document, string extension = "pdf")
    {
        // Barra vira hifen: "08/2026" quebraria o nome do arquivo baixado.
        string competence = document.Competence.Replace('/', '-');
        string unit = new(document.UnitIdentifier
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray());

        return $"boleto-{unit}-{competence}.{extension}";
    }
}

/// <summary>O que o morador ve das proprias cobrancas.</summary>
[Route("api/minhas-cobrancas")]
[Authorize(Policy = ConviviumPolicies.Member)]
public sealed class MyChargesController(BillingService billing) : ApiControllerBase
{
    /// <summary>Cobrancas das unidades em que a pessoa mora ou e proprietaria.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ChargeDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ChargeDto>>> List(CancellationToken cancellationToken)
        => Ok(await billing.GetChargesForPersonAsync(Tenant.RequirePersonId(), cancellationToken));

    /// <summary>Boleto em PDF, com QR Code PIX e o detalhamento dos itens.</summary>
    /// <remarks>
    /// <para>
    /// Mora aqui, e não no <see cref="BillingController"/>, porque baixar o
    /// próprio boleto é coisa de morador e aquele controlador inteiro exige
    /// conselho. Os dois <c>[Authorize]</c>, o da classe e o do método, se
    /// somam em vez de se substituir: não existe abrir uma exceção lá dentro.
    /// </para>
    /// <para>
    /// A rota é absoluta para continuar em <c>/api/cobrancas/{id}/pdf</c>,
    /// que é o endereço que o convivium-web e os links já publicados usam.
    /// </para>
    /// </remarks>
    [HttpGet("/api/cobrancas/{id:guid}/pdf")]
    [Produces("application/pdf")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPdf(
        Guid id,
        [FromServices] IChargeDocumentRenderer renderer,
        CancellationToken cancellationToken)
    {
        ChargeDocument document = await billing.BuildDocumentAsync(
            id, RestrictToOwnCharges(), cancellationToken);

        return File(
            renderer.Render(document),
            "application/pdf",
            BillingController.BuildFileName(document));
    }

    /// <summary>Boleto em imagem, para mandar no WhatsApp.</summary>
    /// <remarks>
    /// Mesmo desenho e mesma regra de permissão do PDF. Muda só o formato:
    /// no WhatsApp a imagem aparece aberta na conversa, com o QR Code do PIX
    /// à vista, enquanto o PDF chega como um cartão que precisa ser tocado.
    /// </remarks>
    [HttpGet("/api/cobrancas/{id:guid}/imagem")]
    [Produces("image/png")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetImage(
        Guid id,
        [FromServices] IChargeDocumentRenderer renderer,
        CancellationToken cancellationToken)
    {
        ChargeDocument document = await billing.BuildDocumentAsync(
            id, RestrictToOwnCharges(), cancellationToken);

        return File(
            renderer.RenderImage(document),
            "image/png",
            BillingController.BuildFileName(document, "png"));
    }

    /// <summary>
    /// Nulo para quem enxerga a prestação de contas inteira; o id da pessoa
    /// para o morador, que só pode ver o que é dele.
    /// </summary>
    private Guid? RestrictToOwnCharges() =>
        Tenant.IsSuperAdmin || Tenant.Role >= MembershipRole.CouncilMember
            ? null
            : Tenant.RequirePersonId();
}

/// <summary>
/// Acesso publico ao boleto pelo link enviado por e-mail, sem login.
/// </summary>
/// <remarks>
/// A autorizacao e o proprio token da URL: 256 bits de entropia, valido para
/// uma unica cobranca. E o que permite mandar o boleto para um proprietario
/// que nunca criou conta no sistema.
/// </remarks>
[Route("api/boleto")]
[AllowAnonymous]
public sealed class PublicChargeController(BillingService billing) : ApiControllerBase
{
    [HttpGet("{token}")]
    [ProducesResponseType<ChargeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChargeDto>> Get(string token, CancellationToken cancellationToken)
        => Ok(await billing.GetByPublicTokenAsync(token, cancellationToken));

    /// <summary>Boleto em imagem pelo link publico, sem login.</summary>
    [HttpGet("{token}/imagem")]
    [Produces("image/png")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetImage(
        string token,
        [FromServices] IChargeDocumentRenderer renderer,
        CancellationToken cancellationToken)
    {
        ChargeDocument document = await billing.BuildDocumentByTokenAsync(token, cancellationToken);

        return File(
            renderer.RenderImage(document),
            "image/png",
            BillingController.BuildFileName(document, "png"));
    }

    /// <summary>Baixa o boleto em PDF pelo link publico, sem login.</summary>
    [HttpGet("{token}/pdf")]
    [Produces("application/pdf")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPdf(
        string token,
        [FromServices] IChargeDocumentRenderer renderer,
        CancellationToken cancellationToken)
    {
        ChargeDocument document = await billing.BuildDocumentByTokenAsync(token, cancellationToken);
        return File(renderer.Render(document), "application/pdf", BillingController.BuildFileName(document));
    }
}
