# Auditoria de pacotes NuGet

Este projeto utiliza o SDK do .NET 9, portanto é possível executar as verificações de atualização e de vulnerabilidades diretamente com a CLI do .NET.

## Comandos úteis

Execute-os na raiz da solução quando a CLI estiver disponível:

```bash
dotnet list package --include-transitive --outdated
dotnet list package --include-transitive --vulnerable
```

Os relatórios acima ajudam a identificar pacotes com versões desatualizadas ou com CVEs conhecidos. Após atualizar algum pacote, rode `dotnet restore` e os testes automatizados para validar o comportamento.

## Automatizando no CI

Para garantir que novas vulnerabilidades sejam detectadas rapidamente, configure o pipeline de CI para chamar os comandos anteriores e falhar caso qualquer vulnerabilidade de severidade média ou superior seja encontrada. Ferramentas como GitHub Dependabot ou Renovate também podem ser habilitadas para criar PRs automáticos com atualizações de pacotes.
