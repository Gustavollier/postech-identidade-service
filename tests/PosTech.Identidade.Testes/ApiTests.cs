using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.IdentityModel.Tokens;
using PosTech.Identidade;

namespace PosTech.Identidade.Testes;

[Collection("identidade")]
public class ClientesApiTests(AmbienteDaIdentidade ambiente)
{
    private readonly HttpClient _http = ambiente.Cliente();

    private static async Task<JsonElement> Json(HttpResponseMessage resposta) =>
        await resposta.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Cadastra_e_encontra_pelo_documento_com_ou_sem_pontuacao()
    {
        var cpf = Dados.Cpf();
        var formatado = $"{cpf[..3]}.{cpf[3..6]}.{cpf[6..9]}-{cpf[9..]}";

        var criado = await _http.PostAsJsonAsync("/api/v1/clientes", new
        {
            cpf = formatado, nomeCompleto = "  Ana Prado  ", telefone = "11999990000", email = "ana@oficina.test",
        });

        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);

        var corpo = await Json(criado);
        Assert.Equal(cpf, corpo.GetProperty("cpf").GetString());
        Assert.Equal("Ana Prado", corpo.GetProperty("nomeCompleto").GetString());

        var porDocumento = await Json(await _http.GetAsync($"/api/v1/clientes/busca?documento={formatado}"));
        Assert.Equal(corpo.GetProperty("id").GetInt32(), porDocumento.GetProperty("id").GetInt32());

        var porId = await _http.GetAsync($"/api/v1/clientes/{corpo.GetProperty("id").GetInt32()}");
        Assert.Equal(HttpStatusCode.OK, porId.StatusCode);
    }

    [Fact]
    public async Task Cliente_pessoa_juridica_usa_CNPJ()
    {
        var cnpj = Dados.Cnpj();

        var criado = await _http.PostAsJsonAsync("/api/v1/clientes", new { cnpj, nomeCompleto = "Transportes LTDA" });

        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        Assert.Equal(cnpj, (await Json(criado)).GetProperty("cnpj").GetString());

        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync($"/api/v1/clientes/busca?documento={cnpj}")).StatusCode);
    }

    [Fact]
    public async Task O_mesmo_CPF_nao_entra_duas_vezes()
    {
        // A lacuna que a Fase 3 deixou aberta: sem UNIQUE, a Auth Function
        // autenticava um dos clientes duplicados, sem critério.
        var cpf = Dados.Cpf();

        await ambiente.NovoClienteAsync(cpf);

        var repetido = await _http.PostAsJsonAsync("/api/v1/clientes", new { cpf, nomeCompleto = "Outra Pessoa" });

        Assert.Equal(HttpStatusCode.Conflict, repetido.StatusCode);
    }

    [Fact]
    public async Task Cadastros_simultaneos_do_mesmo_CPF_deixam_um_so()
    {
        // As dez requisições passam juntas pela checagem prévia; quem segura é
        // o índice único, e quem perde recebe 409 e não 500.
        var cpf = Dados.Cpf();

        var respostas = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            ambiente.Cliente().PostAsJsonAsync("/api/v1/clientes", new { cpf, nomeCompleto = "Ana Prado" })));

        Assert.Equal(1, respostas.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(respostas.Where(r => r.StatusCode != HttpStatusCode.Created),
            r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("52998224725", "11222333000181")]
    public async Task Exatamente_um_documento(string? cpf, string? cnpj)
    {
        var resposta = await _http.PostAsJsonAsync("/api/v1/clientes", new { cpf, cnpj, nomeCompleto = "Ana" });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Theory]
    [InlineData("12345678900", "Ana", null)]
    [InlineData(null, "", null)]
    [InlineData(null, "Ana", "nao-e-email")]
    public async Task Dados_invalidos_voltam_400_com_o_motivo(string? cpf, string nome, string? email)
    {
        var resposta = await _http.PostAsJsonAsync("/api/v1/clientes", new
        {
            cpf = cpf ?? Dados.Cpf(), nomeCompleto = nome, email,
        });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await Json(resposta)).GetProperty("title").GetString()));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("11111111111")]
    public async Task Busca_por_documento_invalido_e_400_e_nao_vai_ao_banco(string documento)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync($"/api/v1/clientes/busca?documento={documento}")).StatusCode);
    }

    [Fact]
    public async Task Documento_valido_sem_cadastro_e_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"/api/v1/clientes/busca?documento={Dados.Cpf()}")).StatusCode);
    }

    [Fact]
    public async Task Atualiza_os_dados_mas_nao_o_documento()
    {
        var id = await ambiente.NovoClienteAsync();

        var resposta = await _http.PutAsJsonAsync($"/api/v1/clientes/{id}", new
        {
            nomeCompleto = "Ana Prado Souza", telefone = "11988887777", email = "ana.souza@oficina.test",
        });

        var corpo = await Json(resposta);
        Assert.Equal("Ana Prado Souza", corpo.GetProperty("nomeCompleto").GetString());
        Assert.Equal("ana.souza@oficina.test", corpo.GetProperty("email").GetString());

        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.PutAsJsonAsync("/api/v1/clientes/999999", new { nomeCompleto = "X" })).StatusCode);
    }

    [Fact]
    public async Task Desativar_tira_de_circulacao_sem_apagar()
    {
        var id = await ambiente.NovoClienteAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync($"/api/v1/clientes/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.DeleteAsync($"/api/v1/clientes/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"/api/v1/clientes/{id}")).StatusCode);

        // A linha continua lá: ordens em outro banco apontam para este id.
        await using var conexao = new SqlConnection(ambiente.ConnectionString);
        Assert.Equal(1, await conexao.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Cliente WHERE Id = @id", new { id }));
    }

    [Fact]
    public async Task A_paginacao_conta_certo_e_nao_some_com_o_total_alem_do_fim()
    {
        for (var i = 0; i < 3; i++)
            await ambiente.NovoClienteAsync();

        var primeira = await Json(await _http.GetAsync("/api/v1/clientes?pagina=1&tamanho=2"));
        var total = primeira.GetProperty("total").GetInt32();

        Assert.True(total >= 3);
        Assert.Equal(2, primeira.GetProperty("itens").GetArrayLength());
        Assert.Equal((total + 1) / 2, primeira.GetProperty("totalDePaginas").GetInt32());

        var alemDoFim = await Json(await _http.GetAsync("/api/v1/clientes?pagina=9999&tamanho=2"));
        Assert.Equal(0, alemDoFim.GetProperty("itens").GetArrayLength());
        Assert.Equal(total, alemDoFim.GetProperty("total").GetInt32());
    }
}

[Collection("identidade")]
public class VeiculosApiTests(AmbienteDaIdentidade ambiente)
{
    private readonly HttpClient _http = ambiente.Cliente();

    private async Task<HttpResponseMessage> CadastrarAsync(int clienteId, string placa, int? anoModelo = 2022, int? anoFabricacao = 2021) =>
        await _http.PostAsJsonAsync("/api/v1/veiculos", new
        {
            clienteId, marca = "VW", modelo = "Gol", placa, cor = "Prata", anoModelo, anoFabricacao, kmEntrada = 42000,
        });

    [Fact]
    public async Task Cadastra_com_a_placa_normalizada_e_lista_pelo_cliente()
    {
        var clienteId = await ambiente.NovoClienteAsync();
        var placa = Dados.Placa();
        var digitada = $"{placa[..3].ToLowerInvariant()}-{placa[3..]}";

        var criado = await CadastrarAsync(clienteId, digitada);

        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);

        var corpo = await criado.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(placa, corpo.GetProperty("placa").GetString());
        Assert.Equal($"VW Gol {placa}", corpo.GetProperty("descricao").GetString());

        var doCliente = await _http.GetFromJsonAsync<JsonElement>($"/api/v1/clientes/{clienteId}/veiculos");
        Assert.Equal(placa, Assert.Single(doCliente.EnumerateArray()).GetProperty("placa").GetString());
    }

    [Fact]
    public async Task A_mesma_placa_ativa_nao_entra_duas_vezes_mas_volta_depois_de_desativada()
    {
        // Carro vendido aparece de novo com outro dono: o índice único só
        // olha veículos ativos.
        var placa = Dados.Placa();
        var primeiroDono = await ambiente.NovoClienteAsync();
        var segundoDono = await ambiente.NovoClienteAsync();

        var criado = await (await CadastrarAsync(primeiroDono, placa)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, (await CadastrarAsync(segundoDono, placa)).StatusCode);

        await _http.DeleteAsync($"/api/v1/veiculos/{criado.GetProperty("id").GetInt32()}");

        Assert.Equal(HttpStatusCode.Created, (await CadastrarAsync(segundoDono, placa)).StatusCode);
    }

    [Fact]
    public async Task Veiculo_de_cliente_que_nao_existe_ou_foi_desativado_e_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await CadastrarAsync(999999, Dados.Placa())).StatusCode);

        var desativado = await ambiente.NovoClienteAsync();
        await _http.DeleteAsync($"/api/v1/clientes/{desativado}");

        Assert.Equal(HttpStatusCode.NotFound, (await CadastrarAsync(desativado, Dados.Placa())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"/api/v1/clientes/{desativado}/veiculos")).StatusCode);
    }

    [Theory]
    [InlineData("AB12", 2022, 2021)]
    [InlineData(null, 2019, 2021)]
    [InlineData(null, 1800, null)]
    public async Task Placa_ou_ano_invalidos_voltam_400(string? placa, int? anoModelo, int? anoFabricacao)
    {
        var clienteId = await ambiente.NovoClienteAsync();

        var resposta = await CadastrarAsync(clienteId, placa ?? Dados.Placa(), anoModelo, anoFabricacao);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Atualiza_sem_mudar_a_placa_e_desativa()
    {
        var clienteId = await ambiente.NovoClienteAsync();
        var placa = Dados.Placa();
        var id = (await (await CadastrarAsync(clienteId, placa)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetInt32();

        var atualizado = await _http.PutAsJsonAsync($"/api/v1/veiculos/{id}", new
        {
            marca = "VW", modelo = "Gol G5", cor = "Preto", anoModelo = 2022, anoFabricacao = 2022, kmEntrada = 50000,
        });

        var corpo = await atualizado.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Gol G5", corpo.GetProperty("modelo").GetString());
        Assert.Equal(placa, corpo.GetProperty("placa").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync($"/api/v1/veiculos/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"/api/v1/veiculos/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.PutAsJsonAsync($"/api/v1/veiculos/{id}", new { marca = "VW", modelo = "Gol" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.DeleteAsync($"/api/v1/veiculos/{id}")).StatusCode);
    }
}

[Collection("identidade")]
public class FuncionariosEAutenticacaoTests(AmbienteDaIdentidade ambiente)
{
    private readonly HttpClient _http = ambiente.Cliente();

    private async Task<(int Id, string Cpf)> NovoFuncionarioAsync(string cargo = "Mecanico")
    {
        var cpf = Dados.Cpf();

        var resposta = await _http.PostAsJsonAsync("/api/v1/funcionarios", new
        {
            nome = "Bruno Lima", contato = "11977776666", cpf, cargo, valorHora = 85.5m, senha = Dados.SenhaForte,
        });

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return ((await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32(), cpf);
    }

    private Task<HttpResponseMessage> Entrar(string cpf, string senha) =>
        _http.PostAsJsonAsync("/api/v1/autenticacao/login", new { cpf, senha });

    [Fact]
    public async Task O_login_emite_o_mesmo_JWT_da_Fase_3()
    {
        var (id, cpf) = await NovoFuncionarioAsync("Gerente");

        var resposta = await Entrar(cpf, Dados.SenhaForte);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var token = (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();

        // Valida como o APIM valida: mesma chave, emissor e audiência.
        var principal = new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
        {
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AmbienteDaIdentidade.ChaveJwt)),
            ValidIssuer = AmbienteDaIdentidade.Emissor,
            ValidAudience = AmbienteDaIdentidade.Audiencia,
        }, out _);

        Assert.True(principal.IsInRole("Gerente"));
        Assert.Equal(id.ToString(), principal.FindFirstValue("FuncionarioId"));
    }

    [Fact]
    public async Task CPF_inexistente_e_senha_errada_respondem_igual()
    {
        // Senão o login vira um jeito de descobrir quais CPFs são de
        // funcionários.
        var (_, cpf) = await NovoFuncionarioAsync();

        var senhaErrada = await Entrar(cpf, "Errada@2026");
        var cpfQueNaoExiste = await Entrar(Dados.Cpf(), "Errada@2026");
        var cpfInvalido = await Entrar("123", "Errada@2026");

        Assert.Equal(HttpStatusCode.Unauthorized, senhaErrada.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, cpfQueNaoExiste.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, cpfInvalido.StatusCode);
        Assert.Equal(await senhaErrada.Content.ReadAsStringAsync(), await cpfQueNaoExiste.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cinco_erros_bloqueiam_e_ate_a_senha_certa_e_recusada()
    {
        // A Fase 3 tinha as colunas de bloqueio e nunca as usou.
        var (_, cpf) = await NovoFuncionarioAsync();

        for (var i = 0; i < Acesso.TentativasAntesDoBloqueio - 1; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Entrar(cpf, "Errada@2026")).StatusCode);

        var quinta = await Entrar(cpf, "Errada@2026");
        Assert.Equal(HttpStatusCode.Locked, quinta.StatusCode);

        var certa = await Entrar(cpf, Dados.SenhaForte);
        Assert.Equal(HttpStatusCode.Locked, certa.StatusCode);
        Assert.True((await certa.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("bloqueadoAte", out _));
    }

    [Fact]
    public async Task Acertar_antes_do_quinto_erro_zera_a_contagem()
    {
        var (id, cpf) = await NovoFuncionarioAsync();

        for (var i = 0; i < Acesso.TentativasAntesDoBloqueio - 1; i++)
            await Entrar(cpf, "Errada@2026");

        Assert.Equal(HttpStatusCode.OK, (await Entrar(cpf, Dados.SenhaForte)).StatusCode);

        await using var conexao = new SqlConnection(ambiente.ConnectionString);
        Assert.Equal(0, await conexao.ExecuteScalarAsync<int>(
            "SELECT TentativasFalhas FROM Seguranca WHERE FuncionarioId = @id", new { id }));
    }

    [Fact]
    public async Task Trocar_a_senha_exige_a_atual_e_a_nova_passa_a_valer()
    {
        var (_, cpf) = await NovoFuncionarioAsync();

        var semAtual = await _http.PostAsJsonAsync("/api/v1/autenticacao/trocar-senha",
            new { cpf, senhaAtual = "Errada@2026", novaSenha = "Nova@Senha2026" });
        Assert.Equal(HttpStatusCode.Unauthorized, semAtual.StatusCode);

        var fraca = await _http.PostAsJsonAsync("/api/v1/autenticacao/trocar-senha",
            new { cpf, senhaAtual = Dados.SenhaForte, novaSenha = "fraca" });
        Assert.Equal(HttpStatusCode.BadRequest, fraca.StatusCode);

        var trocou = await _http.PostAsJsonAsync("/api/v1/autenticacao/trocar-senha",
            new { cpf, senhaAtual = Dados.SenhaForte, novaSenha = "Nova@Senha2026" });
        Assert.Equal(HttpStatusCode.NoContent, trocou.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Entrar(cpf, Dados.SenhaForte)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Entrar(cpf, "Nova@Senha2026")).StatusCode);
    }

    [Fact]
    public async Task A_troca_de_senha_tambem_conta_para_o_bloqueio()
    {
        // Sem isto, a troca de senha seria uma segunda porta para testar
        // senhas à vontade, contornando o bloqueio do login.
        var (_, cpf) = await NovoFuncionarioAsync();

        for (var i = 0; i < Acesso.TentativasAntesDoBloqueio; i++)
        {
            await _http.PostAsJsonAsync("/api/v1/autenticacao/trocar-senha",
                new { cpf, senhaAtual = "Errada@2026", novaSenha = "Nova@Senha2026" });
        }

        Assert.Equal(HttpStatusCode.Locked, (await Entrar(cpf, Dados.SenhaForte)).StatusCode);
    }

    [Fact]
    public async Task Funcionario_desativado_nao_entra()
    {
        var (id, cpf) = await NovoFuncionarioAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync($"/api/v1/funcionarios/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Entrar(cpf, Dados.SenhaForte)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.DeleteAsync($"/api/v1/funcionarios/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_resposta_nao_expoe_o_CPF_nem_a_senha()
    {
        var (id, cpf) = await NovoFuncionarioAsync();

        var corpo = await _http.GetStringAsync($"/api/v1/funcionarios/{id}");

        Assert.DoesNotContain(cpf, corpo);
        Assert.DoesNotContain(Dados.SenhaForte, corpo);
        Assert.Contains(cpf[6..9], corpo);

        var lista = await _http.GetStringAsync("/api/v1/funcionarios");
        Assert.DoesNotContain(cpf, lista);
    }

    [Fact]
    public async Task Cadastro_valida_senha_cargo_e_CPF_repetido()
    {
        var (_, cpf) = await NovoFuncionarioAsync();

        var repetido = await _http.PostAsJsonAsync("/api/v1/funcionarios", new
        {
            nome = "Outro", cpf, cargo = "Mecanico", valorHora = 50m, senha = Dados.SenhaForte,
        });
        Assert.Equal(HttpStatusCode.Conflict, repetido.StatusCode);

        var senhaFraca = await _http.PostAsJsonAsync("/api/v1/funcionarios", new
        {
            nome = "Outro", cpf = Dados.Cpf(), cargo = "Mecanico", valorHora = 50m, senha = "12345678",
        });
        Assert.Equal(HttpStatusCode.BadRequest, senhaFraca.StatusCode);

        var cargoInvalido = await _http.PostAsJsonAsync("/api/v1/funcionarios", new
        {
            nome = "Outro", cpf = Dados.Cpf(), cargo = 42, valorHora = 50m, senha = Dados.SenhaForte,
        });
        Assert.Equal(HttpStatusCode.BadRequest, cargoInvalido.StatusCode);

        var valorZero = await _http.PostAsJsonAsync("/api/v1/funcionarios", new
        {
            nome = "Outro", cpf = Dados.Cpf(), cargo = "Mecanico", valorHora = 0m, senha = Dados.SenhaForte,
        });
        Assert.Equal(HttpStatusCode.BadRequest, valorZero.StatusCode);
    }

    [Fact]
    public async Task Atualiza_cargo_e_valor_e_o_novo_cargo_vai_no_token()
    {
        var (id, cpf) = await NovoFuncionarioAsync("Mecanico");

        var atualizado = await _http.PutAsJsonAsync($"/api/v1/funcionarios/{id}", new
        {
            nome = "Bruno Lima", contato = "11977776666", cargo = "Supervisor", valorHora = 120m,
        });
        Assert.Equal("Supervisor", (await atualizado.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cargo").GetString());

        var login = await (await Entrar(cpf, Dados.SenhaForte)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Supervisor", login.GetProperty("cargo").GetString());

        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.PutAsJsonAsync("/api/v1/funcionarios/999999", new { nome = "X", cargo = "Mecanico", valorHora = 1m })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/v1/funcionarios/999999")).StatusCode);
    }
}
