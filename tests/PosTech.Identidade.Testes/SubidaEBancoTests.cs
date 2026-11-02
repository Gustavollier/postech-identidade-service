using System.Net;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using PosTech.Identidade;

namespace PosTech.Identidade.Testes;

/// <summary>A API sobe inteira, e se recusa a subir sem o que precisa.</summary>
[Collection("identidade")]
public class SubidaDoServicoTests(AmbienteDaIdentidade ambiente)
{
    [Fact]
    public async Task Liveness_readiness_e_swagger_respondem()
    {
        var http = ambiente.Cliente();

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/ready")).StatusCode);

        var swagger = await http.GetStringAsync("/swagger/v1/swagger.json");
        Assert.Contains("/api/v1/autenticacao/login", swagger);
    }

    [Theory]
    [InlineData("SqlConnectionString", null)]
    [InlineData("Jwt:SecretKey", null)]
    [InlineData("Jwt:SecretKey", "curta")]
    [InlineData("Jwt:Issuer", null)]
    [InlineData("Jwt:Audience", null)]
    public void Sem_configuracao_obrigatoria_o_servico_nao_sobe(string chave, string? valor)
    {
        var configuracao = new Dictionary<string, string?>
        {
            ["SqlConnectionString"] = ambiente.ConnectionString,
            ["Jwt:SecretKey"] = AmbienteDaIdentidade.ChaveJwt,
            ["Jwt:Issuer"] = AmbienteDaIdentidade.Emissor,
            ["Jwt:Audience"] = AmbienteDaIdentidade.Audiencia,
            [chave] = valor,
        };

        using var fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            foreach (var (k, v) in configuracao.Where(c => c.Value is not null))
                builder.UseSetting(k, v);
        });

        var erro = Assert.ThrowsAny<Exception>(() => fabrica.CreateClient());

        Assert.Contains(chave, erro.ToString());
    }
}

/// <summary>
/// O schema e as restrições, direto no banco. É a última linha de defesa: o
/// que passar por qualquer outro caminho que não a API — a Auth Function, um
/// script de carga — esbarra aqui.
/// </summary>
[Collection("identidade")]
public class SchemaTests(AmbienteDaIdentidade ambiente)
{
    [Fact]
    public async Task Aplicar_o_schema_de_novo_nao_quebra_nada()
    {
        // Toda réplica aplica na partida. Tem que ser idempotente.
        await InstaladorDoSchema.AplicarAsync(ambiente.Conexoes);
        await InstaladorDoSchema.AplicarAsync(ambiente.Conexoes);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("52998224725", "11222333000181")]
    public async Task O_banco_recusa_cliente_sem_documento_ou_com_os_dois(string? cpf, string? cnpj)
    {
        await using var conexao = new SqlConnection(ambiente.ConnectionString);

        var erro = await Assert.ThrowsAsync<SqlException>(() => conexao.ExecuteAsync(
            "INSERT INTO Cliente (CPF, CNPJ, NomeCompleto) VALUES (@cpf, @cnpj, 'Direto no banco')",
            new { cpf, cnpj }));

        Assert.Contains("CK_Cliente_UmDocumento", erro.Message);
    }

    [Fact]
    public async Task O_repositorio_traduz_o_indice_unico_em_erro_de_negocio()
    {
        // O caminho que a checagem prévia do serviço não cobre: alguém gravou
        // entre a checagem e o INSERT.
        var repositorio = new ClienteRepositorio(ambiente.Conexoes);
        var cpf = Dados.Cpf();

        await repositorio.InserirAsync(new Cliente { CPF = cpf, NomeCompleto = "Primeiro" });

        var erro = await Assert.ThrowsAsync<RegistroDuplicadoException>(
            () => repositorio.InserirAsync(new Cliente { CPF = cpf, NomeCompleto = "Segundo" }));

        Assert.Equal("CPF", erro.Campo);
    }

    [Fact]
    public async Task Funcionario_e_credencial_nascem_juntos_ou_nao_nascem()
    {
        var repositorio = new FuncionarioRepositorio(ambiente.Conexoes);
        var cpf = Dados.Cpf();

        await repositorio.InserirAsync(
            new Funcionario { Nome = "Primeiro", CPF = cpf, Cargo = ECargoFuncionario.Mecanico, ValorHora = 50m },
            Acesso.Hash(Dados.SenhaForte));

        await Assert.ThrowsAsync<RegistroDuplicadoException>(() => repositorio.InserirAsync(
            new Funcionario { Nome = "Segundo", CPF = cpf, Cargo = ECargoFuncionario.Mecanico, ValorHora = 50m },
            Acesso.Hash(Dados.SenhaForte)));

        await using var conexao = new SqlConnection(ambiente.ConnectionString);

        // Nenhuma credencial órfã ficou da tentativa que falhou.
        Assert.Equal(0, await conexao.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Seguranca s LEFT JOIN Funcionario f ON f.Id = s.FuncionarioId WHERE f.Id IS NULL"));
    }

    [Fact]
    public async Task Veiculo_para_cliente_inexistente_esbarra_na_chave_estrangeira()
    {
        var repositorio = new VeiculoRepositorio(ambiente.Conexoes);

        var erro = await Assert.ThrowsAsync<RegistroNaoEncontradoException>(() => repositorio.InserirAsync(
            new Veiculo { ClienteId = 999999, Marca = "VW", Modelo = "Gol", Placa = Dados.Placa() }));

        Assert.Equal("Cliente", erro.Entidade);
    }
}
