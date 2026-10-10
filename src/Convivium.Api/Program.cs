using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using Convivium.Api.Auth;
using Convivium.Api.Common;
using Convivium.Application;
using Convivium.Application.Abstractions;
using Convivium.Application.Auth;
using Convivium.Domain.People;
using Convivium.Infrastructure;
using Convivium.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Enums viajam como texto: o front do convivium-web le "Manager",
        // nao 5, e um valor novo no meio do enum nao quebra o cliente.
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());

        // Campos nulos sao serializados, nao omitidos. Omitir deixa o contrato
        // instavel: uma serie de grafico perderia os meses sem dado, e o
        // cliente teria que distinguir "ausente" de "sem valor".
    });

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

// --- Autenticacao ---

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("Seção Jwt ausente na configuração.");

// Falha no boot, e nao na primeira tentativa de login: subir uma API que
// aceita qualquer token e pior do que nao subir. A chave nunca deve estar no
// appsettings versionado — use variavel de ambiente ou gerenciador de segredos.
if (Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32)
{
    throw new InvalidOperationException(
        "Jwt:SigningKey não configurada ou curta demais. " +
        "Defina pelo menos 32 bytes em Jwt__SigningKey (variável de ambiente) " +
        "ou em appsettings.Development.json para rodar localmente.");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Sem o mapeamento legado, "sub" continua "sub" em vez de virar
        // a URI longa de nameidentifier do WS-Federation.
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = ConviviumClaims.Name,
            RoleClaimType = ConviviumClaims.Role,
        };
    });

builder.Services.AddSingleton<IAuthorizationHandler, MinimumRoleHandler>();

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(ConviviumPolicies.Member, policy =>
        policy.AddRequirements(new MinimumRoleRequirement(MembershipRole.Resident)))
    .AddPolicy(ConviviumPolicies.Council, policy =>
        policy.AddRequirements(new MinimumRoleRequirement(MembershipRole.CouncilMember)))
    .AddPolicy(ConviviumPolicies.Finance, policy =>
        policy.AddRequirements(new MinimumRoleRequirement(MembershipRole.AssistantManager)))
    .AddPolicy(ConviviumPolicies.Manager, policy =>
        policy.AddRequirements(new MinimumRoleRequirement(MembershipRole.Manager)))
    // Exige a marca no token, nao um papel: papel e sempre dentro de um
    // condominio, e criar condominio acontece fora de qualquer um.
    .AddPolicy(ConviviumPolicies.SuperAdmin, policy =>
        policy.RequireClaim(ConviviumClaims.SuperAdmin, "1"));

// --- CORS para o convivium-web ---

string[] allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// --- Swagger ---

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Convivium API",
        Version = "v1",
        Description = "Gestão de condomínios: caixa, rateio, cobranças e prestação de contas.",
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Cole aqui o accessToken devolvido por POST /api/auth/login.",
    });

    // Swashbuckle 10 resolve a referencia contra o documento gerado,
    // por isso o requisito e uma funcao e nao um objeto pronto.
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
    });
});

builder.Services.AddConviviumRateLimiter();

// --- Proxy reverso ---

// Atras do Caddy/nginx, RemoteIpAddress e o IP do proxy: sem isto o limite de
// tentativas viraria um balde unico para todo mundo, e o IP gravado na sessao
// seria sempre o mesmo. Fica desligado por padrao de proposito — aceitar
// X-Forwarded-For de qualquer origem deixa qualquer um forjar o proprio IP.
//
// Aceita IP solto ("10.0.0.5") ou faixa ("172.18.0.0/16"). A faixa existe por
// causa do Docker: o container do proxy troca de IP a cada recriacao, entao
// fixar um endereco quebraria o limite no primeiro "compose up" seguinte.
string[] trustedProxies = builder.Configuration.GetSection("App:TrustedProxies").Get<string[]>() ?? [];

if (trustedProxies.Length > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (string proxy in trustedProxies)
        {
            if (proxy.Contains('/', StringComparison.Ordinal))
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(proxy));
            }
            else
            {
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            }
        }

        // Um proxy so na frente. O padrao do ASP.NET tambem e 1, mas deixar
        // explicito evita que alguem aumente sem perceber que cada salto a
        // mais e um cabecalho a mais em que se passa a confiar.
        options.ForwardLimit = 1;
    });
}

builder.Services.AddHealthChecks();

var app = builder.Build();

// Chave de credenciais bancarias presente mas invalida derruba a subida, como
// a do JWT: subir e so falhar quando o sindico abre a tela do banco esconderia
// o motivo atras de um erro 500. Ausente, o boleto bancario so fica desligado.
app.Services.GetRequiredService<Convivium.Application.Abstractions.ISecretProtector>();

await app.MigrateAndSeedAsync();

if (trustedProxies.Length > 0)
{
    app.UseForwardedHeaders();
}

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "Convivium API v1"));
}

app.UseCors();

// Antes da autenticacao: tentativa barrada nao deve nem chegar a consultar o
// banco para conferir a senha.
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

/// <summary>Exposto para que os testes de integracao possam subir a API em memoria.</summary>
public partial class Program;
