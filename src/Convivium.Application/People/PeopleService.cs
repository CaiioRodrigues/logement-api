namespace Convivium.Application.People;

using System.Security.Cryptography;
using System.Text;
using Convivium.Application.Abstractions;
using Convivium.Domain.Common;
using Convivium.Application.Notifications;
using Convivium.Domain.Condominiums;
using Convivium.Domain.Notifications;
using Convivium.Domain.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// Cadastro de moradores, papéis e vínculo com as unidades.
/// </summary>
public sealed class PeopleService(
    IApplicationDbContext db,
    IClock clock,
    IPasswordHasher passwordHasher,
    EmailComposer composer,
    IOptions<ConviviumOptions> options)
{
    /// <summary>Validade do convite de primeiro acesso.</summary>
    private static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// Validade do link de redefinição de senha. Bem mais curto que o convite:
    /// o convite é combinado com a pessoa, a redefinição pode ter sido pedida
    /// por outro alguém que digitou o e-mail dela.
    /// </summary>
    private static readonly TimeSpan PasswordResetLifetime = TimeSpan.FromHours(1);

    private const int MinimumPasswordLength = 8;

    public async Task<IReadOnlyList<PersonDto>> ListAsync(
        PersonFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        DateOnly hoje = clock.Today;
        DateTimeOffset agora = clock.Now;

        // Parte da pessoa, não do vínculo: o filtro global do DbContext já
        // limita Membership ao condomínio ativo, então o join resolve o escopo.
        var vinculos = await db.Memberships
            .AsNoTracking()
            .Include(m => m.Person)
            .Where(m => m.EndedOn == null || m.EndedOn >= hoje)
            .Where(m => filter.IncludeInactive || m.Person.IsActive)
            .Where(m => filter.Role == null || m.Role == filter.Role)
            .ToListAsync(cancellationToken);

        var idsDasPessoas = vinculos.Select(m => m.PersonId).ToList();

        var ocupacoes = await db.UnitOccupancies
            .AsNoTracking()
            .Include(o => o.Unit).ThenInclude(u => u.Block)
            .Where(o => idsDasPessoas.Contains(o.PersonId))
            .Where(o => o.EndedOn == null || o.EndedOn >= hoje)
            .ToListAsync(cancellationToken);

        var porPessoa = ocupacoes
            .GroupBy(o => o.PersonId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var pessoas = vinculos
            .Select(m =>
            {
                var unidades = porPessoa.GetValueOrDefault(m.PersonId, []);

                return new PersonDto(
                    m.Person.Id,
                    m.Person.Name,
                    m.Person.Email,
                    m.Person.Cpf,
                    m.Person.Phone,
                    m.Role,
                    m.Person.IsActive,
                    m.Person.CanSignIn,
                    m.Person.HasPendingInvite(agora),
                    m.Person.LastLoginAt,
                    unidades
                        .OrderBy(o => o.Unit.Identifier)
                        .Select(o => new PersonUnitDto(
                            o.Id, o.UnitId, o.Unit.FullIdentifier, o.Relation, o.IsBillingResponsible))
                        .ToList());
            })
            .ToList();

        if (filter.OnlyWithoutUnit == true)
        {
            pessoas = pessoas.Where(p => p.Units.Count == 0).ToList();
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            string termo = filter.Search.Trim().ToLowerInvariant();

            pessoas = pessoas
                .Where(p =>
                    p.Name.Contains(termo, StringComparison.OrdinalIgnoreCase)
                    || (p.Email?.Contains(termo, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (p.Cpf?.Contains(termo, StringComparison.Ordinal) ?? false))
                .ToList();
        }

        return pessoas
            .OrderByDescending(p => p.Role)
            .ThenBy(p => p.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    public async Task<PersonDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var pessoas = await ListAsync(new PersonFilter { IncludeInactive = true }, cancellationToken);

        return pessoas.FirstOrDefault(p => p.Id == id)
            ?? throw new KeyNotFoundException("Pessoa não encontrada.");
    }

    public async Task<PersonDto> CreateAsync(
        SavePersonRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string nome = (request.Name ?? string.Empty).Trim();
        DomainException.ThrowIf(nome.Length == 0, "Informe o nome da pessoa.");

        string? email = NormalizeEmail(request.Email);
        string? cpf = NormalizeCpf(request.Cpf);

        // A pessoa pode já existir vinda de outro condomínio: reaproveita o
        // cadastro em vez de duplicar, senão o login dela quebraria.
        Person? pessoa = null;

        if (email is not null)
        {
            pessoa = await db.People.IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.Email == email, cancellationToken);
        }

        if (pessoa is null && cpf is not null)
        {
            pessoa = await db.People.IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.Cpf == cpf, cancellationToken);
        }

        if (pessoa is null)
        {
            pessoa = new Person { Name = nome, Email = email, Cpf = cpf, Phone = Trim(request.Phone) };
            db.People.Add(pessoa);
        }
        else
        {
            bool jaVinculada = await db.Memberships
                .AnyAsync(m => m.PersonId == pessoa.Id, cancellationToken);

            DomainException.ThrowIf(
                jaVinculada,
                $"{pessoa.Name} já está cadastrada neste condomínio.");

            pessoa.Name = nome;
            pessoa.Phone = Trim(request.Phone) ?? pessoa.Phone;
        }

        db.Memberships.Add(new Membership
        {
            Person = pessoa,
            Role = request.Role,
            StartedOn = clock.Today,
        });

        await db.SaveChangesAsync(cancellationToken);

        if (request.UnitId is { } unitId)
        {
            await LinkUnitAsync(
                pessoa.Id,
                new LinkUnitRequest(unitId, request.Relation, request.IsBillingResponsible),
                cancellationToken);
        }

        return await GetAsync(pessoa.Id, cancellationToken);
    }

    public async Task<PersonDto> UpdateAsync(
        Guid id,
        SavePersonRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Person pessoa = await LoadPersonAsync(id, cancellationToken);

        string nome = (request.Name ?? string.Empty).Trim();
        DomainException.ThrowIf(nome.Length == 0, "Informe o nome da pessoa.");

        string? email = NormalizeEmail(request.Email);
        string? cpf = NormalizeCpf(request.Cpf);

        if (email is not null && email != pessoa.Email)
        {
            bool emUso = await db.People.IgnoreQueryFilters()
                .AnyAsync(p => p.Id != id && p.Email == email, cancellationToken);

            DomainException.ThrowIf(emUso, "Este e-mail já está em uso por outra pessoa.");
        }

        if (cpf is not null && cpf != pessoa.Cpf)
        {
            bool emUso = await db.People.IgnoreQueryFilters()
                .AnyAsync(p => p.Id != id && p.Cpf == cpf, cancellationToken);

            DomainException.ThrowIf(emUso, "Este CPF já está em uso por outra pessoa.");
        }

        pessoa.Name = nome;
        pessoa.Email = email;
        pessoa.Cpf = cpf;
        pessoa.Phone = Trim(request.Phone);

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<PersonDto> ChangeRoleAsync(
        Guid id,
        ChangeRoleRequest request,
        CancellationToken cancellationToken = default)
    {
        Membership vinculo = await db.Memberships
            .FirstOrDefaultAsync(m => m.PersonId == id, cancellationToken)
            ?? throw new KeyNotFoundException("Vínculo não encontrado neste condomínio.");

        if (vinculo.Role == MembershipRole.Manager && request.Role != MembershipRole.Manager)
        {
            // Rebaixar o último síndico deixaria o condomínio sem ninguém que
            // pudesse fechar rateio ou alterar configuração.
            int sindicos = await db.Memberships.CountAsync(
                m => m.Role >= MembershipRole.Manager && m.EndedOn == null, cancellationToken);

            DomainException.ThrowIf(
                sindicos <= 1,
                "Este é o único síndico do condomínio. Promova outra pessoa antes de mudar o papel.");
        }

        vinculo.Role = request.Role;
        await db.SaveChangesAsync(cancellationToken);

        return await GetAsync(id, cancellationToken);
    }

    // --- Vínculo com unidade ---

    public async Task<PersonDto> LinkUnitAsync(
        Guid personId,
        LinkUnitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Person pessoa = await LoadPersonAsync(personId, cancellationToken);

        bool unidadeExiste = await db.Units.AnyAsync(u => u.Id == request.UnitId, cancellationToken);
        DomainException.ThrowIf(!unidadeExiste, "Unidade não encontrada.");

        DateOnly hoje = clock.Today;

        bool jaVinculada = await db.UnitOccupancies.AnyAsync(
            o => o.PersonId == personId
              && o.UnitId == request.UnitId
              && (o.EndedOn == null || o.EndedOn >= hoje),
            cancellationToken);

        DomainException.ThrowIf(jaVinculada, $"{pessoa.Name} já está vinculada a esta unidade.");

        var ocupacao = new UnitOccupancy
        {
            PersonId = personId,
            UnitId = request.UnitId,
            Relation = request.Relation,
            IsBillingResponsible = request.IsBillingResponsible,
            StartedOn = hoje,
        };

        db.UnitOccupancies.Add(ocupacao);

        if (request.IsBillingResponsible)
        {
            await ClearOtherBillingResponsiblesAsync(request.UnitId, ocupacao.Id, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(personId, cancellationToken);
    }

    /// <summary>
    /// Define quem recebe a cobrança da unidade.
    /// </summary>
    /// <remarks>
    /// Exatamente um responsável por unidade: com dois, o boleto sairia
    /// duplicado; com nenhum, sairia sem destinatário.
    /// </remarks>
    public async Task<PersonDto> SetBillingResponsibleAsync(
        Guid occupancyId,
        CancellationToken cancellationToken = default)
    {
        UnitOccupancy ocupacao = await db.UnitOccupancies
            .FirstOrDefaultAsync(o => o.Id == occupancyId, cancellationToken)
            ?? throw new KeyNotFoundException("Vínculo não encontrado.");

        ocupacao.IsBillingResponsible = true;
        await ClearOtherBillingResponsiblesAsync(ocupacao.UnitId, occupancyId, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(ocupacao.PersonId, cancellationToken);
    }

    public async Task<PersonDto> UnlinkUnitAsync(
        Guid occupancyId,
        CancellationToken cancellationToken = default)
    {
        UnitOccupancy ocupacao = await db.UnitOccupancies
            .FirstOrDefaultAsync(o => o.Id == occupancyId, cancellationToken)
            ?? throw new KeyNotFoundException("Vínculo não encontrado.");

        Guid personId = ocupacao.PersonId;

        // Encerra em vez de apagar: o histórico precisa mostrar quem morava na
        // unidade quando cada cobrança foi emitida.
        ocupacao.EndedOn = clock.Today;
        ocupacao.IsBillingResponsible = false;

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(personId, cancellationToken);
    }

    private async Task ClearOtherBillingResponsiblesAsync(
        Guid unitId,
        Guid manter,
        CancellationToken cancellationToken)
    {
        DateOnly hoje = clock.Today;

        var outras = await db.UnitOccupancies
            .Where(o => o.UnitId == unitId && o.Id != manter)
            .Where(o => o.EndedOn == null || o.EndedOn >= hoje)
            .Where(o => o.IsBillingResponsible)
            .ToListAsync(cancellationToken);

        foreach (UnitOccupancy outra in outras)
        {
            outra.IsBillingResponsible = false;
        }
    }

    // --- Acesso ao sistema ---

    /// <summary>
    /// Gera o convite de primeiro acesso e enfileira o e-mail com o link.
    /// </summary>
    public async Task<InviteResult> InviteAsync(
        Guid personId,
        CancellationToken cancellationToken = default)
    {
        Person pessoa = await LoadPersonAsync(personId, cancellationToken);

        DomainException.ThrowIf(
            string.IsNullOrWhiteSpace(pessoa.Email),
            $"{pessoa.Name} está sem e-mail cadastrado. Preencha antes de enviar o convite.");

        DomainException.ThrowIf(!pessoa.IsActive, "Esta pessoa está desativada.");

        string token = InviteToken.Generate();
        DateTimeOffset expiraEm = clock.Now.Add(InviteLifetime);

        pessoa.InviteTokenHash = InviteToken.Hash(token);
        pessoa.InviteTokenExpiresAt = expiraEm;

        string link = BuildInviteUrl(token);

        Condominium condominio = await db.Condominiums.AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new DomainException("Nenhum condomínio ativo no contexto da requisição.");

        // O corpo do convite é montado aqui, e não no envio como acontece com
        // as cobranças: ele não muda com o tempo, e o token só existe neste
        // ponto — depois daqui, o banco guarda apenas o hash dele.
        EmailContent conteudo = composer.ComposeWelcome(condominio.Name, pessoa.Name, link);

        db.EmailMessages.Add(new EmailMessage
        {
            // Explicito porque EmailMessage nao e ITenantScoped — o CondominiumId
            // dela e anulavel, para caber mensagem da plataforma — e portanto o
            // SaveChanges nao carimba sozinho. Sem esta linha o convite ficava
            // orfao: fora da fila do condominio e saindo com o remetente padrao
            // em vez do nome do predio.
            CondominiumId = condominio.Id,
            Kind = EmailKind.Welcome,
            ToAddress = pessoa.Email!,
            ToName = pessoa.Name,
            Subject = conteudo.Subject,
            HtmlBody = conteudo.HtmlBody,
            TextBody = conteudo.TextBody,
            ScheduledFor = clock.Now,
        });

        await db.SaveChangesAsync(cancellationToken);

        return new InviteResult(pessoa.Id, pessoa.Email!, expiraEm, link);
    }

    /// <summary>
    /// Gera o link de redefinição pedido na tela de login e enfileira o e-mail.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rota anônima, então tudo aqui ignora o filtro de condomínio: quem
    /// esqueceu a senha não tem sessão e portanto não tem condomínio ativo.
    /// </para>
    /// <para>
    /// Nunca diz se o e-mail existe, nem por retorno nem por demora
    /// perceptível — e-mail desconhecido sai por aqui em silêncio. Responder
    /// "não encontrado" transformaria esta tela num verificador de cadastro:
    /// bastaria alguém enfileirar endereços para descobrir quem mora no
    /// prédio, e nome de morador com apartamento é dado pessoal.
    /// </para>
    /// <para>
    /// O token mora no mesmo campo do convite, que é de uso único. Pedir
    /// redefinição derruba um convite pendente, e é o comportamento certo:
    /// existe um caminho de acesso por pessoa de cada vez.
    /// </para>
    /// </remarks>
    public async Task RequestPasswordResetAsync(
        ForgotPasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? email = NormalizeEmail(request.Email);

        if (email is null)
        {
            return;
        }

        Person? pessoa = await db.People
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Email == email, cancellationToken);

        if (pessoa is null || !pessoa.IsActive)
        {
            return;
        }

        string token = InviteToken.Generate();
        DateTimeOffset expiraEm = clock.Now.Add(PasswordResetLifetime);

        pessoa.InviteTokenHash = InviteToken.Hash(token);
        pessoa.InviteTokenExpiresAt = expiraEm;

        // O condomínio serve para assinar o remetente e para a mensagem cair
        // na fila certa. Quem tem vínculo em mais de um recebe pelo primeiro;
        // super admin sem vínculo nenhum sai com o remetente da plataforma.
        Condominium? condominio = await db.Memberships
            .IgnoreQueryFilters()
            .Where(m => m.PersonId == pessoa.Id)
            .Where(m => m.EndedOn == null || m.EndedOn >= clock.Today)
            .OrderBy(m => m.StartedOn)
            .Select(m => m.Condominium)
            .FirstOrDefaultAsync(cancellationToken);

        EmailContent conteudo = composer.ComposePasswordReset(
            condominio?.Name ?? "Logement",
            pessoa.Name,
            BuildInviteUrl(token),
            PasswordResetLifetime);

        db.EmailMessages.Add(new EmailMessage
        {
            CondominiumId = condominio?.Id,
            Kind = EmailKind.PasswordReset,
            ToAddress = pessoa.Email!,
            ToName = pessoa.Name,
            Subject = conteudo.Subject,
            HtmlBody = conteudo.HtmlBody,
            TextBody = conteudo.TextBody,
            ScheduledFor = clock.Now,
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Troca o token do convite pela senha escolhida pela pessoa.
    /// </summary>
    /// <remarks>
    /// Ignora o filtro de condomínio: quem abre o link do convite ainda não
    /// tem sessão nem condomínio ativo.
    /// </remarks>
    public async Task SetPasswordAsync(
        SetPasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        DomainException.ThrowIf(
            string.IsNullOrWhiteSpace(request.Password)
                || request.Password.Length < MinimumPasswordLength,
            $"A senha precisa ter pelo menos {MinimumPasswordLength} caracteres.");

        string hash = InviteToken.Hash(request.Token ?? string.Empty);

        Person? pessoa = await db.People
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.InviteTokenHash == hash, cancellationToken);

        DomainException.ThrowIf(
            pessoa is null || pessoa.InviteTokenExpiresAt <= clock.Now,
            "Link inválido, expirado ou já usado. Peça outro em \"Esqueci minha senha\", "
            + "na tela de entrada.");

        DomainException.ThrowIf(!pessoa.IsActive, "Este acesso está desativado.");

        pessoa.PasswordHash = passwordHasher.Hash(request.Password);

        // Convite é de uso único: consumido, some.
        pessoa.InviteTokenHash = null;
        pessoa.InviteTokenExpiresAt = null;

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PersonDto> DeactivateAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Person pessoa = await LoadPersonAsync(id, cancellationToken);

        Membership? vinculo = await db.Memberships
            .FirstOrDefaultAsync(m => m.PersonId == id, cancellationToken);

        if (vinculo?.Role >= MembershipRole.Manager)
        {
            int sindicos = await db.Memberships.CountAsync(
                m => m.Role >= MembershipRole.Manager && m.EndedOn == null, cancellationToken);

            DomainException.ThrowIf(
                sindicos <= 1,
                "Este é o único síndico do condomínio. Promova outra pessoa antes de desativar.");
        }

        pessoa.IsActive = false;
        pessoa.InviteTokenHash = null;
        pessoa.InviteTokenExpiresAt = null;

        if (vinculo is not null)
        {
            vinculo.EndedOn = clock.Today;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    // --- Apoio ---

    private async Task<Person> LoadPersonAsync(Guid id, CancellationToken cancellationToken)
    {
        bool pertence = await db.Memberships.AnyAsync(m => m.PersonId == id, cancellationToken);
        DomainException.ThrowIf(!pertence, "Pessoa não encontrada neste condomínio.");

        return await db.People.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Pessoa não encontrada.");
    }

    private string BuildInviteUrl(string token)
    {
        ConviviumOptions opcoes = options.Value;
        return $"{opcoes.PublicBaseUrl.TrimEnd('/')}/definir-senha/{token}";
    }

    private static string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    private static string? NormalizeCpf(string? cpf)
    {
        string? digits = BrazilianDocument.OnlyDigits(cpf);

        if (digits is null)
        {
            return null;
        }

        DomainException.ThrowIf(
            !BrazilianDocument.IsValidCpf(digits),
            "CPF inválido: confira os dígitos.");

        return digits;
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}
