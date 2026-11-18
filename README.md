# Claudin

Use duas contas do **Claude Desktop** no Windows, lado a lado, numa única janela — por exemplo, a conta pessoal e a da empresa — e alterne entre elas com um clique ou um atalho.

> O Claudin é um projeto independente e **não tem ligação com a Anthropic**. Ele não modifica o Claude Desktop: só organiza as janelas de duas instâncias do Claude que você mesmo usa.

## Como funciona

- Cada conta roda numa instância própria do Claude Desktop, com **pasta de dados separada** (`--user-data-dir`). Por isso cada uma mantém seu login, suas conversas, seus MCPs e suas configurações.
- O Claudin mostra a janela do Claude da conta ativa encaixada dentro da dele e esconde a da outra conta. A troca é feita pelas abas no topo.
- Nenhum arquivo da instalação do Claude é alterado, e o Claudin **nunca lê, copia ou registra** tokens, cookies ou senhas das pastas do Claude.

## Requisitos

- Windows 10 ou 11 (64 bits).
- Claude Desktop instalado (pelo site da Anthropic ou pela Microsoft Store).

## Instalação

1. Baixe o `Claudin-Setup-<versão>.exe` da última versão em **Releases**.
2. Abra o instalador. Ele instala só para o seu usuário, sem precisar de administrador.
3. Se o Windows SmartScreen mostrar "O Windows protegeu o computador", é porque o instalador ainda não tem assinatura digital. Clique em **Mais informações → Executar assim mesmo**. Se quiser, antes confira o arquivo pelo `SHA256SUMS.txt` da release (veja o [SECURITY.md](SECURITY.md)).

Também dá para [compilar a partir do código](#compilar-a-partir-do-código).

## Primeira configuração

### 1. As duas contas

Na primeira vez, o Claudin cria dois perfis:

| Perfil | Pasta de dados | Observação |
|---|---|---|
| **Conta X** | a pasta padrão do Claude (`%APPDATA%\Claude`) | É o Claude que você já usa, com o login que já existe. Os links `claude://` (login, convites) sempre abrem nesta conta. |
| **Conta Y** | `C:\Claudin\perfis\y` | Uma instância nova, ainda sem login. |

### 2. Entrar na segunda conta

1. Clique na aba **Conta Y**. O Claude abre na tela de login.
2. Clique no botão de **chave** (Assistente de login) na barra do Claudin e siga a faixa amarela.
3. Entre com o e-mail da outra conta e abra o link que chegar por e-mail.
4. Como o Windows entrega os links `claude://` à Conta X, é ela que mostra um **código de login**. O Claudin troca para ela sozinho. Copie o código.
5. O Claudin volta para a Conta Y. Cole o código (Ctrl+V) para concluir.

Depois disso, as duas contas ficam logadas de vez, também depois de reiniciar.

### 3. Dar nome e cor às abas

- Clique com o **botão direito** numa aba → **Renomear…** (por exemplo, "Pessoal" e "Corporativa").
- Ou use a **engrenagem** (Configurações) → **Perfis**, onde também dá para escolher a **cor** de cada conta. A cor aparece na aba e na moldura em volta do Claude, para você sempre saber em qual conta está.

## Uso no dia a dia

- **Trocar de conta:** clique na aba, ou use **Ctrl+Alt+1**, **Ctrl+Alt+2**…
- **Bandeja:** o ícone do Claudin na bandeja mostra as contas e permite sair. "Sair" deixa as contas do Claude abertas; "Sair e fechar as contas do Claude…" fecha tudo.
- **Levar uma conversa para a outra conta** (botão à esquerda da chave):
  - **Levar conversa do chat:** o Claudin copia um pedido de resumo. Cole na conversa, envie, e clique em **Copiar** na resposta. O Claudin abre um chat novo na outra conta, já com o contexto (nada é enviado sozinho).
  - **Levar sessão do Code:** o mesmo, para sessões da aba Code. A sessão nova abre na **mesma pasta do projeto**.
- **Levar todas as sessões do Code de uma vez:** use a função do próprio Claude, na conta de destino: **Ajuda → Solução de problemas → Importar sessões da CLI do Claude Code…**. As conversas do Claude Code ficam numa pasta comum do seu usuário do Windows, e esse comando as adiciona à lista da outra conta.

## Configurações

Na **engrenagem** do Claudin:

| Opção | O que faz |
|---|---|
| Perfis: nome e cor | Como cada conta aparece nas abas e na moldura. |
| Pastas dos perfis | Raiz das pastas das contas sem pasta própria (padrão `C:\Claudin\perfis`). Vale depois de reiniciar. |
| Claude Desktop | Caminho do Claude. Vazio = detecção automática. |
| Iniciar com o Windows | Abre o Claudin em segundo plano, na bandeja. É a única alteração no registro, e o valor original é salvo antes (há um botão para restaurar). |
| Abrir o perfil ativo quando o Claudin iniciar | Já abre a última conta usada. |
| O X da janela mantém o Claudin na bandeja | Fechar esconde, em vez de sair. |
| Esconder os botões de minimizar, maximizar e fechar do Claude | Ficam só os botões do Claudin. |
| Moldura na cor da conta ativa | Borda fina na cor da conta em volta do Claude. |
| Mostrar só o Claudin na barra de tarefas | Tira o botão do Claude da barra e do Alt+Tab. Com isso, o contador e o piscar do Claude na barra deixam de aparecer (as notificações do Windows continuam). |

A configuração fica em `%LOCALAPPDATA%\Claudin\config.json`, e os logs em `%LOCALAPPDATA%\Claudin\logs` (sem dados pessoais, guardados por 14 dias).

## Desinstalar

Use **Configurações do Windows → Aplicativos**. O desinstalador pergunta se você quer apagar também as pastas das contas criadas pelo Claudin. A pasta padrão do Claude (`%APPDATA%\Claude`) **nunca** é apagada.

## Problemas conhecidos

- **Atualizações do Claude Desktop:** o Claudin depende de como a janela do Claude se comporta. Se algo parar de funcionar depois de uma atualização do Claude, desligue a opção correspondente nas Configurações e abra uma issue com o log.
- **Antivírus corporativo:** antivírus com detecção heurística podem reclamar do desinstalador do Inno Setup se ele for aberto por um script. Aberto pelo Windows, normalmente não há problema.

## Compilar a partir do código

Requisitos: [.NET 10 SDK](https://dotnet.microsoft.com/) e, para o instalador, [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
dotnet test tests\Claudin.Tests          # testes
.\tools\publish.ps1                       # gera artifacts\publish\Claudin.exe (arquivo único)
.\tools\build-installer.ps1               # gera artifacts\installer\Claudin-Setup-<versão>.exe
```

Para testar sem mexer nas suas contas, aponte o Claudin para uma pasta de teste:

```powershell
$env:CLAUDIN_HOME = 'C:\Claudin\teste\home'; .\src\Claudin\bin\Debug\net10.0-windows\Claudin.exe
```

## Estrutura do repositório

| Pasta | Conteúdo |
|---|---|
| `src/Claudin` | O aplicativo (WPF, .NET 10). `Core` tem a lógica testável; `Hosting`, o encaixe das janelas; `Interop`, as chamadas ao Windows. |
| `tests/Claudin.Tests` | Testes automáticos (xUnit). |
| `installer` | Script do Inno Setup. |
| `tools` | Scripts de publicação, do instalador, do ícone e de investigação. |
| `PLANO.md` | Plano do projeto, por fases, e o histórico das versões. |

## Contribuir e segurança

- Contribuições entram por **Pull Request** e só são incorporadas depois de revisadas pelo mantenedor.
- Para relatar uma falha de segurança, siga o [SECURITY.md](SECURITY.md). Não abra uma issue pública.
- Cada release traz o `SHA256SUMS.txt`, para você conferir o instalador baixado.

## Licença

[MIT](LICENSE). O software é fornecido "como está", sem garantias.
