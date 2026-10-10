namespace Convivium.Infrastructure;

using Convivium.Application.Abstractions;
using Convivium.Application.Auth;
using Convivium.Application.Accountability;
using Convivium.Application.Banking;
using Convivium.Application.Billing;
using Convivium.Infrastructure.Auth;
using Convivium.Infrastructure.Banking;
using Convivium.Infrastructure.Notifications;
using Convivium.Infrastructure.Documents;
using Convivium.Infrastructure.Persistence;
using Convivium.Infrastructure.Persistence.Seeding;
using Convivium.Application.Notifications;
using Convivium.Application.Utilities;
using Convivium.Infrastructure.Services;
using Convivium.Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "Connection string 'Default' não configurada.");

        services.AddDbContext<ConviviumDbContext>(options => options
            .UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsAssembly(typeof(ConviviumDbContext).Assembly.FullName)
                .EnableRetryOnFailure(3))
            .UseSnakeCaseNamingConvention());

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ConviviumDbContext>());

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<ConviviumOptions>(configuration.GetSection(ConviviumOptions.SectionName));
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));

        // QuestPDF exige a licenca declarada antes de gerar o primeiro documento.
        // Community e gratuita para empresas com receita anual abaixo de US$ 1 milhao.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddSingleton<IAccessTokenFactory, JwtAccessTokenFactory>();

        services.AddSingleton<IChargeDocumentRenderer, ChargePdfRenderer>();
        services.AddSingleton<IStatementRenderer, StatementPdfRenderer>();
        services.AddSingleton<IPdfTextExtractor, PdfPigTextExtractor>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<EmailComposer>();

        services.AddHostedService<EmailDispatcher>();

        // A ordem importa: leitores especificos primeiro, o generico por ultimo,
        // porque o generico aceita qualquer texto e engoliria os demais.
        services.AddSingleton<IUtilityBillParser, CemigBillParser>();
        services.AddSingleton<IUtilityBillParser, GenericBillParser>();

        // Boleto registrado. O protetor le a chave uma vez; sem ela o sistema
        // sobe normalmente e so a configuracao do banco fica bloqueada.
        services.Configure<InterOptions>(configuration.GetSection(InterOptions.SectionName));
        services.AddSingleton<ISecretProtector>(_ => new AesGcmSecretProtector(configuration["App:SecretsKey"]));
        services.AddSingleton<BankConnectionPool>();
        services.AddSingleton<IBankHttpClientSource>(sp => sp.GetRequiredService<BankConnectionPool>());

        // Singleton porque guarda os tokens do banco em memoria: um por
        // requisicao pediria um token novo a cada boleto emitido.
        services.AddSingleton<IBoletoProvider, InterBoletoProvider>();

        services.Configure<BankingOptions>(configuration.GetSection(BankingOptions.SectionName));
        services.AddHostedService<BoletoSyncWorker>();

        services.AddScoped<DemoDataSeeder>();
        services.AddScoped<SuperAdminBootstrapper>();

        return services;
    }
}
