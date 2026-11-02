# postech-identidade-service

[![Quality Gate](https://sonarcloud.io/api/project_badges/measure?project=postech4_postech-identidade-service&metric=alert_status)](https://sonarcloud.io/summary/new_code?id=postech4_postech-identidade-service) [![Cobertura](https://sonarcloud.io/api/project_badges/measure?project=postech4_postech-identidade-service&metric=coverage)](https://sonarcloud.io/summary/new_code?id=postech4_postech-identidade-service) [![Bugs](https://sonarcloud.io/api/project_badges/measure?project=postech4_postech-identidade-service&metric=bugs)](https://sonarcloud.io/summary/new_code?id=postech4_postech-identidade-service) [![Vulnerabilidades](https://sonarcloud.io/api/project_badges/measure?project=postech4_postech-identidade-service&metric=vulnerabilities)](https://sonarcloud.io/summary/new_code?id=postech4_postech-identidade-service) [![CI/CD](https://github.com/Gustavollier/postech-identidade-service/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/Gustavollier/postech-identidade-service/actions/workflows/ci-cd.yml)

Microsserviço de **Identidade** do Tech Challenge Fase 4 (FIAP 13SOAT).

Dono dos clientes, dos veículos, dos funcionários e do login da equipe.


> Visão de conjunto dos quatro serviços, com o diagrama da arquitetura e da saga: [`docs/arquitetura.md`](docs/arquitetura.md).

## O que este serviço faz

| Responsabilidade | Detalhe |
| --- | --- |
| Clientes | Cadastro por CPF **ou** CNPJ, busca pelo documento, listagem paginada |
| Veículos | Cadastro com placa antiga ou Mercosul, por cliente |
| Funcionários | Cadastro com cargo, valor da hora e credencial |
| Login da equipe | CPF e senha, com JWT no formato que o APIM já valida |

Banco: **Azure SQL** (`identidade-db`). Sem mensageria, e isso é de propósito
(veja abaixo).

## O lugar na arquitetura

A saga é orquestrada pelo OS Service ([ADR-0006](docs/ADR-0006-saga-orquestrada.md)),
e o Identidade **não participa dela**. Ele é consultado, não coordenado.

Quem abre uma ordem de serviço (o front da recepção) busca o cliente e o
veículo aqui e manda nome, e-mail e descrição do veículo no corpo do
`POST /ordens-servico`. O OS Service carrega esses dados no `OrdemAberta`, e
dali em diante nenhum serviço da saga pergunta nada ao Identidade.

Com isso, se o Identidade cair no meio de uma saga, a saga continua. O preço é
o dado do cliente duplicado nos eventos, e é barato. Por isso este serviço usa
só **REST síncrono**, o "quando necessário" do enunciado: não há evento de
cliente que algum serviço precise ouvir. A fila `identidade.eventos` está
declarada na topologia para o dia em que houver.

## O que mudou desde a Fase 3

O schema é o de Cliente, Veiculo, Funcionario e Seguranca do monólito, movido
para o banco do serviço dono dele. Veio com as restrições que o documento de
entrega da Fase 3 deixou como recomendação:

| Lacuna da Fase 3 | Agora |
| --- | --- |
| CPF de cliente sem `UNIQUE`: a Auth Function fazia `SELECT TOP 1 ... WHERE CPF = @Cpf` e, com CPF duplicado, autenticava um dos dois clientes, sem critério | Índices únicos filtrados em `CPF` e `CNPJ`, e no `CPF` do funcionário |
| Cliente podia existir sem documento nenhum | `CHECK` exigindo exatamente um: CPF ou CNPJ |
| Placa sem unicidade | Índice único na placa **entre veículos ativos**: carro vendido volta com outro dono |
| `TentativasFalhas` e `BloqueadoAte` existiam e nunca eram usados | Cinco erros seguidos bloqueiam o login por quinze minutos (`423`) |
| Paginação dividia por 10 fixo: 105 clientes davam 10 páginas | Arredonda para cima, com contagem e dados numa consulta só |
| Script de schema rodado à mão em cada ambiente | Embutido no assembly e aplicado na partida, de forma idempotente |

**Compatível com o que já existe.** A senha usa o mesmo BCrypt (`Enhanced`,
SHA-384) do monólito, então os hashes migrados continuam conferindo. O JWT tem
o mesmo formato (HS256, cargo como `role`, `FuncionarioId`) e as mesmas
chaves de configuração (`Jwt:SecretKey`, `Jwt:Issuer`, `Jwt:Audience`), que é
o que a política `validate-jwt` do APIM confere. Há testes para as duas
coisas.

## Segurança do login

- **CPF que não existe, funcionário sem senha e senha errada respondem
  igual.** Senão o login vira um jeito de descobrir quais CPFs são de
  funcionários.
- **Bloqueado não confere a senha.** Responder diferente para a senha certa
  durante o bloqueio deixaria quem ataca continuar testando.
- **A troca de senha passa pelo mesmo login**, e conta para o mesmo bloqueio.
  Conferir a senha atual por fora dele seria uma segunda porta sem limite de
  tentativas.
- **A resposta de funcionário não traz o CPF inteiro** (`***.***.247-**`).

## Rotas

| Método | Rota | O quê |
| --- | --- | --- |
| `POST` | `/api/v1/clientes` | Cadastra. `cpf` **ou** `cnpj` |
| `GET` | `/api/v1/clientes?pagina=&tamanho=` | Lista, paginado (tamanho até 100) |
| `GET` | `/api/v1/clientes/{id}` | Um cliente |
| `GET` | `/api/v1/clientes/busca?documento=` | Por CPF ou CNPJ, com ou sem pontuação |
| `PUT` | `/api/v1/clientes/{id}` | Nome, telefone e e-mail. O documento não muda |
| `DELETE` | `/api/v1/clientes/{id}` | Desativa (ordens em outros bancos apontam para o id) |
| `GET` | `/api/v1/clientes/{id}/veiculos` | Veículos do cliente |
| `POST` | `/api/v1/veiculos` | Cadastra. A placa é normalizada (`abc-1d23` → `ABC1D23`) |
| `GET` · `PUT` · `DELETE` | `/api/v1/veiculos/{id}` | Consulta, atualiza, desativa |
| `POST` | `/api/v1/funcionarios` | Cadastra com senha |
| `GET` | `/api/v1/funcionarios` | Equipe ativa |
| `GET` · `PUT` · `DELETE` | `/api/v1/funcionarios/{id}` | Consulta, atualiza cargo e valor, desativa |
| `POST` | `/api/v1/autenticacao/login` | CPF e senha → JWT. `401` ou `423` (bloqueado) |
| `POST` | `/api/v1/autenticacao/trocar-senha` | CPF, senha atual e nova |
| `GET` | `/health` · `/health/ready` | Liveness e readiness |

Swagger em `/swagger`. Exemplos em [`PosTech.Identidade.Api.http`](src/PosTech.Identidade.Api/PosTech.Identidade.Api.http).

Os erros de negócio são traduzidos num lugar só (`TradutorDeErros`): dado
inválido é `400`, inexistente é `404`, duplicado é `409`.

## Rodar local

```bash
docker compose up -d

export SqlConnectionString="Server=localhost;Database=master;User Id=sa;Password=...;TrustServerCertificate=true"
export Jwt__SecretKey="uma-chave-com-pelo-menos-32-bytes-aqui"
export Jwt__Issuer="PosTechChallenge"
export Jwt__Audience="PosTechChallenge-API"
dotnet run --project src/PosTech.Identidade.Api
```

O schema é aplicado na partida.

## Testes

```bash
dotnet test
```

**95 testes, 97% de cobertura de linha.** Os de integração sobem SQL Server
de verdade via Testcontainers, então é preciso ter Docker. As regras que mais
importam moram no banco (índices únicos, `CHECK`, chave estrangeira, a
transação que cria funcionário e credencial juntos), e dublê não prova
nenhuma delas.

Entre o que os testes garantem:

- dez cadastros **simultâneos** do mesmo CPF deixam um só, e os outros nove
  recebem `409`, e não `500`
- o JWT emitido valida com a mesma chave, emissor e audiência que o APIM usa
- um hash gerado como na Fase 3 continua conferindo
- o quinto erro bloqueia, e durante o bloqueio até a senha certa é recusada
- a troca de senha conta para o bloqueio
- desativar um veículo libera a placa para outro dono
- aplicar o schema duas vezes não quebra nada

A pipeline **reprova abaixo de 80% de cobertura**.

## Configuração

| Variável | O quê |
| --- | --- |
| `SqlConnectionString` | Conexão com o `identidade-db`. Obrigatória |
| `Jwt__SecretKey` | Segredo HMAC, com pelo menos 32 bytes. O mesmo do Key Vault da Fase 3. Obrigatória |
| `Jwt__Issuer` | `PosTechChallenge`. Obrigatória |
| `Jwt__Audience` | `PosTechChallenge-API`. Obrigatória |
| `Jwt__AccessTokenExpirationMinutes` | Padrão 15 |

Sem qualquer uma das obrigatórias, ou com chave JWT curta, **o serviço não
sobe**.

## Documentação

- [ADR-0006: Saga orquestrada](docs/ADR-0006-saga-orquestrada.md)
- [ADR-0007: Um banco por serviço](docs/ADR-0007-banco-por-servico.md)
- [ADR-0008: CQRS só como separação de código](docs/ADR-0008-cqrs-sem-infraestrutura-nova.md)
