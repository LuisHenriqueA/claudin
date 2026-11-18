# Política de segurança

## Versões com suporte

Só a versão mais recente publicada em **Releases** recebe correções de segurança.

## Como relatar uma vulnerabilidade

**Não abra uma issue pública.** Use o relato privado do GitHub: na aba **Security** deste repositório, clique em **Report a vulnerability**. Só o mantenedor vê o relato.

Inclua, se possível:
- a versão do Claudin e do Windows;
- os passos para reproduzir;
- o impacto que você observou.

Você recebe uma resposta assim que possível. A correção é publicada numa nova versão, com crédito a quem relatou, se você quiser.

## O que está no escopo

- O Claudin ler, gravar ou registrar em log dados que não deveria (tokens, cookies, credenciais ou conteúdo das conversas).
- O instalador ou o desinstalador apagar ou alterar algo fora do que está documentado no README.
- Qualquer forma de executar código ou elevar privilégios por meio do Claudin.

O Claude Desktop em si é da Anthropic. Falhas nele devem ser relatadas diretamente à Anthropic.

## Conferir o instalador

Cada release publica o arquivo `SHA256SUMS.txt`. Para conferir o instalador baixado:

```powershell
Get-FileHash .\Claudin-Setup-<versão>.exe -Algorithm SHA256
```

O valor tem de ser igual ao do `SHA256SUMS.txt`. Se for diferente, não execute o arquivo.
