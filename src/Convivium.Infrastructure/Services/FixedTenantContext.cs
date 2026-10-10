namespace Convivium.Infrastructure.Services;

using Convivium.Application.Abstractions;
using Convivium.Domain.People;

/// <summary>
/// Contexto preso a um condominio, para rotinas em segundo plano que
/// trabalham um condominio de cada vez.
/// </summary>
/// <remarks>
/// Diferente do <see cref="SystemTenantContext"/>, que enxerga todos: aqui o
/// filtro por condominio continua ligado. E o que impede a baixa de um boleto
/// de procurar a conta contabil 4.1 e encontrar a do vizinho.
/// </remarks>
public sealed class FixedTenantContext(Guid condominiumId) : ITenantContext
{
    public Guid? CondominiumId { get; } = condominiumId;

    public Guid? PersonId => null;

    public MembershipRole? Role => MembershipRole.Manager;

    public bool IsSuperAdmin => false;
}
