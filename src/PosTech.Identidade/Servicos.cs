using System.Net.Mail;

namespace PosTech.Identidade;

public sealed record NovoCliente(string? Cpf, string? Cnpj, string? NomeCompleto, string? Telefone, string? Email);

public sealed record DadosDoCliente(string? NomeCompleto, string? Telefone, string? Email);

public sealed record NovoVeiculo(
    int ClienteId, string? Marca, string? Modelo, string? Placa, string? Cor,
    int? AnoModelo, int? AnoFabricacao, int? KmEntrada);

public sealed record DadosDoVeiculo(
    string? Marca, string? Modelo, string? Cor, int? AnoModelo, int? AnoFabricacao, int? KmEntrada);

public sealed record NovoFuncionario(
    string? Nome, string? Contato, string? Cpf, ECargoFuncionario Cargo, decimal ValorHora, string? Senha);

public sealed record DadosDoFuncionario(string? Nome, string? Contato, ECargoFuncionario Cargo, decimal ValorHora);

/// <summary>
/// Cadastro de clientes, veículos e funcionários.
///
/// A validação de forma fica aqui, antes do banco; a de unicidade fica no
/// banco, nos índices únicos — é a única que aguenta duas requisições
/// simultâneas com o mesmo CPF. A checagem prévia de duplicidade existe só
/// para o caso comum sair com mensagem boa sem depender do erro do SQL.
/// </summary>
public sealed class Cadastro(
    IClienteRepositorio clientes,
    IVeiculoRepositorio veiculos,
    IFuncionarioRepositorio funcionarios)
{
    public async Task<Cliente> CadastrarClienteAsync(NovoCliente novo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(novo);

        var temCpf = !string.IsNullOrWhiteSpace(novo.Cpf);
        var temCnpj = !string.IsNullOrWhiteSpace(novo.Cnpj);

        if (temCpf == temCnpj)
            throw new ArgumentException("Informe exatamente um documento: CPF ou CNPJ.");

        var cliente = new Cliente
        {
            CPF = temCpf ? Cpf.Criar(novo.Cpf).Valor : null,
            CNPJ = temCnpj ? Cnpj.Criar(novo.Cnpj).Valor : null,
            NomeCompleto = Obrigatorio(novo.NomeCompleto, "nome", 150),
            Telefone = Opcional(novo.Telefone, 20),
            Email = EmailOpcional(novo.Email),
        };

        if (await clientes.ObterPorDocumentoAsync(cliente.Documento, cancellationToken).ConfigureAwait(false) is not null)
            throw new RegistroDuplicadoException(temCpf ? "CPF" : "CNPJ", cliente.Documento);

        await clientes.InserirAsync(cliente, cancellationToken).ConfigureAwait(false);
        return cliente;
    }

    /// <summary>
    /// Busca por CPF ou CNPJ, com ou sem pontuação. Documento que não passa
    /// nos dígitos verificadores nem chega ao banco.
    /// </summary>
    public async Task<Cliente?> BuscarClientePorDocumentoAsync(string? documento, CancellationToken cancellationToken = default)
    {
        var digitos = new string((documento ?? string.Empty).Where(char.IsDigit).ToArray());

        var normalizado = digitos.Length switch
        {
            Cpf.Digitos => Cpf.Criar(digitos).Valor,
            Cnpj.Digitos => Cnpj.Criar(digitos).Valor,
            _ => throw new ArgumentException("Documento deve ter 11 (CPF) ou 14 (CNPJ) dígitos."),
        };

        return await clientes.ObterPorDocumentoAsync(normalizado, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Cliente> AtualizarClienteAsync(int id, DadosDoCliente dados, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dados);

        var cliente = await clientes.ObterAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new RegistroNaoEncontradoException("Cliente", id);

        cliente.NomeCompleto = Obrigatorio(dados.NomeCompleto, "nome", 150);
        cliente.Telefone = Opcional(dados.Telefone, 20);
        cliente.Email = EmailOpcional(dados.Email);

        if (!await clientes.AtualizarAsync(cliente, cancellationToken).ConfigureAwait(false))
            throw new RegistroNaoEncontradoException("Cliente", id);

        return cliente;
    }

    public async Task<Veiculo> CadastrarVeiculoAsync(NovoVeiculo novo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(novo);

        var veiculo = new Veiculo
        {
            ClienteId = novo.ClienteId,
            Marca = Obrigatorio(novo.Marca, "marca", 50),
            Modelo = Obrigatorio(novo.Modelo, "modelo", 50),
            Placa = Placa.Normalizar(novo.Placa),
            Cor = Opcional(novo.Cor, 30),
        };

        AplicarAnosEKm(veiculo, novo.AnoModelo, novo.AnoFabricacao, novo.KmEntrada);

        // Cliente desativado não recebe veículo novo. A FK sozinha deixaria,
        // porque a linha do cliente continua existindo.
        if (await clientes.ObterAsync(novo.ClienteId, cancellationToken).ConfigureAwait(false) is null)
            throw new RegistroNaoEncontradoException("Cliente", novo.ClienteId);

        await veiculos.InserirAsync(veiculo, cancellationToken).ConfigureAwait(false);
        return veiculo;
    }

    public async Task<Veiculo> AtualizarVeiculoAsync(int id, DadosDoVeiculo dados, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dados);

        var veiculo = await veiculos.ObterAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new RegistroNaoEncontradoException("Veículo", id);

        veiculo.Marca = Obrigatorio(dados.Marca, "marca", 50);
        veiculo.Modelo = Obrigatorio(dados.Modelo, "modelo", 50);
        veiculo.Cor = Opcional(dados.Cor, 30);
        AplicarAnosEKm(veiculo, dados.AnoModelo, dados.AnoFabricacao, dados.KmEntrada);

        if (!await veiculos.AtualizarAsync(veiculo, cancellationToken).ConfigureAwait(false))
            throw new RegistroNaoEncontradoException("Veículo", id);

        return veiculo;
    }

    public async Task<Funcionario> CadastrarFuncionarioAsync(NovoFuncionario novo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(novo);

        var funcionario = new Funcionario
        {
            Nome = Obrigatorio(novo.Nome, "nome", 100),
            Contato = Opcional(novo.Contato, 50),
            CPF = Cpf.Criar(novo.Cpf).Valor,
            Cargo = CargoValido(novo.Cargo),
            ValorHora = ValorHoraValido(novo.ValorHora),
        };

        Acesso.ValidarSenhaNova(novo.Senha);

        if (await funcionarios.ObterPorCpfAsync(funcionario.CPF, cancellationToken).ConfigureAwait(false) is not null)
            throw new RegistroDuplicadoException("CPF", funcionario.CPF);

        await funcionarios.InserirAsync(funcionario, Acesso.Hash(novo.Senha!), cancellationToken).ConfigureAwait(false);
        return funcionario;
    }

    public async Task<Funcionario> AtualizarFuncionarioAsync(int id, DadosDoFuncionario dados, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dados);

        var funcionario = await funcionarios.ObterAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new RegistroNaoEncontradoException("Funcionário", id);

        funcionario.Nome = Obrigatorio(dados.Nome, "nome", 100);
        funcionario.Contato = Opcional(dados.Contato, 50);
        funcionario.Cargo = CargoValido(dados.Cargo);
        funcionario.ValorHora = ValorHoraValido(dados.ValorHora);

        if (!await funcionarios.AtualizarAsync(funcionario, cancellationToken).ConfigureAwait(false))
            throw new RegistroNaoEncontradoException("Funcionário", id);

        return funcionario;
    }

    private static void AplicarAnosEKm(Veiculo veiculo, int? anoModelo, int? anoFabricacao, int? km)
    {
        var limite = DateTime.UtcNow.Year + 1;

        if (anoModelo is < 1900 || anoModelo > limite || anoFabricacao is < 1900 || anoFabricacao > limite)
            throw new ArgumentException($"Ano fora do intervalo 1900–{limite}.");

        if (anoModelo is { } m && anoFabricacao is { } f && (m < f || m > f + 1))
            throw new ArgumentException("O ano do modelo é o de fabricação ou o seguinte.");

        if (km is < 0)
            throw new ArgumentException("Quilometragem não pode ser negativa.");

        veiculo.AnoModelo = anoModelo;
        veiculo.AnoFabricacao = anoFabricacao;
        veiculo.KmEntrada = km;
    }

    private static ECargoFuncionario CargoValido(ECargoFuncionario cargo) =>
        Enum.IsDefined(cargo) ? cargo : throw new ArgumentException($"Cargo inválido: {cargo}.");

    private static decimal ValorHoraValido(decimal valor) =>
        valor > 0 ? decimal.Round(valor, 2) : throw new ArgumentException("O valor da hora tem que ser positivo.");

    private static string Obrigatorio(string? valor, string campo, int maximo)
    {
        var limpo = valor?.Trim();

        if (string.IsNullOrEmpty(limpo))
            throw new ArgumentException($"O campo {campo} é obrigatório.");

        if (limpo.Length > maximo)
            throw new ArgumentException($"O campo {campo} tem no máximo {maximo} caracteres.");

        return limpo;
    }

    private static string? Opcional(string? valor, int maximo)
    {
        var limpo = valor?.Trim();

        if (string.IsNullOrEmpty(limpo))
            return null;

        return limpo.Length <= maximo ? limpo : throw new ArgumentException($"Campo com mais de {maximo} caracteres.");
    }

    private static string? EmailOpcional(string? valor)
    {
        var limpo = Opcional(valor, 150);

        // O e-mail vai para o Mercado Pago na cobrança: e-mail malformado é
        // cobrança recusada lá na frente, longe de quem digitou.
        if (limpo is not null && (!MailAddress.TryCreate(limpo, out var endereco) || endereco.Address != limpo))
            throw new ArgumentException($"E-mail inválido: '{limpo}'.");

        return limpo;
    }
}

public enum EResultadoDoLogin
{
    Sucesso,
    CredenciaisInvalidas,
    Bloqueado,
}

public sealed record Login(EResultadoDoLogin Resultado, Funcionario? Funcionario, DateTime? BloqueadoAte);

/// <summary>
/// Login de funcionário por CPF e senha.
///
/// Toda falha de credencial devolve o mesmo resultado — CPF que não existe,
/// funcionário sem senha e senha errada são indistinguíveis de fora, senão o
/// login vira um oráculo para descobrir quais CPFs são de funcionários.
/// </summary>
public sealed class Autenticacao(IFuncionarioRepositorio funcionarios, Func<DateTime>? relogio = null)
{
    private readonly Func<DateTime> _agora = relogio ?? (() => DateTime.UtcNow);

    public async Task<Login> EntrarAsync(string? cpf, string? senha, CancellationToken cancellationToken = default)
    {
        var invalido = new Login(EResultadoDoLogin.CredenciaisInvalidas, null, null);

        if (!Cpf.TentarCriar(cpf, out var documento, out _) || string.IsNullOrEmpty(senha))
            return invalido;

        var funcionario = await funcionarios.ObterPorCpfAsync(documento!.Valor, cancellationToken).ConfigureAwait(false);

        if (funcionario is null)
            return invalido;

        var seguranca = await funcionarios.ObterSegurancaAsync(funcionario.Id, cancellationToken).ConfigureAwait(false);

        if (seguranca is null)
            return invalido;

        var agora = _agora();

        // Bloqueado não confere a senha: conferir e responder diferente
        // deixaria quem ataca continuar testando durante o bloqueio.
        if (Acesso.EstaBloqueado(seguranca, agora))
            return new Login(EResultadoDoLogin.Bloqueado, null, seguranca.BloqueadoAte);

        if (!Acesso.Confere(senha, seguranca.SenhaHash))
        {
            Acesso.RegistrarFalha(seguranca, agora);
            await funcionarios.SalvarSegurancaAsync(seguranca, cancellationToken).ConfigureAwait(false);

            return Acesso.EstaBloqueado(seguranca, agora)
                ? new Login(EResultadoDoLogin.Bloqueado, null, seguranca.BloqueadoAte)
                : invalido;
        }

        if (seguranca.TentativasFalhas > 0 || seguranca.BloqueadoAte is not null)
        {
            Acesso.RegistrarSucesso(seguranca, agora);
            await funcionarios.SalvarSegurancaAsync(seguranca, cancellationToken).ConfigureAwait(false);
        }

        return new Login(EResultadoDoLogin.Sucesso, funcionario, null);
    }

    /// <summary>
    /// Troca de senha, exigindo CPF e senha atual. Passa pelo mesmo
    /// <see cref="EntrarAsync"/> de propósito: conferir a senha atual por fora
    /// dele seria uma segunda porta sem o bloqueio de tentativas.
    /// </summary>
    public async Task<Login> TrocarSenhaAsync(string? cpf, string? atual, string? nova, CancellationToken cancellationToken = default)
    {
        Acesso.ValidarSenhaNova(nova);

        var login = await EntrarAsync(cpf, atual, cancellationToken).ConfigureAwait(false);

        if (login.Resultado != EResultadoDoLogin.Sucesso)
            return login;

        var seguranca = await funcionarios.ObterSegurancaAsync(login.Funcionario!.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Credencial sumiu entre o login e a troca.");

        seguranca.SenhaHash = Acesso.Hash(nova!);
        Acesso.RegistrarSucesso(seguranca, _agora());

        await funcionarios.SalvarSegurancaAsync(seguranca, cancellationToken).ConfigureAwait(false);
        return login;
    }
}
