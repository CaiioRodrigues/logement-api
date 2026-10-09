# Colocar o Convivium no ar

Quatro containers numa maquina so: banco, API, portal e um proxy que resolve
o HTTPS sozinho. Um quinto faz backup do banco todo dia.

So o proxy fica exposto. Banco e API conversam por uma rede interna do
Docker, sem rota vinda de fora — o portal e um BFF, entao quem fala com a API
e sempre o servidor do Next, nunca o navegador.

## O que voce precisa antes

| | |
|---|---|
| **Servidor** | qualquer VPS com 2 GB de RAM e 20 GB de disco resolve um condominio |
| **Sistema** | Ubuntu 22.04 ou 24.04, com Docker e o plugin Compose |
| **Dominio** | apontando para o IP do servidor **antes** da primeira subida |
| **Portas** | 80 e 443 abertas |
| **SMTP** | conta em Resend, Brevo, Amazon SES ou o SMTP do seu provedor |

O dominio precisa estar apontado antes porque o Caddy pede o certificado
consultando o DNS. Se ainda nao propagou, ele tenta de novo sozinho, mas a
Let's Encrypt limita as tentativas por semana — melhor conferir com
`dig +short seudominio.com.br` antes de subir.

## Subindo

Os dois repositorios ficam lado a lado:

```bash
git clone https://github.com/CaiioRodrigues/logement-api.git
git clone https://github.com/CaiioRodrigues/logement-web.git
cd logement-api/deploy
```

Preencha a configuracao:

```bash
cp .env.example .env
nano .env
```

Os dois segredos que voce gera na hora:

```bash
openssl rand -base64 24   # POSTGRES_PASSWORD
openssl rand -base64 48   # JWT_SIGNING_KEY
```

Suba:

```bash
docker compose --env-file .env up -d --build
```

A primeira vez demora alguns minutos compilando as duas imagens. Depois:

```bash
docker compose ps          # os cinco servicos, e o estado de saude de cada um
docker compose logs -f api
```

## Primeiro acesso

A API cria a pessoa de `SUPER_ADMIN_EMAIL` na subida, sem senha, e imprime o
link de primeiro acesso no log:

```bash
docker compose logs api | grep -i convite
```

Abra o link, escolha a senha, e crie o condominio pela tela. O link vale 7
dias e so serve uma vez.

## O dia a dia

```bash
# Atualizar depois de um git pull nos dois repositorios
docker compose --env-file .env up -d --build

# Ver o que esta acontecendo
docker compose logs -f --tail 100

# Parar sem apagar nada
docker compose down

# Espaco em disco dos volumes
docker system df -v
```

## Backup

O container `backup` roda `pg_dump` uma vez por dia e guarda os ultimos
`DIAS_DE_BACKUP` dias no volume `convivium_backups`.

```bash
# O que existe hoje
docker compose exec backup ls -lh /backups

# Forcar um agora, antes de uma mudanca arriscada
docker compose exec backup pg_dump -Fc -f /backups/antes-da-mudanca.dump
```

**Isso cobre engano, nao desastre.** Um `DROP` errado, uma migracao ruim, um
dado apagado sem querer — para tudo isso a copia local resolve. Para o
servidor morrer, a copia precisa estar fora dele. Da sua maquina:

```bash
# Uma vez por semana, no cron da sua maquina
rsync -avz --delete \
  usuario@servidor:/var/lib/docker/volumes/convivium_backups/_data/ \
  ~/backups-condominio/
```

### Restaurar

```bash
# Copie o arquivo para o container e restaure por cima
docker compose exec -T postgres pg_restore \
  -U convivium -d convivium --clean --if-exists < backup.dump
```

Teste a restauracao **antes** de precisar dela. Backup que nunca foi
restaurado e so um arquivo grande.

## Quando alguma coisa nao sobe

**O certificado nao sai.** `docker compose logs caddy`. Quase sempre e DNS
que ainda nao propagou ou porta 443 fechada no firewall do provedor.

**A API reinicia sozinha.** `docker compose logs api`. A causa mais comum e
`JWT_SIGNING_KEY` curta demais — ela se recusa a subir com menos de 32
bytes, de proposito: uma API que aceita qualquer token e pior do que uma API
fora do ar.

**O portal diz que nao alcanca a API.** Confira `docker compose ps`: o `web`
so sobe depois de o `api` responder no `/health`, entao se ele subiu e a
chamada falha, o problema esta na rede interna, nao na ordem.

**E-mail nao chega.** `docker compose logs api | grep -i email`. A fila
guarda o erro do servidor SMTP e tenta de novo; a mensagem nao se perde
enquanto isso.

## O que ainda nao esta aqui

- **Monitoramento.** Ninguem e avisado se o site cair as 3 da manha. Um
  Uptime Kuma ou um healthcheck externo resolve, e fica para depois.
- **Varias instancias.** A API roda as migrations no boot e a fila de e-mail
  assume uma instancia so. Para um condominio, sobra.
- **Staging.** Um servidor so. Mudanca vai direto para producao, entao o
  teste continua sendo na sua maquina.
