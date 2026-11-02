using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace PosTech.Identidade.Api.Controllers;

/// <summary>
/// Transforma as falhas de negócio em respostas HTTP, num lugar só. Espalhar
/// <c>try/catch</c> pelos controllers faria um esquecer o que o outro trata,
/// e o esquecido vira 500.
/// </summary>
public sealed class TradutorDeErros : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var (status, titulo) = context.Exception switch
        {
            RegistroNaoEncontradoException e => (StatusCodes.Status404NotFound, e.Message),
            RegistroDuplicadoException e => (StatusCodes.Status409Conflict, e.Message),
            ArgumentException e => (StatusCodes.Status400BadRequest, e.Message),
            _ => (0, string.Empty),
        };

        if (status == 0)
            return;

        context.Result = new ObjectResult(new ProblemDetails { Status = status, Title = titulo }) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}

public sealed record AtualizarClienteRequest(string? NomeCompleto, string? Telefone, string? Email);

/// <summary>
/// Clientes. O OS Service não consulta este serviço por HTTP ao abrir a ordem:
/// quem abre (o front da recepção) busca aqui e manda nome, e-mail e veículo
/// no corpo, e o OS os carrega no <c>OrdemAberta</c>. Assim a saga não depende
/// do Identidade estar no ar.
/// </summary>
[ApiController]
[Route("api/v1/clientes")]
public sealed class ClientesController(Cadastro cadastro, IClienteRepositorio clientes, IVeiculoRepositorio veiculos)
    : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cadastrar([FromBody] NovoCliente corpo, CancellationToken cancellationToken)
    {
        var cliente = await cadastro.CadastrarClienteAsync(corpo, cancellationToken);

        return CreatedAtAction(nameof(Obter), new { id = cliente.Id }, Mapear(cliente));
    }

    /// <summary>Lista paginada. <c>?pagina=1&amp;tamanho=20</c>, tamanho até 100.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Listar(
        [FromQuery] int pagina = 1, [FromQuery] int tamanho = 20, CancellationToken cancellationToken = default)
    {
        var resultado = await clientes.ListarAsync(pagina, tamanho, cancellationToken);

        return Ok(new
        {
            itens = resultado.Itens.Select(Mapear),
            pagina = resultado.PaginaAtual,
            tamanho = resultado.TamanhoDaPagina,
            total = resultado.Total,
            totalDePaginas = resultado.TotalDePaginas,
        });
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obter(int id, CancellationToken cancellationToken)
    {
        var cliente = await clientes.ObterAsync(id, cancellationToken);

        return cliente is null ? NotFound() : Ok(Mapear(cliente));
    }

    /// <summary>Por CPF ou CNPJ, com ou sem pontuação: <c>?documento=123.456.789-09</c>.</summary>
    [HttpGet("busca")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PorDocumento([FromQuery] string? documento, CancellationToken cancellationToken)
    {
        var cliente = await cadastro.BuscarClientePorDocumentoAsync(documento, cancellationToken);

        return cliente is null ? NotFound() : Ok(Mapear(cliente));
    }

    [HttpPut("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Atualizar(int id, [FromBody] AtualizarClienteRequest corpo, CancellationToken cancellationToken)
    {
        var cliente = await cadastro.AtualizarClienteAsync(
            id, new DadosDoCliente(corpo.NomeCompleto, corpo.Telefone, corpo.Email), cancellationToken);

        return Ok(Mapear(cliente));
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Desativar(int id, CancellationToken cancellationToken) =>
        await clientes.DesativarAsync(id, cancellationToken) ? NoContent() : NotFound();

    [HttpGet("{id:int}/veiculos")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Veiculos(int id, CancellationToken cancellationToken)
    {
        if (await clientes.ObterAsync(id, cancellationToken) is null)
            return NotFound();

        var lista = await veiculos.ListarDoClienteAsync(id, cancellationToken);

        return Ok(lista.Select(VeiculosController.Mapear));
    }

    internal static object Mapear(Cliente c) => new
    {
        id = c.Id,
        cpf = c.CPF,
        cnpj = c.CNPJ,
        nomeCompleto = c.NomeCompleto,
        telefone = c.Telefone,
        email = c.Email,
        criadoEm = c.CreatedAt,
        atualizadoEm = c.UpdatedAt,
    };
}

[ApiController]
[Route("api/v1/veiculos")]
public sealed class VeiculosController(Cadastro cadastro, IVeiculoRepositorio veiculos) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cadastrar([FromBody] NovoVeiculo corpo, CancellationToken cancellationToken)
    {
        var veiculo = await cadastro.CadastrarVeiculoAsync(corpo, cancellationToken);

        return CreatedAtAction(nameof(Obter), new { id = veiculo.Id }, Mapear(veiculo));
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obter(int id, CancellationToken cancellationToken)
    {
        var veiculo = await veiculos.ObterAsync(id, cancellationToken);

        return veiculo is null ? NotFound() : Ok(Mapear(veiculo));
    }

    [HttpPut("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Atualizar(int id, [FromBody] DadosDoVeiculo corpo, CancellationToken cancellationToken) =>
        Ok(Mapear(await cadastro.AtualizarVeiculoAsync(id, corpo, cancellationToken)));

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Desativar(int id, CancellationToken cancellationToken) =>
        await veiculos.DesativarAsync(id, cancellationToken) ? NoContent() : NotFound();

    internal static object Mapear(Veiculo v) => new
    {
        id = v.Id,
        clienteId = v.ClienteId,
        marca = v.Marca,
        modelo = v.Modelo,
        placa = v.Placa,
        cor = v.Cor,
        anoModelo = v.AnoModelo,
        anoFabricacao = v.AnoFabricacao,
        kmEntrada = v.KmEntrada,

        // O texto que o OS Service carrega na ordem.
        descricao = v.Descricao,
    };
}

[ApiController]
[Route("api/v1/funcionarios")]
public sealed class FuncionariosController(Cadastro cadastro, IFuncionarioRepositorio funcionarios) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cadastrar([FromBody] NovoFuncionario corpo, CancellationToken cancellationToken)
    {
        var funcionario = await cadastro.CadastrarFuncionarioAsync(corpo, cancellationToken);

        return CreatedAtAction(nameof(Obter), new { id = funcionario.Id }, Mapear(funcionario));
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Listar(CancellationToken cancellationToken) =>
        Ok((await funcionarios.ListarAsync(cancellationToken)).Select(Mapear));

    [HttpGet("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obter(int id, CancellationToken cancellationToken)
    {
        var funcionario = await funcionarios.ObterAsync(id, cancellationToken);

        return funcionario is null ? NotFound() : Ok(Mapear(funcionario));
    }

    [HttpPut("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Atualizar(int id, [FromBody] DadosDoFuncionario corpo, CancellationToken cancellationToken) =>
        Ok(Mapear(await cadastro.AtualizarFuncionarioAsync(id, corpo, cancellationToken)));

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Desativar(int id, CancellationToken cancellationToken) =>
        await funcionarios.DesativarAsync(id, cancellationToken) ? NoContent() : NotFound();

    // Sem CPF completo na resposta da listagem: é dado pessoal, e a tela de
    // equipe não precisa dele para nada.
    private static object Mapear(Funcionario f) => new
    {
        id = f.Id,
        nome = f.Nome,
        contato = f.Contato,
        cpf = $"***.***.{f.CPF[6..9]}-**",
        cargo = f.Cargo,
        valorHora = f.ValorHora,
    };
}

public sealed record LoginRequest(string? Cpf, string? Senha);

public sealed record TrocarSenhaRequest(string? Cpf, string? SenhaAtual, string? NovaSenha);

[ApiController]
[Route("api/v1/autenticacao")]
public sealed class AutenticacaoController(Autenticacao autenticacao, EmissorDeToken emissor) : ControllerBase
{
    /// <summary>
    /// Login de funcionário. CPF inexistente e senha errada respondem igual.
    /// Cinco erros seguidos bloqueiam por quinze minutos (423).
    /// </summary>
    [HttpPost("login")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status423Locked)]
    public async Task<IActionResult> Login([FromBody] LoginRequest corpo, CancellationToken cancellationToken)
    {
        var login = await autenticacao.EntrarAsync(corpo.Cpf, corpo.Senha, cancellationToken);

        return login.Resultado switch
        {
            EResultadoDoLogin.Sucesso => Ok(emissor.Emitir(login.Funcionario!)),
            EResultadoDoLogin.Bloqueado => Bloqueado(login),
            _ => Unauthorized(new ProblemDetails { Status = 401, Title = "CPF ou senha inválidos." }),
        };
    }

    [HttpPost("trocar-senha")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status423Locked)]
    public async Task<IActionResult> TrocarSenha([FromBody] TrocarSenhaRequest corpo, CancellationToken cancellationToken)
    {
        var login = await autenticacao.TrocarSenhaAsync(corpo.Cpf, corpo.SenhaAtual, corpo.NovaSenha, cancellationToken);

        return login.Resultado switch
        {
            EResultadoDoLogin.Sucesso => NoContent(),
            EResultadoDoLogin.Bloqueado => Bloqueado(login),
            _ => Unauthorized(new ProblemDetails { Status = 401, Title = "CPF ou senha inválidos." }),
        };
    }

    private ObjectResult Bloqueado(Login login) => StatusCode(StatusCodes.Status423Locked, new ProblemDetails
    {
        Status = StatusCodes.Status423Locked,
        Title = "Acesso bloqueado por excesso de tentativas.",
        Extensions = { ["bloqueadoAte"] = login.BloqueadoAte },
    });
}
