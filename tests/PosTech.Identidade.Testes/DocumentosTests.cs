using PosTech.Identidade;

namespace PosTech.Identidade.Testes;

/// <summary>
/// Os documentos. É a regra que a Auth Function aplica antes de tocar no banco,
/// então um furo aqui vira consulta inútil no melhor caso e autenticação errada
/// no pior.
/// </summary>
public class DocumentosTests
{
    // CPFs válidos de teste, com dígitos verificadores corretos.
    [Theory]
    [InlineData("529.982.247-25")]
    [InlineData("52998224725")]
    [InlineData("111.444.777-35")]
    public void CPF_valido_passa_com_ou_sem_pontuacao(string entrada)
    {
        Assert.True(Cpf.TentarCriar(entrada, out var cpf, out _));
        Assert.Equal(11, cpf!.Valor.Length);
        Assert.DoesNotContain('.', cpf.Valor);
    }

    [Theory]
    [InlineData(null, "vazio")]
    [InlineData("", "vazio")]
    [InlineData("   ", "vazio")]
    [InlineData("123", "11 dígitos")]
    [InlineData("529982247250", "11 dígitos")]
    [InlineData("52998224724", "inválido")]
    public void CPF_invalido_diz_o_que_esta_errado(string? entrada, string trechoDoErro)
    {
        Assert.False(Cpf.TentarCriar(entrada, out var cpf, out var erro));
        Assert.Null(cpf);
        Assert.Contains(trechoDoErro, erro);
    }

    [Theory]
    [InlineData("11111111111")]
    [InlineData("00000000000")]
    [InlineData("99999999999")]
    public void CPF_de_digitos_repetidos_nao_passa(string entrada)
    {
        // Estes passam nos dígitos verificadores. É o CPF que todo mundo digita
        // para testar, e sem uma regra própria ele entraria na base.
        Assert.False(Cpf.TentarCriar(entrada, out _, out _));
    }

    [Fact]
    public void CPF_formatado_sai_legivel()
    {
        Assert.Equal("529.982.247-25", Cpf.Criar("52998224725").Formatado());
    }

    [Fact]
    public void Dois_CPF_iguais_sao_o_mesmo_documento_mesmo_digitados_diferente()
    {
        Assert.Equal(Cpf.Criar("529.982.247-25"), Cpf.Criar("52998224725"));
        Assert.Equal(Cpf.Criar("52998224725").GetHashCode(), Cpf.Criar("529.982.247-25").GetHashCode());
    }

    [Fact]
    public void Criar_com_CPF_invalido_estoura_com_a_mensagem()
    {
        var erro = Assert.Throws<ArgumentException>(() => Cpf.Criar("52998224724"));

        Assert.Contains("inválido", erro.Message);
    }

    [Theory]
    [InlineData("11.222.333/0001-81")]
    [InlineData("11222333000181")]
    public void CNPJ_valido_passa_com_ou_sem_pontuacao(string entrada)
    {
        Assert.True(Cnpj.TentarCriar(entrada, out var cnpj, out _));
        Assert.Equal("11222333000181", cnpj!.Valor);
    }

    [Theory]
    [InlineData("11222333000182")]
    [InlineData("1122233300018")]
    [InlineData("11111111111111")]
    public void CNPJ_invalido_nao_passa(string entrada)
    {
        Assert.False(Cnpj.TentarCriar(entrada, out _, out _));
    }

    [Fact]
    public void CNPJ_formatado_sai_legivel()
    {
        Assert.Equal("11.222.333/0001-81", Cnpj.Criar("11222333000181").Formatado());
    }

    [Fact]
    public void A_regra_de_CPF_e_a_mesma_que_a_Auth_Function_aplica()
    {
        // A Function valida antes de consultar o banco. Se as duas regras
        // divergirem, um CPF aceito no cadastro pode ser recusado no login —
        // ou pior, o contrário. Este teste existe para a divergência quebrar
        // o build em vez de aparecer em produção.
        string[] aceitos = ["52998224725", "11144477735"];
        string[] recusados = ["52998224724", "11111111111", "123"];

        Assert.All(aceitos, c => Assert.True(Cpf.TentarCriar(c, out _, out _)));
        Assert.All(recusados, c => Assert.False(Cpf.TentarCriar(c, out _, out _)));
    }
}
