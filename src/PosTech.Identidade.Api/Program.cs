using System.Text.Json.Serialization;
using PosTech.Identidade;
using PosTech.Identidade.Api;
using PosTech.Identidade.Api.Controllers;

var builder = WebApplication.CreateBuilder(args);

// Obrigatória: o serviço se recusa a subir sem ela. Falhar na partida é
// melhor que descobrir na primeira requisição.
var connectionString = builder.Configuration["SqlConnectionString"]
    ?? throw new InvalidOperationException("SqlConnectionString não configurada.");

builder.Services.AddControllers(opcoes => opcoes.Filters.Add<TradutorDeErros>())
    .AddJsonOptions(opcoes => opcoes.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

builder.Logging.AddJsonConsole(opcoes =>
{
    opcoes.IncludeScopes = true;
    opcoes.UseUtcTimestamp = true;
});

builder.Services.AddSingleton(new FabricaDeConexao(connectionString));
builder.Services.AddScoped<IClienteRepositorio, ClienteRepositorio>();
builder.Services.AddScoped<IVeiculoRepositorio, VeiculoRepositorio>();
builder.Services.AddScoped<IFuncionarioRepositorio, FuncionarioRepositorio>();
builder.Services.AddScoped<Cadastro>();
builder.Services.AddScoped(sp => new Autenticacao(sp.GetRequiredService<IFuncionarioRepositorio>()));

// Construído já na partida: chave JWT ausente ou curta derruba a subida, e
// não o primeiro login.
var emissor = new EmissorDeToken(builder.Configuration);
builder.Services.AddSingleton(emissor);

var app = builder.Build();

// O schema é idempotente e embutido no assembly. Aplicar aqui tira do caminho
// o "esqueceram de rodar o script" de cada ambiente novo.
await InstaladorDoSchema.AplicarAsync(app.Services.GetRequiredService<FabricaDeConexao>());

app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();
app.MapHealthChecks("/health");
app.MapGet("/health/ready", () => Results.Ok(new { status = "pronto" }));

app.Run();

/// <summary>Exposto para os testes de integração subirem a API em memória.</summary>
public partial class Program;
