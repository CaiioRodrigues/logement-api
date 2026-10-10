namespace Convivium.Infrastructure.Banking;

using Convivium.Application.Abstractions;
using Convivium.Application.Banking;
using Convivium.Application.Billing;
using Convivium.Domain.Condominiums;
using Convivium.Infrastructure.Persistence;
using Convivium.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Confere com o banco, de tempos em tempos, os boletos que ainda podem
/// mudar — e da baixa no caixa dos que foram pagos.
/// </summary>
/// <remarks>
/// <para>
/// E o que torna a baixa automatica sem depender do webhook do banco, que
/// exige o sistema publicado com endereco fixo na internet. Quando o
/// webhook existir, esta rotina continua valendo como rede de seguranca:
/// webhook perdido nao vira boleto pago esquecido.
/// </para>
/// <para>
/// Cada condominio roda com o seu proprio contexto, com o filtro por
/// condominio ligado. Um contexto de sistema enxergaria todos de uma vez, e
/// a baixa de um boleto poderia lancar a receita na conta do vizinho. Assume
/// uma unica instancia rodando, como o despachante de e-mail.
/// </para>
/// </remarks>
public sealed class BoletoSyncWorker(
    IServiceProvider services,
    IOptions<BankingOptions> options,
    ILogger<BoletoSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BankingOptions settings = options.Value;

        if (!settings.SyncEnabled)
        {
            logger.LogInformation("Conferência de boletos desabilitada por configuração.");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, settings.SyncMinutes));
        using var timer = new PeriodicTimer(interval);

        logger.LogInformation("Conferência de boletos ativa, a cada {Minutes} min.", interval.TotalMinutes);

        do
        {
            try
            {
                await SyncAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Uma rodada que falha nao derruba a rotina: a proxima tenta de novo.
                logger.LogError(exception, "Falha na conferência de boletos.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SyncAllAsync(CancellationToken cancellationToken)
    {
        if (!services.GetRequiredService<ISecretProtector>().IsAvailable)
        {
            return;
        }

        List<(Guid Id, string Name)> condominiums;

        using (IServiceScope scope = services.CreateScope())
        {
            var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<ConviviumDbContext>>();
            await using var db = new ConviviumDbContext(dbOptions, new SystemTenantContext());

            condominiums = (await db.Condominiums
                .AsNoTracking()
                .Where(c => c.IsActive && c.Boleto.Provider != BoletoProvider.None)
                .Select(c => new { c.Id, c.Name })
                .ToListAsync(cancellationToken))
                .Select(c => (c.Id, c.Name))
                .ToList();
        }

        foreach ((Guid id, string name) in condominiums)
        {
            try
            {
                BoletoSyncResult result = await SyncCondominiumAsync(id, cancellationToken);

                if (result.Changed > 0 || result.Errors.Count > 0)
                {
                    logger.LogInformation(
                        "Boletos de {Condominium}: {Checked} conferidos, {Changed} mudaram, {Payments} baixas no caixa, {Errors} erros.",
                        name,
                        result.Checked,
                        result.Changed,
                        result.PaymentsRegistered,
                        result.Errors.Count);
                }

                foreach (string error in result.Errors)
                {
                    logger.LogWarning("Boleto de {Condominium}: {Error}", name, error);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Credencial vencida num condominio nao pode parar a baixa dos outros.
                logger.LogWarning(exception, "Conferência de boletos de {Condominium} falhou.", name);
            }
        }
    }

    /// <summary>Monta os servicos com o contexto preso a um condominio.</summary>
    private async Task<BoletoSyncResult> SyncCondominiumAsync(Guid condominiumId, CancellationToken cancellationToken)
    {
        using IServiceScope scope = services.CreateScope();
        IServiceProvider provider = scope.ServiceProvider;

        var dbOptions = provider.GetRequiredService<DbContextOptions<ConviviumDbContext>>();
        await using var db = new ConviviumDbContext(dbOptions, new FixedTenantContext(condominiumId));

        var clock = provider.GetRequiredService<IClock>();
        var billing = new BillingService(db, clock, provider.GetRequiredService<IOptions<ConviviumOptions>>());

        var boletos = new BoletoService(
            db,
            clock,
            provider.GetRequiredService<ISecretProtector>(),
            provider.GetServices<IBoletoProvider>(),
            provider.GetRequiredService<IBankHttpClientSource>(),
            billing);

        return await boletos.SyncOpenAsync(cancellationToken);
    }
}
