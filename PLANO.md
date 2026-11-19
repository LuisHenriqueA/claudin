# Claudin — Plano de implementação

> Instruções para o Claude Code: siga este plano **fase por fase**. Ao terminar cada fase, pare, resuma o que foi feito, o que foi verificado e o que mudou em relação ao plano, e espere aprovação antes de seguir. A **Fase 0 é investigação**: não escreva o app antes de concluí-la e registrar os achados em `docs/ACHADOS.md`.

## 1. Objetivo

App desktop para Windows que roda **duas instâncias do Claude Desktop oficial**, cada uma logada numa conta diferente, e permite alternar entre elas numa única janela.

- O Claude Desktop **não é modificado**: uma única instalação oficial, que continua com atualização automática.
- A isolação vem de **pastas de dados separadas** por perfil, e não de instalações separadas.
- O "embed" é visual: o Claudin é uma barra superior que mostra e esconde as janelas do Claude e as posiciona sob ela. Não usar `SetParent`.

## 2. Regras inegociáveis

1. Nunca modificar, copiar ou reempacotar arquivos da instalação do Claude.
2. Nunca ler, copiar, exportar ou registrar em log tokens, cookies ou credenciais das pastas de dados.
3. Antes de alterar qualquer chave de registro, salvar o valor original e oferecer restauração (botão nas configurações e na desinstalação).
4. Toda chamada Win32 deve tratar falha. Janelas e processos de terceiros podem sumir a qualquer momento.
5. Logs em `%LOCALAPPDATA%\Claudin\logs`, sem dados pessoais.

## 3. Stack

- **C# / .NET 10 (LTS), WPF**. Trocado do .NET 8, cujo suporte terminou em 10/11/2026.
- P/Invoke para Win32: `EnumWindows`, `GetWindowThreadProcessId`, `ShowWindow`, `SetWindowPos`, `SetForegroundWindow`, `IsWindow`, `GetDpiForWindow`
- Configuração em JSON: `%LOCALAPPDATA%\Claudin\config.json`
- Testes: xUnit para a lógica (config, roteamento de protocolo, resolução de caminhos); checklist manual para a parte de janelas

## 4. Estrutura proposta

```
Claudin/
  src/Claudin/
    App.xaml(.cs)
    MainWindow.xaml(.cs)          # barra superior + área "host"
    Core/
      Profile.cs                  # Id, Nome, Cor, PastaDados
      ConfigService.cs
      ClaudeInstallLocator.cs     # acha a instalação e o exe real
      InstanceManager.cs          # inicia, rastreia e reinicia instâncias
      WindowTracker.cs            # acha a janela principal de cada instância
      WindowHost.cs               # posiciona, mostra e esconde, sincroniza
      ProtocolRouter.cs           # handler claude://
      RegistryBackup.cs
    Interop/NativeMethods.cs
  tests/Claudin.Tests/
  docs/ACHADOS.md
  PLANO.md
```

Perfis padrão:

| Id | Nome | Pasta de dados |
|---|---|---|
| `x` | Conta X | `%APPDATA%\Claude` (pasta padrão do Claude, decisão da Fase 0) |
| `y` | Conta Y | `C:\Claudin\perfis\y` |

A raiz (`C:\Claudin\perfis`) deve ser configurável. Perfis sem pasta explícita usam `<raiz>\<id>`.

> **Revisado após a Fase 0** (ver `docs/ACHADOS.md`): o Claude instalado é a variante MSIX. A isolação usa `--user-data-dir`, o executável é o alias `claude-desktop.exe` e o login entre instâncias usa o código que o próprio Claude exibe. As fases abaixo já refletem isso.

---

## Fase 0 — Investigação (obrigatória)

Objetivo: confirmar as premissas na máquina real antes de codar. Registrar tudo em `docs/ACHADOS.md`.

1. **Instalação**
   - Localizar o Claude Desktop. O local esperado é `%LOCALAPPDATA%\AnthropicClaude\`.
   - Verificar se `claude.exe` na raiz é um stub que inicia `app-<versão>\claude.exe` e encerra. Anotar a árvore de processos (pai e filhos) logo após abrir.
   - Ver se existe alguma variante MSIX ou Store, que mudaria caminhos e comportamento.
2. **Pasta de dados padrão**
   - Confirmar onde ficam login e config, com expectativa de `%APPDATA%\Claude`.
   - Ver o que também é gravado em `%LOCALAPPDATA%`.
3. **Teste de isolação** (com o Claude padrão aberto e logado na conta X)
   - A: iniciar o exe com `APPDATA=C:\Claudin\teste\Roaming` sobrescrito. Verificar se abre uma segunda instância nova, deslogada, e se os arquivos foram gravados na pasta nova.
   - B: repetir sobrescrevendo também `LOCALAPPDATA`. Verificar se algo quebra (updater, cache).
   - C: testar `--user-data-dir=<pasta>` como alternativa.
   - Testar tanto pelo stub quanto pelo exe versionado.
   - Para cada variante: as duas instâncias rodam juntas? Os dados ficam realmente separados? Ao fechar e reabrir, o login persiste?
4. **Login**
   - Fazer login na instância de teste e observar se o fluxo usa o navegador com retorno via `claude://`.
   - Ler o handler atual em `HKCU\Software\Classes\claude\shell\open\command` (e em HKLM, se existir).
   - Verificar se iniciar o exe com o mesmo `APPDATA` e o link `claude://...` como argumento entrega o link para a instância já aberta, pelo mecanismo de segunda instância do Electron.
5. **Janelas**
   - Identificar qual processo é dono da janela principal, sua classe e seu título.
   - Testar `ShowWindow(SW_HIDE/SW_SHOW)` e `SetWindowPos` nela via um script PowerShell com P/Invoke e anotar os efeitos colaterais.
6. **Recursos globais**
   - Ícone de bandeja, atalho global e servidores MCP: anotar o que conflita com duas instâncias abertas.

**Critério de saída:** `ACHADOS.md` com a variante de isolação escolhida (A, B ou C), o executável a usar, a estratégia de login e a lista de conflitos. Se nenhuma variante isolar de verdade, **parar e reportar**.

## Fase 1 — Esqueleto e configuração

- Criar a solução WPF e o projeto de testes.
- `ConfigService`: carregar e salvar `config.json`, criar os perfis padrão e as pastas na primeira execução.
- `ClaudeInstallLocator`: encontrar o executável conforme a Fase 0, nesta ordem:
  1. o caminho sobrescrito na configuração;
  2. o alias `%LOCALAPPDATA%\Microsoft\WindowsApps\claude-desktop.exe`;
  3. o pacote MSIX `Claude_pzs8sxrjxfjjc` (`InstallLocation\app\Claude.exe`).
- Janela principal: barra superior (cerca de 36px) com os botões dos perfis, na cor de cada perfil, mais um botão de configurações. Área abaixo vazia.

**Pronto quando:** o app abre, cria config e pastas, e mostra o caminho do Claude detectado (ou um erro claro se não encontrar).

## Fase 2 — Gerenciamento de instâncias

- `InstanceManager.Start(profile)`: iniciar o Claude com `--user-data-dir="<pasta>"`, usando `UseShellExecute = false` e descartando stdout/stderr.
- Não há stub: o PID retornado é o MAIN. **Adotar** uma instância já aberta do perfil, já que o Windows pode abrir a padrão sozinho: o MAIN é o pai dos processos `--type=*` com o `--user-data-dir` do perfil.
- `WindowTracker`: encontrar a janela principal com `EnumWindows` filtrando pelos PIDs, com polling e timeout de cerca de 30s.
- Detectar se a instância foi fechada ou travou: mostrar "Conta X fechada — [Reabrir]" na área do host.
- Não iniciar uma segunda instância do mesmo perfil.

**Pronto quando:** clicar em Conta X e depois em Conta Y abre duas instâncias isoladas, e o app sabe o handle da janela de cada uma.

## Fase 3 — Host de janelas

- `WindowHost`: calcular o retângulo da área host em pixels físicos, considerando o DPI por monitor.
- Ao alternar de perfil: esconder a janela do perfil inativo, mostrar a do ativo, posicionar com `SetWindowPos` e dar foco.
- Fase 0 mostrou:
  - `SW_RESTORE` antes de `SetWindowPos`, porque com a janela maximizada ele é ignorado;
  - posicionar ainda oculta e só depois mostrar;
  - compensar as bordas invisíveis (`DWMWA_EXTENDED_FRAME_BOUNDS`);
  - mínimo do Claude ≈ 600x400 DIP;
  - a janela reaparece sozinha quando recebe um link e precisa ser re-escondida.
- Sincronizar quando o Claudin for movido, redimensionado, minimizado, restaurado ou maximizado, e quando mudar de monitor.
- Se o usuário mover a janela do Claude por conta própria, reposicioná-la (com debounce) ou soltá-la. Decidir após testar e registrar a decisão.
- Atalhos dentro do app: `Ctrl+Alt+1` e `Ctrl+Alt+2` alternam entre os perfis.
- Opcional, só se a Fase 0 mostrar que é seguro: esconder o botão das instâncias na barra de tarefas.

**Pronto quando:** alternar fica fluido, a janela sempre se encaixa sob a barra e nada fica "solto" ao mover ou redimensionar, inclusive com dois monitores de DPI diferente.

## Fase 4 — Assistente de login (revisada após a Fase 0)

Na variante MSIX, o Windows entrega todo `claude://` para a instância padrão (perfil X), e a chave `HKCU\Software\Classes\claude` é ignorada. O próprio Claude resolve o login entre instâncias: a instância padrão mostra um código, que é colado na instância que está logando. **Não alterar o registro do protocolo.**

- Marcar um perfil como "aguardando login", manualmente na barra ou ao detectar uma instância deslogada.
- Detectar quando a janela do perfil X aparece sozinha, o que indica que um link chegou. Mostrar o perfil X para o usuário ver o código e oferecer "Colar no perfil Y", que volta ao perfil que está logando.
- Instância única do próprio Claudin via named pipe: a segunda execução traz a primeira para frente e encerra.
- Implementado: botão de chave na barra, faixa com as etapas e retorno ao perfil de destino ao detectar uma cópia (só o aviso, sem ler o conteúdo). Detalhes em `docs/ACHADOS.md`, seção 5c.
- Alternativa futura, se o fluxo de código se mostrar ruim: registrar o Claudin como candidato a `claude://` e o usuário escolhê-lo em Aplicativos padrão.

**Pronto quando:** dá para logar a Conta X e a Conta Y, cada uma na sua instância, sem o login cair na conta errada.

## Fase 5 — Acabamento

- Ícone na bandeja: mostrar o Claudin, alternar perfil e sair (com a opção de fechar ou não as instâncias do Claude).
- Opção de iniciar com o Windows.
- Lembrar o último perfil ativo, além do tamanho e posição da janela.
- Tela de configurações: raiz das pastas, nome e cor dos perfis, caminho do Claude (sobrescrevível) e handler de protocolo.
- Logs com rotação.

Implementado (2026-11-10):
- **Bandeja** (WinForms `NotifyIcon`): mostrar o Claudin, alternar perfil e sair. "Sair" mantém o Claude aberto. "Sair e fechar as contas do Claude…" encerra os processos **à força** (o Claude não tem um jeito de sair pedido de fora) e sempre pede confirmação.
- **X da janela:** sai do Claudin, mantendo o Claude aberto, ou mantém na bandeja (opção "fechar para a bandeja").
- **Início com o Windows:** valor `Claudin` em `HKCU\...\Run`, com `--background` (inicia na bandeja). Antes da primeira alteração, o valor original é salvo em `registry-backup.json`. Para restaurar, há um botão nas configurações e o comando `Claudin.exe --restore-registry`, para o desinstalador.
- **Último perfil ativo**, mais posição e tamanho da janela (`GetWindowPlacement`/`SetWindowPlacement`; posição fora dos monitores atuais é descartada). Opção "abrir o perfil ativo ao iniciar", ligada por padrão.
- **Configurações:** nome e cor dos perfis, raiz das pastas, caminho do Claude (sobrescrevível), início com o Windows, abrir ao iniciar, fechar para a bandeja, restaurar o registro, abrir as pastas de logs e de configuração. O handler de protocolo virou só um texto explicativo, porque não há nada a configurar (Fase 0).
- **Logs:** um arquivo por dia, novo arquivo a cada 5 MB, retenção de 14 dias.
- **Ícone** gerado por `tools/make-icon.ps1`.

## Fase 7 — Levar conversa de chat para outra conta (adicionada em 2026-11-10)

- Sem RPA e sem cookies. O Claude da conta de origem escreve um documento de transferência, a pedido. O Claudin o entrega num chat novo da outra conta pelo link oficial `claude://claude.ai/new?q=`, e o usuário revisa e envia. Detalhes em `docs/ACHADOS.md`, seção 5d.
- Possível próximo passo: importar a exportação oficial de dados (zip) para levar a conversa palavra por palavra.

## Fase 6 — Empacotamento

- Publicação self-contained single-file (`win-x64`).
- Instalador simples (Inno Setup ou equivalente). A desinstalação pergunta se as pastas de perfil devem ser apagadas, **nunca** a pasta padrão do perfil X. Não há handler para restaurar.

Concluída (2026-11-11):
- Instalador per-user do Inno Setup: `tools\build-installer.ps1` gera `artifacts\installer\Claudin-Setup-<versão>.exe`.
- Instalar, atualizar (0.6.0 para 0.7.0) e desinstalar foram verificados, e a desinstalação manteve os dados e `%APPDATA%\Claude`.
- Pendência menor: a entrada de desinstalação ficou no registro e foi apagada à mão. A causa não foi confirmada (detalhes em `docs/ACHADOS.md`, seção 5e).

## Versão 0.8.0 (pedidos do usuário)

- Corrige o piscar do fundo do Claudin na troca de perfil: a janela nova entra por cima da anterior.
- Esconde os botões de janela do Claude, ficando só os do Claudin. A opção vem ligada nas Configurações.
- Só o botão do Claudin na barra de tarefas. A janela do Claude vira "de ferramenta" (WS_EX_TOOLWINDOW) enquanto está hospedada, e o estilo original volta ao soltar a janela. O DeleteTab do ITaskbarList não funcionou. A opção vem ligada nas Configurações. Falta o usuário confirmar o botão sumindo.
- Clique direito na aba → "Renomear…". Muda só o nome exibido, com a mesma validação das Configurações. O menu ainda não foi testado na tela: a máquina estava bloqueada e o menu do WPF não abre com clique simulado.
- Detalhes em `docs/ACHADOS.md`, seção 5f.

## Versão 0.9.0 (pedidos do usuário)

- **"Levar sessão do Code"** está no menu do botão de levar, ao lado de "Levar conversa do chat".
  - O pedido de resumo também pede a linha "Pasta do projeto:".
  - O Claudin abre `claude://code/new?q=…&folder=…` na outra conta: uma sessão nova do Code na mesma pasta, com o resumo preenchido e sem enviar.
  - Se a pasta não existir, a sessão abre sem pasta e o Claudin avisa.
  - Coberto por testes de unidade; falta o teste de ponta a ponta com uma sessão real.
- **"Copiar ambientes do Code"** foi investigado e descartado: o Claude apaga os ambientes gravados de fora (`docs/ACHADOS.md`, seção 5h).
- **Para levar todas as sessões do Code de uma vez**, use a função oficial do Claude: Ajuda → Solução de problemas → "Importar sessões da CLI do Claude Code…" (seção 5g).

## Versão 0.9.1

- **Corrige:** ao voltar ao Claudin pela barra de tarefas, a área ficava vazia (o Claude aberto, mas fora de vista).
  - Depois de o Claudin ser ativado ou restaurado, ele confere em 400 ms se o centro da área é do Claude (ou de uma janela do mesmo processo). Se não for, e o Claudin ou o Claude estiverem em primeiro plano, reposiciona o Claude por cima e devolve o foco (até 3 tentativas).
  - Fica um aviso no log com o que estava no lugar (a classe da janela, nunca o título), se o Claude estava visível ou minimizado, e se estava oculto pelo DWM.
  - A causa exata no computador do usuário não foi reproduzida: a ativação pela barra de tarefas não pode ser simulada daqui. Foi verificado forçando o Claude para baixo do Claudin no perfil de teste: o Claudin detectou e corrigiu.

## Versão 1.0.0

- **Moldura na cor da conta:** borda de 2 DIP na cor da conta ativa em volta do Claude, que ocupa só o miolo. Liga e desliga nas Configurações, e vem ligada. Verificado no perfil de teste: a borda fica na cor do perfil e o Claude fica 3 px para dentro (1 px de borda do Claudin + 2 px de moldura).
- **Avisos por conta: descartado por ora.**
  - O plano era captar o "pedido de atenção" (`FlashWindowEx`) do Claude pelo shell hook (`RegisterShellHookWindow`, `HSHELL_FLASH` 0x8006) e mostrar um selo na aba da conta.
  - Teste com uma conta de teste: a janela normal gerou `HSHELL_FLASH`. A mesma janela marcada como de ferramenta (sem botão na barra, como o Claudin deixa) ou oculta (como a conta inativa) **não gerou nada**.
  - Com "Mostrar só o Claudin na barra de tarefas" ligado, não há sinal para captar. As notificações do Windows continuam, mas as duas contas usam o mesmo AUMID, então não dá para saber de qual conta veio cada uma.

## Versão 1.1.0

- **Consumo das contas:** botão de gráfico na barra abre um painel com o uso da sessão (5 h) e da semana de cada conta, lado a lado. Também mostra os limites semanais por modelo (Sonnet, Opus), o do Cowork e o uso extra, quando o arquivo traz esses valores. A dica das abas ganhou o resumo do consumo.
  - **Fonte:** o próprio Claude Desktop grava `plan-usage-history.json` na pasta de dados de cada conta. Visto no `app.asar` 6.6395 (só leitura): `{version: 2, samples: [{t, org, u: {fh, sd, sn, so, cw, xu…}}]}`, 30 dias de amostras, no máximo uma a cada 4,5 min, só com a conta aberta. Só tem percentuais e o id da organização, que o Claudin ignora. Não tem token nem cookie, então a regra 2 vale.
  - **Sem horário de renovação:** o `resets_at` fica só na memória do Claude. O painel mostra há quanto tempo foi a leitura, marca como desatualizada a partir de 45 min, e a sessão como "renovada" quando a leitura tem mais de 5 h.
  - Formato interno e sem documentação: se mudar, o painel diz que não conseguiu ler, e o Claudin continua funcionando.
  - **Verificado** numa configuração de teste (`C:\Claudin\teste\consumo`) com cópias dos históricos reais das duas contas e uma leitura antiga fictícia. **Não verificado:** o painel por cima de uma janela do Claude hospedada. Ele é um popup e fica no topo, então deve aparecer.
- **Ao sair do Claudin, o Claude vai para a bandeja** (pedido do usuário). Antes, as janelas que o Claudin escondia voltavam a aparecer.
  - Agora o Claudin manda `WM_CLOSE` (`PostMessage`, sem esperar) a cada janela hospedada. É o mesmo que o X do Claude: a janela some e o processo continua aberto e logado (Fase 0, seção 5).
  - Encerrar os processos foi a outra opção. O usuário preferiu a bandeja, porque nada se perde e reabrir é imediato.
  - Opção `sendClaudeToTrayOnExit`, ligada por padrão, nas Configurações. Desligada, o comportamento antigo volta. A bandeja do Claudin diz o que o "Sair" faz.
  - **Verificado** com uma conta de teste: depois de fechar o Claudin, o processo continuou vivo e a janela ficou oculta. Ao reabrir o Claudin, ele pegou a mesma janela e a mostrou.
  - **Efeito colateral:** com a opção ligada, cada Claude fica com o ícone dele na bandeja do Windows.

---

## Checklist de testes manuais (rodar no fim das fases 2 a 5)

- [ ] As duas instâncias abertas ao mesmo tempo, cada uma com sua conta.
- [ ] Fechar e reabrir o Claudin mantém os dois logins.
- [ ] Projetos e conversas da conta X não aparecem na Y.
- [ ] `claude_desktop_config.json` (MCPs) é separado por perfil.
- [ ] Login via navegador cai no perfil certo.
- [ ] Mover, redimensionar, maximizar e minimizar mantém tudo encaixado.
- [ ] Funciona em dois monitores com DPI diferente.
- [ ] Atualização do Claude Desktop não quebra o launcher.
- [ ] Matar uma instância pelo Gerenciador de Tarefas mostra o estado "fechada" com a opção de reabrir.
- [x] Desinstalar não apaga `%APPDATA%\Claude`. Verificado em 2026-11-11.
- [ ] Ícones de bandeja e atalho global com duas instâncias (anotar qual instância atende).

## Riscos conhecidos

| Risco | Mitigação |
|---|---|
| Atualização do Claude muda estrutura, exe ou comportamento | Localização dinâmica do exe; Fase 0 documentada para refazer rápido |
| Variante de isolação deixar de funcionar | Manter as três variantes testadas em `ACHADOS.md`; caminho do exe e argumentos configuráveis |
| Atalho global ou bandeja duplicados | Aceitar; documentar qual instância fica com o atalho |
| Ponte de acesso ao computador ou recursos locais conflitarem | Testar na Fase 0; se conflitar, usar esses recursos em um perfil só |
| Sem suporte oficial da Anthropic | Projeto pessoal; nenhum binário alterado |

## Fora de escopo

- Modificar ou injetar código no Claude Desktop.
- Mais de dois perfis (a estrutura já permite, mas não é requisito agora).
- Claude Code: continua centralizado em uma única conta, fora do Claudin.
