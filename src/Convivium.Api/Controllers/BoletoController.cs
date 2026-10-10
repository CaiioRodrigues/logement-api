namespace Convivium.Api.Controllers;

using Convivium.Api.Auth;
using Convivium.Application.Banking;
using Convivium.Application.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>Boleto registrado em banco: configuracao, emissao e acompanhamento.</summary>
/// <remarks>
/// As politicas dos metodos somam com a da classe (as duas precisam passar),
/// entao a da classe e a mais branda e cada metodo so endurece.
/// </remarks>
[Route("api")]
[Authorize(Policy = ConviviumPolicies.Council)]
public sealed class BoletoController(BoletoService boletos) : ApiControllerBase
{
    // --- Configuracao do banco ---

    /// <summary>Banco do condominio, sem nenhum segredo.</summary>
    [HttpGet("condominio/boleto")]
    [Authorize(Policy = ConviviumPolicies.Manager)]
    [ProducesResponseType<BoletoSettingsDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BoletoSettingsDto>> GetSettings(CancellationToken cancellationToken)
        => Ok(await boletos.GetSettingsAsync(cancellationToken));

    /// <summary>
    /// Grava as credenciais do banco. Campo secreto vazio mantem o que ja
    /// estava guardado; escolher "nenhum banco" apaga as credenciais.
    /// </summary>
    [HttpPut("condominio/boleto")]
    [Authorize(Policy = ConviviumPolicies.Manager)]
    [RequestSizeLimit(256 * 1024)]
    [ProducesResponseType<BoletoSettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BoletoSettingsDto>> UpdateSettings(
        [FromBody] UpdateBoletoSettingsRequest request,
        CancellationToken cancellationToken)
        => Ok(await boletos.UpdateSettingsAsync(request, cancellationToken));

    /// <summary>Pede um token ao banco para conferir as credenciais, sem emitir nada.</summary>
    [HttpPost("condominio/boleto/verificar")]
    [Authorize(Policy = ConviviumPolicies.Manager)]
    [ProducesResponseType<BoletoSettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<BoletoSettingsDto>> Verify(CancellationToken cancellationToken)
        => Ok(await boletos.VerifyAsync(cancellationToken));

    // --- Emissao ---

    /// <summary>
    /// O que impede de emitir os boletos do ciclo — falta de CPF, cobranca
    /// vencida, banco sem configurar — conferido sem falar com o banco.
    /// </summary>
    [HttpGet("cobrancas/ciclos/{cycleId:guid}/boletos/prontidao")]
    [ProducesResponseType<BoletoReadinessDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BoletoReadinessDto>> Readiness(Guid cycleId, CancellationToken cancellationToken)
        => Ok(await boletos.GetReadinessAsync(cycleId, cancellationToken));

    /// <summary>Emite os boletos de todas as cobrancas prontas do ciclo.</summary>
    [HttpPost("cobrancas/ciclos/{cycleId:guid}/boletos")]
    [Authorize(Policy = ConviviumPolicies.Manager)]
    [ProducesResponseType<BoletoBatchResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<BoletoBatchResult>> IssueForCycle(Guid cycleId, CancellationToken cancellationToken)
        => Ok(await boletos.IssueForCycleAsync(cycleId, cancellationToken));

    [HttpPost("cobrancas/{chargeId:guid}/boleto")]
    [Authorize(Policy = ConviviumPolicies.Manager)]
    [ProducesResponseType<ChargeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<ChargeDto>> Issue(Guid chargeId, CancellationToken cancellationToken)
        => Ok(await boletos.IssueAsync(chargeId, cancellationToken));

    // --- Acompanhamento ---

    /// <summary>Confere a cobranca com o banco e da baixa se foi paga.</summary>
    [HttpPost("cobrancas/{chargeId:guid}/boleto/atualizar")]
    [Authorize(Policy = ConviviumPolicies.Finance)]
    [ProducesResponseType<ChargeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<ChargeDto>> Sync(Guid chargeId, CancellationToken cancellationToken)
        => Ok(await boletos.SyncAsync(chargeId, cancellationToken));

    /// <summary>Confere com o banco todos os boletos que ainda podem mudar.</summary>
    [HttpPost("cobrancas/boletos/atualizar")]
    [Authorize(Policy = ConviviumPolicies.Finance)]
    [ProducesResponseType<BoletoSyncResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BoletoSyncResult>> SyncOpen(CancellationToken cancellationToken)
        => Ok(await boletos.SyncOpenAsync(cancellationToken));

    /// <summary>Baixa o titulo no banco: ele deixa de ser pagavel.</summary>
    [HttpPost("cobrancas/{chargeId:guid}/boleto/cancelar")]
    [Authorize(Policy = ConviviumPolicies.Manager)]
    [ProducesResponseType<ChargeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<ChargeDto>> Cancel(Guid chargeId, CancellationToken cancellationToken)
        => Ok(await boletos.CancelAsync(chargeId, cancellationToken));

    /// <summary>
    /// Destrava uma emissao que ficou sem resposta do banco, depois de
    /// conferido no internet banking que nenhum titulo foi registrado.
    /// </summary>
    [HttpPost("cobrancas/{chargeId:guid}/boleto/liberar")]
    [Authorize(Policy = ConviviumPolicies.Manager)]
    [ProducesResponseType<ChargeDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ChargeDto>> Release(Guid chargeId, CancellationToken cancellationToken)
        => Ok(await boletos.ReleaseAsync(chargeId, cancellationToken));

    /// <summary>O PDF do boleto desenhado pelo proprio banco.</summary>
    [HttpGet("cobrancas/{chargeId:guid}/boleto/pdf-do-banco")]
    [Produces("application/pdf")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> BankPdf(Guid chargeId, CancellationToken cancellationToken)
        => File(await boletos.DownloadBankPdfAsync(chargeId, cancellationToken), "application/pdf", "boleto-banco.pdf");
}
