# Logement · API

Sistema da **Logement Administradora**: caixa, rateio mensal, cobranças com
PIX, leitura automática de contas de concessionária e prestação de contas.

API em **ASP.NET Core 10** sobre **PostgreSQL**. O portal (`convivium-web`,
Next.js) consome esta API.

> Os repositórios e os namespaces continuam com o nome antigo, `convivium`.
> É só identificador interno — renomear pastas e `namespace` em 167 arquivos
> não muda nada para quem usa, e quebraria todo histórico de `git blame`.

---

## Como rodar

**Pré-requisitos:** [.NET SDK 10](https://dotnet.microsoft.com/download) e Docker.

```bash
# 1. Sobe Postgres e Mailpit (servidor SMTP de desenvolvimento)
docker compose up -d

# 2. Sobe a API — aplica as migrations e popula o condomínio de demonstração
dotnet run --project src/Convivium.Api
```

No Windows, o Docker Desktop precisa estar aberto antes do primeiro comando —
não basta estar instalado.

### Sem Docker

Docker é conveniência, não exigência. Com um PostgreSQL instalado na máquina
(ou já rodando de outro projeto), crie o usuário e o banco que a connection
string espera:

```sql
CREATE ROLE convivium WITH LOGIN PASSWORD 'convivium';
CREATE DATABASE convivium OWNER convivium;
```

E rode a API normalmente. Se o seu Postgres usa outra porta, outro usuário ou
outra senha, aponte para ele sem editar `appsettings.json`:

```bash
# PowerShell
$env:ConnectionStrings__Default = "Host=localhost;Port=5433;Database=convivium;Username=postgres;Password=suasenha"

# bash
export ConnectionStrings__Default="Host=localhost;Port=5433;Database=convivium;Username=postgres;Password=suasenha"
```

O que se perde sem Docker é só o Mailpit, onde dá para ler os e-mails que a
API envia. A aplicação não quebra por isso: o envio passa por uma fila com
repetição e backoff, então as mensagens ficam pendentes em vez de derrubar
alguma coisa.

Rodar o Postgres do Convivium ao lado de outro banco na mesma máquina é
tranquilo — cada motor escuta na sua porta e um não enxerga o outro.

### Postgres hospedado (Supabase e afins)

Supabase é PostgreSQL, então serve sem nenhuma mudança no código — é só
apontar a connection string para lá. Duas armadilhas:

**SSL é obrigatório.** Sem isso a conexão é recusada:

```
Host=db.SEU-PROJETO.supabase.co;Port=5432;Database=postgres;Username=postgres;Password=SUA-SENHA;SSL Mode=Require
```

**As migrations precisam da conexão direta, porta 5432.** O *transaction
pooler* (porta 6543) não mantém estado entre comandos, e o Npgsql usa
prepared statements — migration por ali falha de um jeito difícil de ler.
Use a direta para subir a API e deixe o pooler para quando houver muita
conexão simultânea.

Vale lembrar que aqui cada consulta vai e volta pela internet: o primeiro
`dotnet run`, que aplica as migrations e popula o seed, demora bem mais do
que com um banco local.

No plano gratuito do Supabase, projeto parado por cerca de uma semana é
suspenso e precisa ser reativado no painel. Para um condomínio que movimenta
pouco fora da época do boleto, isso acontece.

### E-mail chegando de verdade

O Mailpit do compose captura e-mail para você ver na tela, mas não entrega
nada a ninguém. Para o convite chegar na caixa da pessoa, aponte para um
serviço de envio. Qualquer um que fale SMTP serve; o Resend é o de configuração
mais curta:

1. Crie a chave em [resend.com/api-keys](https://resend.com/api-keys).
2. Guarde a chave fora do repositório, com o gerenciador de segredos do .NET:

```bash
cd src/Convivium.Api
dotnet user-secrets set "Email:Host" "smtp.resend.com"
dotnet user-secrets set "Email:Port" "465"
dotnet user-secrets set "Email:UseSsl" "true"
dotnet user-secrets set "Email:Username" "resend"
dotnet user-secrets set "Email:Password" "re_SUA_CHAVE"
dotnet user-secrets set "Email:FromAddress" "convivium@seu-dominio.com.br"
```

No Visual Studio é o mesmo: botão direito no projeto → *Gerenciar Segredos do
Usuário*. O arquivo fica na pasta do seu usuário, nunca no git — por isso a
chave não entra aqui nem por descuido.

**Duas coisas que fazem o e-mail sumir sem erro claro:**

- **O remetente precisa ser de um domínio verificado no Resend.** O
  `nao-responda@convivium.local` padrão é recusado. Antes de verificar um
  domínio, dá para usar `onboarding@resend.dev` — mas ele só entrega para o
  e-mail da sua própria conta Resend, e nada mais.
- **Os endereços do condomínio de demonstração são fictícios** (`@exemplo.local`).
  Mandar para eles falha. Use o seu e-mail de verdade ao testar.

Para conferir o que aconteceu com cada mensagem:

```sql
select kind, to_address, status, attempts, last_error from email_messages
order by created_at desc limit 10;
```

`Pending` é fila, `Sent` saiu, `Failed` desistiu depois de cinco tentativas —
e `last_error` diz por quê.

### Problemas comuns

| O que aparece | O que é |
|---|---|
| `error during connect: ... docker_engine: The system cannot find the file specified` | O Docker Desktop não está rodando. Abra e espere aparecer "Engine running". |
| `Nada esta escutando nessa porta — o banco nao chegou a subir` | O container do Postgres não subiu. `docker compose up -d postgres` e depois `docker compose ps postgres`, que precisa mostrar `healthy`. |
| `port is already allocated` ao subir o compose | Outro Postgres já está na 5432. Pare o serviço, ou troque a porta no `docker-compose.yml` e na connection string. |
| `respondeu, mas recusou a conexao` com `28P01` | Usuário ou senha não conferem. Se o banco já existia com outra senha: `docker compose down -v && docker compose up -d postgres`. |

| O quê | Onde |
|---|---|
| API | http://localhost:5080 |
| Swagger | http://localhost:5080/swagger |
| Mailpit (e-mails enviados) | http://localhost:8025 |
| Postgres | `localhost:5432` — usuário/senha/banco: `convivium` |

### Acessos de demonstração

Senha para todos: `Convivium@123`

| E-mail | Papel | O que enxerga |
|---|---|---|
| `sindico@convivium.local` | Síndico | Tudo |
| `conselho@convivium.local` | Conselho fiscal | Leitura completa das contas |
| `zelador@convivium.local` | Zelador | Lança despesas, sem acesso ao caixa |
| `morador@convivium.local` | Morador | Só as próprias cobranças |

O seed cria o *Residencial Convivium*: 2 blocos, 24 unidades com fração
ideal derivada da área privativa, 27 pessoas e seis meses de histórico
financeiro — o suficiente para os gráficos terem conteúdo desde o primeiro
`dotnet run`.

### Testes

```bash
dotnet test
```

---

## Colocar no ar

A pilha de produção está em [`deploy/`](deploy/README.md): banco, API, portal
e um proxy que resolve o HTTPS sozinho, mais um container que faz backup do
banco todo dia.

```bash
cd deploy
cp .env.example .env   # preencha domínio, senha do banco e chave do JWT
docker compose --env-file .env up -d --build
```

Só o proxy fica exposto. Banco e API conversam por uma rede interna, sem rota
vinda de fora — o portal é um BFF, então quem fala com a API é sempre o
servidor do Next, nunca o navegador.

O [README do deploy](deploy/README.md) traz os pré-requisitos, o primeiro
acesso, como restaurar um backup e o que olhar quando algo não sobe.

## Arquitetura

```
src/
  Convivium.Domain/          Entidades e regras puras. Não conhece banco nem HTTP.
  Convivium.Application/     Casos de uso, DTOs e as portas (interfaces).
  Convivium.Infrastructure/  EF Core, Postgres, PDF, e-mail, JWT.
  Convivium.Api/             Controllers, autenticação, DI, Swagger.
tests/
  Convivium.Tests/           Testes da lógica financeira e dos leitores de PDF.
```

A dependência aponta sempre para dentro: `Api → Infrastructure → Application
→ Domain`. Trocar Postgres por outro banco, ou SMTP por SendGrid, mexe só na
camada de fora.

### Isolamento entre condomínios

Toda entidade que pertence a um condomínio implementa `ITenantScoped`, e o
`DbContext` aplica um filtro global por `CondominiumId` montado por árvore de
expressão. Nenhuma consulta precisa lembrar de filtrar.

O condomínio ativo viaja **dentro do token JWT**, não em header nem query
string — o cliente não consegue escolher o que enxerga. Trocar de condomínio
exige emitir outro token, e ele só sai se o vínculo existir.

O filtro falha fechado: sem condomínio no token, casa com `Guid.Empty` e não
devolve nada.

### Papéis

Os valores de `MembershipRole` são crescentes em poder, então a hierarquia é
uma comparação simples:

`Morador(1) < Zelador(2) < Conselho(3) < Subsíndico(4) < Síndico(5) < Administradora(6)`

As políticas `Member`, `Council`, `Finance` e `Manager` exigem um papel mínimo.

---

## Módulos

### Caixa (`/api/caixa`)

Contas bancárias, plano de contas e lançamentos.

**Saldo nunca é coluna.** É sempre o saldo de abertura mais a soma dos
lançamentos, calculada no banco. Saldo materializado é saldo que um dia
deixa de bater com o extrato.

O extrato por conta e período devolve o saldo anterior ao primeiro dia, que
é o que permite conferir linha a linha contra o extrato do banco.

### Despesas (`/api/despesas`)

Contas a pagar com **competência separada da data de pagamento**: a conta de
luz que chega em outubro pode ser da competência de setembro, e é a
competência que vale na prestação de contas.

Dar baixa cria a saída no caixa na mesma transação. Estorno remove o
lançamento e devolve a despesa para pendente.

### Rateio e cobranças (`/api/cobrancas`)

Ciclo de vida: **Rascunho → Fechado → Publicado**.

- **Prévia** simula a competência sem gravar nada, para o síndico conferir o
  valor da cota e o conselho aprovar.
- **Fechar** congela os valores e gera uma cobrança por unidade. Depois
  disso, mexer nas despesas do mês não muda mais o que foi cobrado — o
  morador precisa poder confiar no boleto que recebeu.
- **Publicar** gera o PIX e libera o acesso do morador.

O rateio divide pela **fração ideal** (regra supletiva do Código Civil, art.
1.336, I), por área privativa ou em partes iguais. O cálculo é feito em
centavos inteiros e a sobra é distribuída pelo método das maiores sobras,
então a soma das cotas bate **exatamente** com o total — sem o clássico
"sobrou R$ 0,03 no rateio".

Multa e juros são calculados na leitura, nunca gravados: se fossem coluna,
dependeriam de uma rotina noturna rodar para ficarem corretos.

### Boleto com PIX

O boleto sai em PDF com QR Code, código copia-e-cola e o detalhamento dos
itens. O BR Code segue o padrão EMV do Banco Central, com CRC-16/CCITT.

Uma cobrança vencida **regenera o PIX com multa e juros embutidos**, para o
morador pagar o valor certo do dia em vez do valor de face.

Dois caminhos de acesso:

- `GET /api/cobrancas/{id}/pdf` — síndico e conselho, autenticado.
- `GET /api/boleto/{token}` e `/pdf` — link público com token de 256 bits,
  um por cobrança. É o que permite mandar o boleto para um proprietário que
  nunca criou conta no sistema.

### Leitura de faturas em PDF (`/api/faturas`)

Importa a conta da CEMIG e extrai valor, vencimento, competência,
instalação, consumo e linha digitável.

O fluxo é **deliberadamente em dois passos**: importar apenas lê e guarda;
gerar a despesa é uma ação separada, depois de a leitura ser conferida.
Lançar no caixa automaticamente o que uma expressão regular achou seria
confiar demais — o valor errado entraria no rateio de todo mundo.

Detalhes que importam:

- O consumo é lido **por posição de coluna** na linha do medidor, não pelo
  rótulo. Buscar por "CONSUMO kWh" devolve a leitura anterior do medidor,
  um acumulado dezenas de vezes maior que o consumo do mês.
- A diferença entre as leituras serve de segunda fonte; quando as duas
  discordam, a fatura vai para revisão com o aviso.
- O que não for encontrado vira aviso, nunca chute: uma despesa com
  vencimento errado gera juros de verdade.
- SHA-256 do arquivo impede importar a mesma fatura duas vezes.
- O texto bruto extraído fica guardado e exposto em
  `GET /api/faturas/{id}/texto`, para entender por que um campo não foi
  encontrado sem precisar do arquivo original.

**Para adicionar outra concessionária** (COPASA, GASMIG, sua distribuidora):
implemente `IUtilityBillParser` em `src/Convivium.Infrastructure/Utilities/`
e registre antes do `GenericBillParser` no `DependencyInjection`.

### E-mail (`/api/notificacoes`)

Padrão **outbox**: a mensagem é gravada na mesma transação da operação que a
originou, e um serviço em segundo plano faz o envio. O SMTP fora do ar não
derruba a requisição.

O corpo é montado **no envio, não no enfileiramento** — multa e juros mudam
todo dia, e um lembrete agendado para daqui a três dias sairia com o valor
de hoje. O PDF anexo é gerado na mesma hora, pelo mesmo motivo.

Retentativa com recuo exponencial de 1 a 16 minutos. Enfileirar duas vezes a
mesma notificação não duplica o envio.

O retorno lista as unidades **sem e-mail cadastrado**, para o síndico saber
quem precisa ser avisado de outra forma.

### Painel (`/api/painel`)

Agregações prontas para os gráficos, no formato que o front consome direto:

| Endpoint | Para quê |
|---|---|
| `/api/painel` | Caixa, resultado do mês, a pagar, a receber, inadimplência |
| `/gastos-por-categoria` | Gráfico de pizza, agrupado no plano de contas |
| `/evolucao-mensal` | Receita × despesa × saldo, mês a mês |
| `/fornecedores` | Ranking de gasto por fornecedor |
| `/consumo/{provider}` | Consumo mês a mês, das faturas lidas em PDF |

A série de consumo traz o preço por unidade derivado: um salto no consumo de
água costuma ser vazamento, e uma alta no R$/kWh denuncia reajuste de tarifa.

---

## Configuração

Tudo em `appsettings.json`, sobrescrevível por variável de ambiente com `__`
como separador (`Jwt__SigningKey`, `ConnectionStrings__Default`).

| Seção | Para quê |
|---|---|
| `ConnectionStrings:Default` | Postgres |
| `Jwt:SigningKey` | **Obrigatória**, mínimo 32 bytes. A API não sobe sem ela. |
| `Jwt:AccessTokenMinutes` | Vida do token de acesso (padrão 30) |
| `App:PublicBaseUrl` | Base do link do boleto que vai no e-mail |
| `Cors:AllowedOrigins` | Origens do `convivium-web` |
| `Email:*` | SMTP, tentativas e intervalo da fila |
| `Seed:Enabled` | Popula o demo (só em Development) |

**A chave JWT nunca deve ir para o `appsettings.json` versionado.** Em
produção, use variável de ambiente ou gerenciador de segredos. A API valida
no boot e recusa subir com chave ausente ou curta — subir uma API que aceita
qualquer token é pior do que não subir.

### Migrations

Em desenvolvimento, as migrations são aplicadas no boot. Em produção,
prefira rodar no deploy: duas instâncias subindo ao mesmo tempo disputariam
o lock.

```bash
dotnet ef migrations add NomeDaMigration \
  --project src/Convivium.Infrastructure \
  --startup-project src/Convivium.Infrastructure \
  --output-dir Persistence/Migrations

dotnet ef database update \
  --project src/Convivium.Infrastructure \
  --startup-project src/Convivium.Infrastructure
```

### Convenções de banco

- **snake_case** nas tabelas e colunas, para o contador conseguir consultar
  o caixa direto no banco quando precisar.
- **Enums como texto**: `"Paid"` diz mais que `2` num extrato.
- **Competência como inteiro AAAAMM**, comparável por índice comum.
- Dinheiro em `numeric(18,2)`, taxas em `numeric(9,6)`, fração ideal em
  `numeric(12,8)` — comporta prédios de centenas de unidades sem perder
  exatidão no rateio.
- `DeleteBehavior.Restrict` no financeiro: apagar uma conta bancária ou um
  fornecedor nunca leva o histórico de lançamentos junto.

---

## O que ainda não existe

Coisas conscientemente fora deste primeiro corte:

- **Boleto bancário registrado.** Hoje a cobrança é PIX + PDF próprio. A
  emissão está atrás de campos já previstos em `Charge` (`ExternalSlipId`,
  `BarcodeLine`) para plugar um banco ou gateway sem reescrever o módulo.
- **Conciliação automática de PIX recebido.** O `txid` já deriva do id da
  cobrança, então o PIX do extrato é rastreável até quem pagou; falta ler o
  extrato.
- **Assembleias, atas e votação.**
- **Reserva de áreas comuns.** Já dá para cobrar (cobrança avulsa), falta a
  agenda.
- **Anexos de nota fiscal** nas despesas.
- **Balancete em PDF** para a prestação de contas anual.

---

## Licença de terceiros

**QuestPDF** usa a licença Community: gratuita para pessoas físicas e
empresas com receita anual abaixo de US$ 1 milhão. Acima disso, exige
licença comercial. A declaração está em
`src/Convivium.Infrastructure/DependencyInjection.cs`.
