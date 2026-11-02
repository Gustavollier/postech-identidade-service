using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PosTech.Identidade;
using Testcontainers.MsSql;

namespace PosTech.Identidade.Testes;

/// <summary>
/// A API do Identidade no ar, com SQL Server de verdade.
///
/// Fixture de coleção para o contêiner subir uma vez só. Os testes não limpam
/// o banco entre si: cada um gera os seus documentos e placas com
/// <see cref="Dados"/>, então não colidem.
/// </summary>
public sealed class AmbienteDaIdentidade : IAsyncLifetime
{
    public const string ChaveJwt = "chave-de-teste-com-mais-de-32-bytes-de-tamanho!";
    public const string Emissor = "postech-identidade";
    public const string Audiencia = "postech-api";

    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public WebApplicationFactory<Program> Fabrica { get; private set; } = null!;

    public FabricaDeConexao Conexoes { get; private set; } = null!;

    public string ConnectionString { get; private set; } = string.Empty;

    public HttpClient Cliente() => Fabrica.CreateClient();

    public async Task InitializeAsync()
    {
        await _sql.StartAsync();

        ConnectionString = _sql.GetConnectionString();
        Conexoes = new FabricaDeConexao(ConnectionString);

        Fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("SqlConnectionString", ConnectionString);
            builder.UseSetting("Jwt:SecretKey", ChaveJwt);
            builder.UseSetting("Jwt:Issuer", Emissor);
            builder.UseSetting("Jwt:Audience", Audiencia);
        });

        // A subida aplica o schema.
        _ = Fabrica.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await Fabrica.DisposeAsync();
        await _sql.DisposeAsync();
    }

    /// <summary>Cadastra um cliente pela API e devolve o id.</summary>
    public async Task<int> NovoClienteAsync(string? cpf = null)
    {
        var resposta = await Cliente().PostAsJsonAsync("/api/v1/clientes", new
        {
            cpf = cpf ?? Dados.Cpf(), nomeCompleto = "Ana Prado", email = "ana@oficina.test",
        });

        resposta.EnsureSuccessStatusCode();
        return (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }
}

[CollectionDefinition("identidade")]
public sealed class ColecaoDaIdentidade : ICollectionFixture<AmbienteDaIdentidade>;

/// <summary>
/// Documentos e placas válidos e únicos para cada teste. CPF e CNPJ com
/// dígito verificador certo, porque o serviço recusa os inválidos antes do
/// banco.
/// </summary>
public static class Dados
{
    private static int _sequencia = Random.Shared.Next(1_000_000, 9_000_000);

    public static string Cpf()
    {
        var base9 = Interlocked.Increment(ref _sequencia).ToString("D9");
        var digitos = base9.Select(c => c - '0').ToList();

        digitos.Add(Dv(digitos, 10));
        digitos.Add(Dv(digitos, 11));

        return string.Concat(digitos);

        static int Dv(List<int> d, int peso)
        {
            var soma = d.Select((x, i) => x * (peso - i)).Sum();
            var resto = soma % 11;
            return resto < 2 ? 0 : 11 - resto;
        }
    }

    public static string Cnpj()
    {
        var base12 = Interlocked.Increment(ref _sequencia).ToString("D8") + "0001";
        var digitos = base12.Select(c => c - '0').ToList();

        digitos.Add(Dv(digitos, [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]));
        digitos.Add(Dv(digitos, [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]));

        return string.Concat(digitos);

        static int Dv(List<int> d, int[] pesos)
        {
            var soma = d.Select((x, i) => x * pesos[i]).Sum();
            var resto = soma % 11;
            return resto < 2 ? 0 : 11 - resto;
        }
    }

    /// <summary>Placa Mercosul única: ABC1D23 com letras e números da sequência.</summary>
    public static string Placa()
    {
        var n = Interlocked.Increment(ref _sequencia);
        char Letra(int i) => (char)('A' + (n / (int)Math.Pow(26, i) % 26));

        return $"{Letra(3)}{Letra(2)}{Letra(1)}{n % 10}{Letra(0)}{n / 10 % 10}{n / 100 % 10}";
    }

    public const string SenhaForte = "Oficina@2026";
}
