using PosTech.Identidade;

namespace PosTech.Identidade.Testes;

public class AcessoTests
{
    private static readonly DateTime Agora = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("")]
    [InlineData("Curta@1")]
    [InlineData("semmaiuscula@1")]
    [InlineData("SemNumero@")]
    [InlineData("SemEspecial1")]
    public void Senha_fora_da_politica_nao_passa(string senha)
    {
        Assert.Throws<ArgumentException>(() => Acesso.ValidarSenhaNova(senha));
    }

    [Fact]
    public void Senha_dentro_da_politica_passa()
    {
        Acesso.ValidarSenhaNova(Dados.SenhaForte);
    }

    [Fact]
    public void O_hash_confere_com_a_senha_e_so_com_ela()
    {
        var hash = Acesso.Hash(Dados.SenhaForte);

        Assert.True(Acesso.Confere(Dados.SenhaForte, hash));
        Assert.False(Acesso.Confere("Outra@2026", hash));
        Assert.DoesNotContain(Dados.SenhaForte, hash);
    }

    [Fact]
    public void Um_hash_gerado_como_na_Fase_3_continua_conferindo()
    {
        // É a mesma chamada do PasswordHasher do monólito. Se o algoritmo
        // mudar aqui, todo funcionário migrado perde o acesso no dia da troca.
        var hashDaFase3 = BCrypt.Net.BCrypt.EnhancedHashPassword(Dados.SenhaForte, BCrypt.Net.HashType.SHA384);

        Assert.True(Acesso.Confere(Dados.SenhaForte, hashDaFase3));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("isto-nao-e-um-hash")]
    public void Hash_ausente_ou_corrompido_e_senha_que_nao_confere(string? hash)
    {
        Assert.False(Acesso.Confere(Dados.SenhaForte, hash));
    }

    [Fact]
    public void Cinco_erros_seguidos_bloqueiam_por_quinze_minutos()
    {
        var seguranca = new Seguranca();

        for (var i = 0; i < Acesso.TentativasAntesDoBloqueio - 1; i++)
            Acesso.RegistrarFalha(seguranca, Agora);

        Assert.False(Acesso.EstaBloqueado(seguranca, Agora));

        Acesso.RegistrarFalha(seguranca, Agora);

        Assert.True(Acesso.EstaBloqueado(seguranca, Agora));
        Assert.True(Acesso.EstaBloqueado(seguranca, Agora.AddMinutes(14)));
        Assert.False(Acesso.EstaBloqueado(seguranca, Agora.AddMinutes(15)));
    }

    [Fact]
    public void Acertar_zera_a_contagem()
    {
        var seguranca = new Seguranca();
        Acesso.RegistrarFalha(seguranca, Agora);
        Acesso.RegistrarFalha(seguranca, Agora);

        Acesso.RegistrarSucesso(seguranca, Agora);

        Assert.Equal(0, seguranca.TentativasFalhas);
        Assert.Null(seguranca.BloqueadoAte);
    }
}

public class PlacaEPaginaTests
{
    [Theory]
    [InlineData("abc-1234", "ABC1234")]
    [InlineData("ABC1D23", "ABC1D23")]
    [InlineData(" abc 1d23 ", "ABC1D23")]
    public void Placa_e_guardada_num_formato_so(string entrada, string esperado)
    {
        // "abc-1234" e "ABC1234" são o mesmo carro, e o índice único só
        // funciona se o banco vir as duas iguais.
        Assert.Equal(esperado, Placa.Normalizar(entrada));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AB1234")]
    [InlineData("1234ABC")]
    [InlineData("ABCD123")]
    public void Placa_fora_dos_dois_padroes_nao_passa(string? entrada)
    {
        Assert.Throws<ArgumentException>(() => Placa.Normalizar(entrada));
    }

    [Theory]
    [InlineData(105, 10, 11)]
    [InlineData(100, 10, 10)]
    [InlineData(0, 10, 0)]
    [InlineData(1, 20, 1)]
    public void Total_de_paginas_arredonda_para_cima(int total, int tamanho, int paginas)
    {
        // O defeito da Fase 3: dividir por 10 fixo, e 105 clientes davam 10
        // páginas.
        Assert.Equal(paginas, new Pagina<int>([], 1, tamanho, total).TotalDePaginas);
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-3, 500, 1, 100)]
    [InlineData(4, 25, 4, 25)]
    public void Pagina_e_tamanho_ficam_dentro_dos_limites(int pagina, int tamanho, int paginaEsperada, int tamanhoEsperado)
    {
        Assert.Equal((paginaEsperada, tamanhoEsperado), Pagina<int>.Normalizar(pagina, tamanho));
    }

    [Fact]
    public void A_descricao_do_veiculo_e_a_que_o_OS_carrega()
    {
        var veiculo = new Veiculo { Marca = "VW", Modelo = "Gol", Placa = "ABC1D23" };

        Assert.Equal("VW Gol ABC1D23", veiculo.Descricao);
    }
}
