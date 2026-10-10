namespace Convivium.Application.Banking;

using System.Globalization;
using System.Text;
using Convivium.Application.Abstractions;
using Convivium.Application.Billing;
using Convivium.Domain.Billing;
using Convivium.Domain.Common;
using Convivium.Domain.Condominiums;
using Convivium.Domain.Payments;
using Convivium.Domain.People;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Boleto registrado em banco: configuracao, emissao, acompanhamento e baixa.
/// </summary>
/// <remarks>
/// <para>
/// O cuidado central daqui e nunca registrar dois titulos para a mesma
/// cobranca. Dois boletos validos na mao do morador viram pagamento em dobro
/// ou, no minimo, uma conversa constrangedora. Por isso a emissao "reserva" a
/// cobranca antes de falar com o banco, e uma resposta que nao chegou deixa a
/// cobranca travada ate alguem conferir no internet banking — em vez de
/// liberar uma segunda tentativa as cegas.
/// </para>
/// <para>
/// Quando o banco avisa que o boleto foi pago, o recebimento entra no caixa
/// pelo mesmo caminho do recebimento manual, para a prestacao de contas nao
/// ter dois jeitos de explicar a mesma entrada.
/// </para>
/// </remarks>
public sealed class BoletoService(
    IApplicationDbContext db,
    IClock clock,
    ISecretProtector protector,
    IEnumerable<IBoletoProvider> providers,
    IBankHttpClientSource connections,
    BillingService billing)
{
    /// <summary>Com menos de 30 dias de validade, o certificado vira aviso na tela.</summary>
    private static readonly TimeSpan CertificateWarningWindow = TimeSpan.FromDays(30);

    private static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");

    // --- Configuracao ---

    public async Task<BoletoSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        Condominium condominium = await LoadCondominiumAsync(tracking: false, cancellationToken);
        return ToDto(condominium.Boleto);
    }

    /// <summary>
    /// Grava a configuracao do banco. Escolher "nenhum banco" apaga as
    /// credenciais em vez de so desligar: credencial bancaria parada no banco
    /// de dados e risco sem utilidade nenhuma.
    /// </summary>
    public async Task<BoletoSettingsDto> UpdateSettingsAsync(
        UpdateBoletoSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Condominium condominium = await LoadCondominiumAsync(tracking: true, cancellationToken);
        BoletoSettings settings = condominium.Boleto;

        if (request.Provider is BoletoProvider.None)
        {
            condominium.Boleto = new BoletoSettings();
            connections.Invalidate(condominium.Id.ToString("N"));
            await db.SaveChangesAsync(cancellationToken);

            return ToDto(condominium.Boleto);
        }

        DomainException.ThrowIf(
            !Enum.IsDefined(request.Provider),
            "Banco não suportado.");

        DomainException.ThrowIf(
            providers.All(p => p.Provider != request.Provider),
            "Este banco ainda não tem integração no sistema.");

        DomainException.ThrowIf(
            !protector.IsAvailable,
            "O servidor está sem chave de criptografia (App:SecretsKey) e não pode guardar " +
            "credenciais bancárias. Peça a quem administra o servidor para configurá-la.");

        string clientId = request.ClientId?.Trim() ?? string.Empty;
        DomainException.ThrowIf(clientId.Length == 0, "Informe o client id da aplicação no banco.");

        DomainException.ThrowIf(
            request.DaysOnAgenda is < 0 or > 60,
            "Os dias para pagamento após o vencimento vão de 0 a 60.");

        bool credentialsChanged =
            settings.Provider != request.Provider
            || settings.Sandbox != request.Sandbox
            || !string.Equals(settings.ClientId, clientId, StringComparison.Ordinal);

        settings.Provider = request.Provider;
        settings.Sandbox = request.Sandbox;
        settings.ClientId = clientId;

        if (!string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            settings.ClientSecretCipher = protector.Protect(
                request.ClientSecret.Trim(),
                Purpose(condominium.Id, "client-secret"));
            credentialsChanged = true;
        }

        DomainException.ThrowIf(
            string.IsNullOrWhiteSpace(settings.ClientSecretCipher),
            "Informe o client secret da aplicação no banco.");

        ClientCertificate.Normalized? certificate = ReadCertificate(request);

        if (certificate is not null)
        {
            settings.CertificateCipher = protector.Protect(
                Convert.ToBase64String(certificate.Pkcs12),
                Purpose(condominium.Id, "certificate"));

            settings.CertificatePasswordCipher = certificate.Password is null
                ? null
                : protector.Protect(certificate.Password, Purpose(condominium.Id, "certificate-password"));

            settings.CertificateSubject = certificate.Subject;
            settings.CertificateExpiresAt = certificate.ExpiresAt;
            credentialsChanged = true;
        }

        DomainException.ThrowIf(
            string.IsNullOrWhiteSpace(settings.CertificateCipher),
            "Envie o certificado da aplicação no banco: o arquivo .crt e o .key, ou um .pfx.");

        if (request.BankAccountId is { } accountId)
        {
            bool accountExists = await db.BankAccounts
                .AnyAsync(a => a.Id == accountId && a.IsActive, cancellationToken);

            DomainException.ThrowIf(!accountExists, "Conta do caixa não encontrada ou inativa.");
        }

        settings.BankAccountId = request.BankAccountId;
        settings.CheckingAccount = BrazilianDocument.OnlyDigits(request.CheckingAccount) is { Length: > 0 } account
            ? account
            : null;
        settings.DaysOnAgenda = request.DaysOnAgenda;
        settings.Instructions = NormalizeInstructions(request.Instructions);

        if (credentialsChanged)
        {
            settings.VerifiedAt = null;
            connections.Invalidate(condominium.Id.ToString("N"));
        }

        await db.SaveChangesAsync(cancellationToken);

        return ToDto(settings);
    }

    /// <summary>
    /// Pede um token ao banco com as credenciais guardadas, sem emitir nada.
    /// E o "testar conexao" da tela de configuracao.
    /// </summary>
    public async Task<BoletoSettingsDto> VerifyAsync(CancellationToken cancellationToken = default)
    {
        Condominium condominium = await LoadCondominiumAsync(tracking: true, cancellationToken);
        (IBoletoProvider provider, BankCredentials credentials) = Connect(condominium);

        await provider.VerifyAsync(credentials, cancellationToken);

        condominium.Boleto.VerifiedAt = clock.Now;
        await db.SaveChangesAsync(cancellationToken);

        return ToDto(condominium.Boleto);
    }

    // --- Prontidao ---

    /// <summary>
    /// Confere, sem falar com o banco, o que impede de emitir os boletos do ciclo.
    /// </summary>
    public async Task<BoletoReadinessDto> GetReadinessAsync(
        Guid cycleId,
        CancellationToken cancellationToken = default)
    {
        Condominium condominium = await LoadCondominiumAsync(tracking: false, cancellationToken);
        List<string> settingsProblems = SettingsProblems(condominium);

        List<Charge> charges = await LoadCycleChargesAsync(cycleId, tracking: false, cancellationToken);

        var chargeProblems = new List<BoletoBatchItem>();
        int ready = 0;

        foreach (Charge charge in charges)
        {
            if (ChargeProblem(charge) is { } problem)
            {
                chargeProblems.Add(new BoletoBatchItem(charge.Id, UnitName(charge), problem));
            }
            else
            {
                ready++;
            }
        }

        return new BoletoReadinessDto(
            settingsProblems.Count == 0 && ready > 0,
            settingsProblems,
            chargeProblems,
            ready);
    }

    // --- Emissao ---

    /// <summary>Registra no banco o boleto de uma cobranca.</summary>
    public async Task<ChargeDto> IssueAsync(Guid chargeId, CancellationToken cancellationToken = default)
    {
        Condominium condominium = await LoadCondominiumAsync(tracking: false, cancellationToken);
        RequireSettings(condominium);

        (IBoletoProvider provider, BankCredentials credentials) = Connect(condominium);

        Charge charge = await LoadChargeAsync(chargeId, cancellationToken);

        if (ChargeProblem(charge) is { } problem)
        {
            throw new DomainException(problem);
        }

        await IssueOneAsync(charge, condominium, provider, credentials, cancellationToken);

        return await billing.GetChargeAsync(chargeId, cancellationToken);
    }

    /// <summary>
    /// Emite os boletos de todas as cobrancas do ciclo que estiverem prontas.
    /// Uma unidade com problema nao impede as outras.
    /// </summary>
    public async Task<BoletoBatchResult> IssueForCycleAsync(
        Guid cycleId,
        CancellationToken cancellationToken = default)
    {
        Condominium condominium = await LoadCondominiumAsync(tracking: false, cancellationToken);
        RequireSettings(condominium);

        (IBoletoProvider provider, BankCredentials credentials) = Connect(condominium);

        // Credencial errada falharia em todas as unidades, uma por uma. Testar
        // antes transforma trinta erros iguais em um so, e nada vai ao banco.
        await provider.VerifyAsync(credentials, cancellationToken);

        List<Charge> charges = await LoadCycleChargesAsync(cycleId, tracking: true, cancellationToken);

        int issued = 0;
        int pending = 0;
        var skipped = new List<BoletoBatchItem>();
        var failed = new List<BoletoBatchItem>();

        foreach (Charge charge in charges)
        {
            if (ChargeProblem(charge) is { } problem)
            {
                // Quem ja tem boleto, ja pagou ou foi cancelado nao e problema:
                // so nao entra no lote. O resto e pendencia que o sindico resolve.
                if (!IsAlreadyHandled(charge))
                {
                    skipped.Add(new BoletoBatchItem(charge.Id, UnitName(charge), problem));
                }

                continue;
            }

            try
            {
                BoletoState state = await IssueOneAsync(charge, condominium, provider, credentials, cancellationToken);

                if (state is BoletoState.Registered)
                {
                    issued++;
                }
                else
                {
                    pending++;
                }
            }
            catch (Exception exception) when (exception is BoletoProviderException or DomainException)
            {
                failed.Add(new BoletoBatchItem(charge.Id, UnitName(charge), exception.Message));

                // Resposta perdida no meio do lote: o banco pode estar fora.
                // Continuar empilharia cobrancas travadas para conferir uma a uma.
                if (exception is BoletoProviderException { OutcomeUnknown: true })
                {
                    break;
                }
            }
        }

        return new BoletoBatchResult(issued, pending, skipped, failed);
    }

    /// <summary>
    /// O coracao da emissao: reserva a cobranca, fala com o banco e grava o
    /// que voltou. A cobranca precisa estar sendo rastreada pelo contexto.
    /// </summary>
    private async Task<BoletoState> IssueOneAsync(
        Charge charge,
        Condominium condominium,
        IBoletoProvider provider,
        BankCredentials credentials,
        CancellationToken cancellationToken)
    {
        BoletoState? previous = charge.SlipState;
        string? previousExternalId = charge.ExternalSlipId;
        decimal? previousAmount = charge.SlipAmount;

        // Valor fechado aqui e usado nos dois lugares: no pedido e na
        // conferencia do codigo de barras que o banco devolver.
        decimal amount = decimal.Round(charge.OutstandingAmount, 2, MidpointRounding.AwayFromZero);

        // Reserva atomica: so passa quem encontrou a cobranca sem boleto vivo.
        // Dois cliques em "emitir" ao mesmo tempo nao podem virar dois titulos.
        int claimed = await db.Charges
            .Where(c => c.Id == charge.Id)
            .Where(c => c.SlipState == null
                || c.SlipState == BoletoState.Cancelled
                || c.SlipState == BoletoState.Expired)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(c => c.SlipState, BoletoState.Pending)
                    .SetProperty(c => c.SlipIssuedAt, clock.Now)
                    .SetProperty(c => c.SlipAmount, amount)
                    .SetProperty(c => c.ExternalSlipId, (string?)null),
                cancellationToken);

        DomainException.ThrowIf(
            claimed == 0,
            $"{UnitName(charge)}: outra emissão deste boleto já está em andamento.");

        // O ExecuteUpdate grava direto no banco, por fora do rastreamento. A
        // entidade em memoria recebe os mesmos valores para que o proximo
        // SaveChanges grave o que ja esta la, e nao desfaca a reserva.
        charge.SlipState = BoletoState.Pending;
        charge.SlipIssuedAt = clock.Now;
        charge.SlipAmount = amount;
        charge.ExternalSlipId = null;

        BoletoRegistration registration;

        try
        {
            registration = await provider.IssueAsync(
                BuildRequest(charge, condominium, amount),
                credentials,
                cancellationToken);
        }
        catch (BoletoProviderException exception) when (exception.SlipMismatch)
        {
            throw await ResolveMismatchAsync(charge, provider, credentials, exception, cancellationToken);
        }
        catch (BoletoProviderException exception) when (exception.OutcomeUnknown)
        {
            // Fica pendente e sem identificador: a tela pede conferencia no
            // internet banking antes de liberar uma nova emissao.
            throw;
        }
        catch (Exception exception) when (exception is BoletoProviderException or DomainException)
        {
            // O banco recusou de forma clara: nada foi registrado, entao a
            // cobranca volta a ficar livre para uma nova tentativa.
            await db.Charges
                .Where(c => c.Id == charge.Id)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(c => c.SlipState, previous)
                        .SetProperty(c => c.SlipIssuedAt, (DateTimeOffset?)null)
                        .SetProperty(c => c.SlipAmount, previousAmount)
                        .SetProperty(c => c.ExternalSlipId, previousExternalId),
                    cancellationToken);

            charge.SlipState = previous;
            charge.SlipIssuedAt = null;
            charge.SlipAmount = previousAmount;
            charge.ExternalSlipId = previousExternalId;

            throw;
        }

        Apply(charge, registration);
        await db.SaveChangesAsync(cancellationToken);

        return registration.State;
    }

    /// <summary>
    /// O banco registrou um titulo diferente do pedido. Ele e pagavel — so que
    /// errado —, entao e cancelado no banco na hora. Se nem o cancelamento
    /// passar, a cobranca fica pendente com o identificador do titulo, e cada
    /// conferencia seguinte tenta cancelar de novo.
    /// </summary>
    /// <returns>A excecao a lancar, com a mensagem do que foi feito.</returns>
    private async Task<BoletoProviderException> ResolveMismatchAsync(
        Charge charge,
        IBoletoProvider provider,
        BankCredentials credentials,
        BoletoProviderException mismatch,
        CancellationToken cancellationToken)
    {
        charge.ExternalSlipId = mismatch.ExternalId ?? charge.ExternalSlipId;
        charge.SlipBarcode = null;
        charge.BarcodeLine = null;
        charge.SlipPixPayload = null;

        bool cancelled = false;

        if (charge.ExternalSlipId is { } externalId)
        {
            try
            {
                await provider.CancelAsync(externalId, "ACERTOS", credentials, cancellationToken);
                cancelled = true;
            }
            catch (BoletoProviderException)
            {
                // Fica pendente com o identificador: a proxima conferencia tenta de novo.
            }
        }

        charge.SlipState = cancelled ? BoletoState.Cancelled : BoletoState.Pending;
        charge.SlipSyncedAt = clock.Now;
        await db.SaveChangesAsync(cancellationToken);

        string unit = UnitName(charge);
        string outcome = cancelled
            ? "O título foi cancelado no banco para ninguém pagar o valor errado. Confira a cobrança e emita de novo."
            : "Não consegui cancelar o título: cancele no internet banking antes que alguém pague. " +
              "O sistema tenta cancelar de novo na próxima conferência.";

        return new BoletoProviderException(
            $"{unit}: {mismatch.Message} {outcome}",
            mismatch,
            slipMismatch: true,
            externalId: charge.ExternalSlipId);
    }

    private BoletoIssueRequest BuildRequest(Charge charge, Condominium condominium, decimal amount)
    {
        Person payer = charge.Payer!;
        Address address = condominium.Address;
        string unit = UnitName(charge);

        // O endereco do pagador e o do proprio condominio, com a unidade no
        // complemento: e para la que o boleto de condominio se destina.
        string complement = string.IsNullOrWhiteSpace(address.Complement)
            ? unit
            : $"{unit} - {address.Complement}";

        return new BoletoIssueRequest
        {
            OwnNumber = OwnNumber(charge),
            Amount = amount,
            DueDate = charge.DueDate,
            DaysOnAgenda = condominium.Boleto.DaysOnAgenda,
            LateFeeRate = condominium.Billing.LateFeeRate,
            MonthlyInterestRate = condominium.Billing.MonthlyInterestRate,
            Instructions = BoletoInstructions.Build(charge.Competence, unit, condominium),
            Payer = new BoletoPayer
            {
                Name = payer.Name,
                Document = BrazilianDocument.OnlyDigits(payer.Cpf) ?? string.Empty,
                Email = payer.Email,
                Street = address.Street,
                Number = address.Number,
                Complement = complement,
                District = address.District,
                City = address.City,
                State = address.State,
                ZipCode = address.ZipCode,
            },
        };
    }

    /// <summary>
    /// O "seu numero" do titulo: competencia e unidade, legivel no extrato do
    /// banco. Ex.: "2610-A101". Nao precisa ser unico — quem identifica o
    /// titulo e o codigo que o banco devolve.
    /// </summary>
    private static string OwnNumber(Charge charge)
    {
        string unit = new(UnitName(charge).Where(char.IsAsciiLetterOrDigit).ToArray());
        string value = $"{charge.Competence.Year % 100:00}{charge.Competence.Month:00}-{unit}".ToUpperInvariant();

        return value.Length <= 15 ? value : value[..15];
    }

    // --- Acompanhamento ---

    /// <summary>Confere uma cobranca com o banco e da baixa se ela foi paga.</summary>
    public async Task<ChargeDto> SyncAsync(Guid chargeId, CancellationToken cancellationToken = default)
    {
        Condominium condominium = await LoadCondominiumAsync(tracking: false, cancellationToken);
        (IBoletoProvider provider, BankCredentials credentials) = Connect(condominium);

        Charge charge = await LoadChargeAsync(chargeId, cancellationToken);

        DomainException.ThrowIf(
            string.IsNullOrWhiteSpace(charge.ExternalSlipId),
            "Esta cobrança não tem boleto no banco para conferir.");

        await SyncOneAsync(charge, condominium, provider, credentials, cancellationToken);

        return await billing.GetChargeAsync(chargeId, cancellationToken);
    }

    /// <summary>
    /// Confere com o banco todos os boletos que ainda podem mudar de estado.
    /// E o que a rotina em segundo plano chama, condominio a condominio.
    /// </summary>
    public async Task<BoletoSyncResult> SyncOpenAsync(CancellationToken cancellationToken = default)
    {
        Condominium condominium = await LoadCondominiumAsync(tracking: false, cancellationToken);

        if (!condominium.Boleto.IsConfigured || !protector.IsAvailable)
        {
            return new BoletoSyncResult(0, 0, 0, []);
        }

        (IBoletoProvider provider, BankCredentials credentials) = Connect(condominium);

        List<Guid> ids = await db.Charges
            .AsNoTracking()
            .Where(c => c.ExternalSlipId != null)
            .Where(c =>
                c.SlipState == BoletoState.Pending
                || c.SlipState == BoletoState.Registered
                || (c.SlipState == BoletoState.Paid && c.Status != ChargeStatus.Paid))
            .OrderBy(c => c.DueDate)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        int changed = 0;
        int payments = 0;
        var errors = new List<string>();

        foreach (Guid id in ids)
        {
            Charge charge = await LoadChargeAsync(id, cancellationToken);
            BoletoState? before = charge.SlipState;
            ChargeStatus statusBefore = charge.Status;

            try
            {
                await SyncOneAsync(charge, condominium, provider, credentials, cancellationToken);
            }
            catch (Exception exception) when (exception is BoletoProviderException or DomainException)
            {
                errors.Add($"{UnitName(charge)}: {exception.Message}");
                continue;
            }

            if (charge.SlipState != before)
            {
                changed++;
            }

            if (statusBefore != ChargeStatus.Paid && charge.Status == ChargeStatus.Paid)
            {
                payments++;
            }
        }

        return new BoletoSyncResult(ids.Count, changed, payments, errors);
    }

    private async Task SyncOneAsync(
        Charge charge,
        Condominium condominium,
        IBoletoProvider provider,
        BankCredentials credentials,
        CancellationToken cancellationToken)
    {
        // Enquanto o banco nao devolveu o codigo de barras, o primeiro que vier
        // ainda precisa ser conferido contra o valor pedido na emissao.
        BoletoExpectation? expected = charge.SlipBarcode is null && charge.SlipAmount is { } requested
            ? new BoletoExpectation(requested, charge.DueDate)
            : null;

        BoletoRegistration registration;

        try
        {
            registration = await provider.QueryAsync(
                charge.ExternalSlipId!,
                credentials,
                expected,
                cancellationToken);
        }
        catch (BoletoProviderException exception) when (exception.SlipMismatch)
        {
            throw await ResolveMismatchAsync(charge, provider, credentials, exception, cancellationToken);
        }

        Apply(charge, registration);
        charge.SlipSyncedAt = clock.Now;

        await db.SaveChangesAsync(cancellationToken);

        if (registration.State is BoletoState.Paid && charge.Status is not ChargeStatus.Paid)
        {
            await RegisterBankPaymentAsync(charge, condominium, registration, cancellationToken);
        }
    }

    /// <summary>
    /// Lanca no caixa o pagamento que o banco informou, uma vez so.
    /// </summary>
    private async Task RegisterBankPaymentAsync(
        Charge charge,
        Condominium condominium,
        BoletoRegistration registration,
        CancellationToken cancellationToken)
    {
        // Sem conta escolhida, o boleto aparece como pago no banco e a
        // cobranca espera: a proxima conferencia lanca quando a conta existir.
        if (condominium.Boleto.BankAccountId is not { } bankAccountId)
        {
            return;
        }

        // A rotina em segundo plano e o botao "atualizar" podem rodar juntos;
        // o identificador do titulo no pagamento impede a baixa em dobro.
        bool alreadyRegistered = await db.Payments
            .AnyAsync(p => p.ChargeId == charge.Id && p.ExternalId == charge.ExternalSlipId, cancellationToken);

        if (alreadyRegistered)
        {
            return;
        }

        decimal amount = registration.PaidAmount is { } paid && paid > 0
            ? paid
            : charge.OutstandingAmount;

        if (amount <= 0)
        {
            return;
        }

        await billing.RegisterPaymentAsync(
            charge.Id,
            new RegisterPaymentRequest(
                amount,
                bankAccountId,
                registration.PaidOn ?? clock.Today,
                PaymentMethod.BankSlip,
                charge.ExternalSlipId,
                "Baixa automática: pagamento informado pelo banco."),
            registeredByPersonId: null,
            cancellationToken);
    }

    // --- Cancelamento ---

    /// <summary>
    /// Baixa o titulo no banco. A cobranca continua existindo: e o caminho
    /// para emitir uma segunda via, ou para cancelar a cobranca em seguida.
    /// </summary>
    public async Task<ChargeDto> CancelAsync(Guid chargeId, CancellationToken cancellationToken = default)
    {
        Condominium condominium = await LoadCondominiumAsync(tracking: false, cancellationToken);
        Charge charge = await LoadChargeAsync(chargeId, cancellationToken);

        DomainException.ThrowIf(
            charge.SlipState is not (BoletoState.Pending or BoletoState.Registered),
            "Esta cobrança não tem boleto em aberto no banco.");

        DomainException.ThrowIf(
            string.IsNullOrWhiteSpace(charge.ExternalSlipId),
            "A emissão deste boleto ficou sem resposta do banco. Confira no internet banking: " +
            "se o título existir, cancele por lá; se não existir, use \"liberar para nova emissão\".");

        (IBoletoProvider provider, BankCredentials credentials) = Connect(condominium);

        await provider.CancelAsync(charge.ExternalSlipId, "ACERTOS", credentials, cancellationToken);

        charge.SlipState = BoletoState.Cancelled;
        charge.SlipSyncedAt = clock.Now;
        await db.SaveChangesAsync(cancellationToken);

        return await billing.GetChargeAsync(chargeId, cancellationToken);
    }

    /// <summary>
    /// Destrava uma cobranca cuja emissao ficou sem resposta, depois que alguem
    /// conferiu no internet banking que nenhum titulo foi registrado.
    /// </summary>
    public async Task<ChargeDto> ReleaseAsync(Guid chargeId, CancellationToken cancellationToken = default)
    {
        Charge charge = await LoadChargeAsync(chargeId, cancellationToken);

        DomainException.ThrowIf(
            charge.SlipState is not BoletoState.Pending || !string.IsNullOrWhiteSpace(charge.ExternalSlipId),
            "Só dá para liberar um boleto cuja emissão ficou sem resposta do banco.");

        charge.SlipState = null;
        charge.SlipIssuedAt = null;
        await db.SaveChangesAsync(cancellationToken);

        return await billing.GetChargeAsync(chargeId, cancellationToken);
    }

    /// <summary>O PDF do boleto desenhado pelo proprio banco.</summary>
    public async Task<byte[]> DownloadBankPdfAsync(Guid chargeId, CancellationToken cancellationToken = default)
    {
        Condominium condominium = await LoadCondominiumAsync(tracking: false, cancellationToken);

        string externalId = await db.Charges
            .AsNoTracking()
            .Where(c => c.Id == chargeId)
            .Select(c => c.ExternalSlipId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("Esta cobrança não tem boleto no banco.");

        (IBoletoProvider provider, BankCredentials credentials) = Connect(condominium);

        return await provider.DownloadPdfAsync(externalId, credentials, cancellationToken);
    }

    // --- Regras ---

    /// <summary>
    /// Por que esta cobranca nao pode virar boleto agora. Nulo quando pode.
    /// </summary>
    private string? ChargeProblem(Charge charge)
    {
        string unit = UnitName(charge);

        return charge switch
        {
            { Status: ChargeStatus.Cancelled } => "cobrança cancelada.",
            { Status: ChargeStatus.Paid } => "cobrança já quitada.",
            { SlipState: BoletoState.Registered or BoletoState.Paid } => "já tem boleto registrado.",
            { SlipState: BoletoState.Pending, ExternalSlipId: null } =>
                "a emissão anterior ficou sem resposta do banco; confira no internet banking.",
            { SlipState: BoletoState.Pending } => "boleto em processamento no banco.",
            { OutstandingAmount: <= 0 } => "nada a cobrar.",
            _ when charge.DueDate < clock.Today =>
                $"venceu em {charge.DueDate.ToString("dd/MM/yyyy", Brazil)} e o banco não registra " +
                "boleto vencido. Mande o PIX, que já calcula multa e juros.",
            { Payer: null } => $"{unit} sem responsável pela cobrança.",
            _ when string.IsNullOrWhiteSpace(charge.Payer!.Cpf) =>
                $"{charge.Payer.Name} está sem CPF, e o banco não registra boleto sem o documento de quem paga.",
            _ when !BrazilianDocument.IsValidCpfOrCnpj(charge.Payer!.Cpf) =>
                $"o CPF de {charge.Payer.Name} está com os dígitos errados.",
            _ => null,
        };
    }

    private static bool IsAlreadyHandled(Charge charge) =>
        charge.Status is ChargeStatus.Paid or ChargeStatus.Cancelled
        || charge.SlipState is BoletoState.Registered or BoletoState.Paid
        || (charge.SlipState is BoletoState.Pending && charge.ExternalSlipId is not null);

    private List<string> SettingsProblems(Condominium condominium)
    {
        var problems = new List<string>();
        BoletoSettings settings = condominium.Boleto;

        if (!protector.IsAvailable)
        {
            problems.Add("O servidor está sem chave de criptografia (App:SecretsKey) para as credenciais do banco.");
        }

        if (!settings.IsConfigured)
        {
            problems.Add("Configure o banco do condomínio: client id, client secret e certificado.");
        }
        else if (settings.CertificateExpiresAt is { } expires && expires <= clock.Now)
        {
            problems.Add($"O certificado do banco venceu em {expires.ToString("dd/MM/yyyy", Brazil)}.");
        }

        if (settings.IsConfigured && settings.BankAccountId is null)
        {
            problems.Add(
                "Escolha a conta do caixa onde entram os boletos pagos; sem ela, a baixa " +
                "automática não lança o recebimento.");
        }

        Address address = condominium.Address;

        if (string.IsNullOrWhiteSpace(address.Street)
            || string.IsNullOrWhiteSpace(address.City)
            || string.IsNullOrWhiteSpace(address.State)
            || BrazilianDocument.OnlyDigits(address.ZipCode) is not { Length: 8 })
        {
            problems.Add(
                "Complete o endereço do condomínio (rua, cidade, UF e CEP): ele vai como " +
                "endereço do pagador no boleto.");
        }

        return problems;
    }

    /// <summary>
    /// Os problemas de configuracao que impedem qualquer emissao. A conta do
    /// caixa fica de fora: sem ela da para emitir, so nao da para lancar a baixa.
    /// </summary>
    private void RequireSettings(Condominium condominium)
    {
        string? blocking = SettingsProblems(condominium)
            .FirstOrDefault(p => !p.StartsWith("Escolha a conta do caixa", StringComparison.Ordinal));

        if (blocking is not null)
        {
            throw new DomainException(blocking);
        }
    }

    private static void Apply(Charge charge, BoletoRegistration registration)
    {
        charge.ExternalSlipId = registration.ExternalId;
        charge.SlipState = registration.State;

        if (registration.Slip is { } slip)
        {
            charge.SlipBarcode = slip.Barcode;
            charge.BarcodeLine = slip.DigitableLine;
        }

        charge.SlipOurNumber = registration.OurNumber ?? charge.SlipOurNumber;
        charge.SlipPixPayload = registration.PixPayload ?? charge.SlipPixPayload;
    }

    // --- Credenciais ---

    private (IBoletoProvider Provider, BankCredentials Credentials) Connect(Condominium condominium)
    {
        BoletoSettings settings = condominium.Boleto;

        DomainException.ThrowIf(!settings.IsConfigured, "O banco do condomínio não está configurado.");
        DomainException.ThrowIf(
            !protector.IsAvailable,
            "O servidor está sem chave de criptografia (App:SecretsKey) para ler as credenciais do banco.");

        IBoletoProvider provider = providers.FirstOrDefault(p => p.Provider == settings.Provider)
            ?? throw new DomainException("Este banco ainda não tem integração no sistema.");

        BankCredentials credentials;

        try
        {
            credentials = new BankCredentials
            {
                CacheKey = condominium.Id.ToString("N"),
                Sandbox = settings.Sandbox,
                ClientId = settings.ClientId!,
                ClientSecret = protector.Unprotect(settings.ClientSecretCipher!, Purpose(condominium.Id, "client-secret")),
                Certificate = Convert.FromBase64String(
                    protector.Unprotect(settings.CertificateCipher!, Purpose(condominium.Id, "certificate"))),
                CertificatePassword = settings.CertificatePasswordCipher is { } password
                    ? protector.Unprotect(password, Purpose(condominium.Id, "certificate-password"))
                    : null,
                CheckingAccount = settings.CheckingAccount,
            };
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Chave do servidor trocada, ou registro mexido no banco de dados.
            throw new DomainException(
                "Não consegui ler as credenciais guardadas do banco. Se a chave de " +
                "criptografia do servidor mudou, cadastre o client secret e o certificado de novo.");
        }

        return (provider, credentials);
    }

    /// <summary>
    /// O condominio entra no rotulo da cifra: um segredo copiado de um
    /// condominio para outro direto no banco de dados nao decifra.
    /// </summary>
    private static string Purpose(Guid condominiumId, string field) => $"{condominiumId:N}:boleto.{field}";

    private ClientCertificate.Normalized? ReadCertificate(UpdateBoletoSettingsRequest request)
    {
        bool hasPem = !string.IsNullOrWhiteSpace(request.CertificatePem)
            || !string.IsNullOrWhiteSpace(request.PrivateKeyPem);

        if (hasPem)
        {
            return ClientCertificate.FromPem(
                request.CertificatePem ?? string.Empty,
                request.PrivateKeyPem ?? string.Empty,
                clock.Now);
        }

        if (!string.IsNullOrWhiteSpace(request.CertificatePkcs12Base64))
        {
            byte[] content;

            try
            {
                content = Convert.FromBase64String(request.CertificatePkcs12Base64);
            }
            catch (FormatException)
            {
                throw new DomainException("O arquivo do certificado chegou corrompido.");
            }

            return ClientCertificate.FromPkcs12(content, request.CertificatePassword, clock.Now);
        }

        return null;
    }

    private static string? NormalizeInstructions(string? instructions)
    {
        if (string.IsNullOrWhiteSpace(instructions))
        {
            return null;
        }

        // Duas linhas ficam reservadas para competencia e encargos.
        string[] lines = instructions
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(BoletoInstructions.MaxLines - 2)
            .Select(l => l.Length <= 78 ? l : l[..78])
            .ToArray();

        return lines.Length == 0 ? null : string.Join('\n', lines);
    }

    private BoletoSettingsDto ToDto(BoletoSettings settings)
    {
        var warnings = new List<string>();

        if (settings.CertificateExpiresAt is { } expires)
        {
            if (expires <= clock.Now)
            {
                warnings.Add($"O certificado venceu em {expires.ToString("dd/MM/yyyy", Brazil)}. Gere um novo no internet banking.");
            }
            else if (expires - clock.Now <= CertificateWarningWindow)
            {
                warnings.Add($"O certificado vence em {expires.ToString("dd/MM/yyyy", Brazil)}. Gere o novo antes disso.");
            }
        }

        if (settings.IsConfigured && settings.BankAccountId is null)
        {
            warnings.Add("Escolha a conta do caixa onde entram os boletos pagos.");
        }

        return new BoletoSettingsDto(
            settings.Provider,
            settings.Sandbox,
            settings.ClientId,
            !string.IsNullOrWhiteSpace(settings.ClientSecretCipher),
            !string.IsNullOrWhiteSpace(settings.CertificateCipher),
            settings.CertificateSubject,
            settings.CertificateExpiresAt,
            settings.CheckingAccount,
            settings.DaysOnAgenda,
            settings.Instructions,
            settings.BankAccountId,
            settings.VerifiedAt,
            settings.IsConfigured,
            protector.IsAvailable,
            warnings);
    }

    // --- Carga ---

    private async Task<Condominium> LoadCondominiumAsync(bool tracking, CancellationToken cancellationToken)
    {
        IQueryable<Condominium> query = tracking ? db.Condominiums : db.Condominiums.AsNoTracking();

        return await query.FirstOrDefaultAsync(cancellationToken)
            ?? throw new DomainException("Nenhum condomínio ativo no contexto da requisição.");
    }

    private async Task<Charge> LoadChargeAsync(Guid chargeId, CancellationToken cancellationToken) =>
        await db.Charges
            .Include(c => c.Unit).ThenInclude(u => u.Block)
            .Include(c => c.Payer)
            .FirstOrDefaultAsync(c => c.Id == chargeId, cancellationToken)
        ?? throw new KeyNotFoundException("Cobrança não encontrada.");

    private async Task<List<Charge>> LoadCycleChargesAsync(
        Guid cycleId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        bool exists = await db.BillingCycles.AnyAsync(c => c.Id == cycleId, cancellationToken);
        DomainException.ThrowIf(!exists, "Ciclo de cobrança não encontrado.");

        IQueryable<Charge> query = db.Charges
            .Include(c => c.Unit).ThenInclude(u => u.Block)
            .Include(c => c.Payer)
            .Where(c => c.BillingCycleId == cycleId);

        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        List<Charge> charges = await query.ToListAsync(cancellationToken);

        return [.. charges.OrderBy(UnitName, StringComparer.Create(Brazil, CompareOptions.IgnoreCase))];
    }

    private static string UnitName(Charge charge) => charge.Unit?.FullIdentifier ?? "Unidade";
}
