using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PosTech.Identidade.Testes;

/// <summary>
/// Quem pode o quê nas rotas do Identidade.
///
/// É o serviço que guarda dado pessoal: CPF, e-mail e telefone de cliente, e o
/// cadastro da equipe. Sem a checagem, um cliente autenticado baixava a
/// listagem inteira de clientes e desativava funcionários.
/// </summary>
[Collection("identidade")]
public class AcessoDaApiTests(AmbienteDaIdentidade ambiente)
{
    private HttpClient Como(string? token) => ambiente.Cliente().Como(token);

    private static object UmFuncionario() => new
    {
        nome = "Bruno Lima", contato = "11977776666", cpf = Dados.Cpf(), cargo = "Mecanico",
        valorHora = 85.5m, senha = Dados.SenhaForte,
    };

    private async Task<int> NovoVeiculoAsync(int clienteId)
    {
        var resposta = await ambiente.Cliente().PostAsJsonAsync("/api/v1/veiculos", new
        {
            clienteId, marca = "VW", modelo = "Gol", placa = Dados.Placa(), cor = "Prata",
            anoModelo = 2022, anoFabricacao = 2021, kmEntrada = 42000,
        });

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<int> NovoFuncionarioAsync()
    {
        var resposta = await ambiente.Cliente().PostAsJsonAsync("/api/v1/funcionarios", UmFuncionario());

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    [Theory]
    [InlineData("/api/v1/clientes")]
    [InlineData("/api/v1/clientes/1")]
    [InlineData("/api/v1/clientes/1/veiculos")]
    [InlineData("/api/v1/clientes/busca?documento=52998224725")]
    [InlineData("/api/v1/veiculos/1")]
    [InlineData("/api/v1/funcionarios")]
    [InlineData("/api/v1/funcionarios/1")]
    public async Task Sem_token_nenhum_cadastro_e_lido(string rota)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Como(null).GetAsync(rota)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Como(TokenDeTeste.Vencido()).GetAsync(rota)).StatusCode);
    }

    [Fact]
    public async Task O_login_e_o_health_continuam_sem_token()
    {
        // Quem faz login ainda não tem token: 401 aqui é "credencial errada",
        // e não "rota fechada" — o que importa é não ser barrado antes.
        var login = await Como(null).PostAsJsonAsync("/api/v1/autenticacao/login", new { cpf = Dados.Cpf(), senha = "Errada@2026" });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Contains("CPF ou senha", await login.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await Como(null).GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Cliente_nao_lista_clientes_nem_busca_por_documento_nem_ve_a_equipe()
    {
        var eu = await ambiente.NovoClienteAsync();
        var http = Como(TokenDeTeste.Cliente(eu));

        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/v1/clientes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/v1/clientes/busca?documento=52998224725")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/v1/funcionarios")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/v1/funcionarios/1")).StatusCode);
    }

    [Fact]
    public async Task O_cliente_le_o_proprio_cadastro_e_o_de_outro_responde_como_se_nao_existisse()
    {
        var eu = await ambiente.NovoClienteAsync();
        var outro = await ambiente.NovoClienteAsync();
        var http = Como(TokenDeTeste.Cliente(eu));

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/api/v1/clientes/{eu}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/api/v1/clientes/{eu}/veiculos")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/v1/clientes/{outro}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/v1/clientes/{outro}/veiculos")).StatusCode);
    }

    [Fact]
    public async Task O_cliente_le_o_proprio_veiculo_e_nao_o_de_outro()
    {
        var eu = await ambiente.NovoClienteAsync();
        var outro = await ambiente.NovoClienteAsync();
        var meuVeiculo = await NovoVeiculoAsync(eu);
        var veiculoAlheio = await NovoVeiculoAsync(outro);
        var http = Como(TokenDeTeste.Cliente(eu));

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/api/v1/veiculos/{meuVeiculo}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/v1/veiculos/{veiculoAlheio}")).StatusCode);
    }

    [Fact]
    public async Task Cliente_nao_escreve_em_cadastro_nenhum_nem_no_proprio()
    {
        var eu = await ambiente.NovoClienteAsync();
        var meuVeiculo = await NovoVeiculoAsync(eu);
        var http = Como(TokenDeTeste.Cliente(eu));

        var respostas = new[]
        {
            await http.PostAsJsonAsync("/api/v1/clientes", new { cpf = Dados.Cpf(), nomeCompleto = "Intruso" }),
            await http.PutAsJsonAsync($"/api/v1/clientes/{eu}", new { nomeCompleto = "Outro Nome" }),
            await http.DeleteAsync($"/api/v1/clientes/{eu}"),
            await http.PostAsJsonAsync("/api/v1/veiculos", new { clienteId = eu, placa = Dados.Placa() }),
            await http.PutAsJsonAsync($"/api/v1/veiculos/{meuVeiculo}", new { cor = "Preto" }),
            await http.DeleteAsync($"/api/v1/veiculos/{meuVeiculo}"),
            await http.PostAsJsonAsync("/api/v1/funcionarios", UmFuncionario()),
        };

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
    }

    [Fact]
    public async Task Mecanico_cadastra_e_consulta_mas_editar_e_desativar_e_do_gerente()
    {
        var cliente = await ambiente.NovoClienteAsync();
        var veiculo = await NovoVeiculoAsync(cliente);
        var funcionario = await NovoFuncionarioAsync();
        var http = Como(TokenDeTeste.Mecanico());

        // A recepção cadastra cliente e veículo ao abrir a ordem.
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/clientes")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/funcionarios")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Created,
            (await http.PostAsJsonAsync("/api/v1/clientes", new { cpf = Dados.Cpf(), nomeCompleto = "Carla Dias" })).StatusCode);

        var respostas = new[]
        {
            await http.PutAsJsonAsync($"/api/v1/clientes/{cliente}", new { nomeCompleto = "Outro Nome" }),
            await http.DeleteAsync($"/api/v1/clientes/{cliente}"),
            await http.PutAsJsonAsync($"/api/v1/veiculos/{veiculo}", new { cor = "Preto" }),
            await http.DeleteAsync($"/api/v1/veiculos/{veiculo}"),
            await http.PostAsJsonAsync("/api/v1/funcionarios", UmFuncionario()),
            await http.PutAsJsonAsync($"/api/v1/funcionarios/{funcionario}", new { nome = "X", contato = "1", cargo = "Gerente", valorHora = 1 }),
            await http.DeleteAsync($"/api/v1/funcionarios/{funcionario}"),
        };

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
    }

    [Fact]
    public async Task Com_a_equipe_ja_cadastrada_ninguem_cria_funcionario_sem_token()
    {
        // A rota aceita chamada sem token só para o primeiro funcionário de um
        // ambiente novo. Com um cadastrado, a porta está fechada.
        await NovoFuncionarioAsync();

        var resposta = await Como(null).PostAsJsonAsync("/api/v1/funcionarios", UmFuncionario());

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }
}
