# Leve 1.0 — Auditoria e plano

Data: 18/11/2026

## 1. Auditoria do Leve 0.7 (versão Codex)

O que está no Drive é o `Leve-0.7-gaming-evidencia.zip`, que traz só `Leve.exe`, README, VALIDACAO e build.json. Não tem código-fonte, e o trabalho da v0.8 descrito no PRD (WAL, worker, IPC) também não está lá. A auditoria foi feita sobre o binário (strings e recursos) e a documentação.

### O que a v0.7 entrega de fato

| Área | Realidade |
|---|---|
| Mutações reais | Uma dúzia: Edge e Chrome em 2º plano (StartupBoost e BackgroundMode), Widgets (`Dsh\AllowNewsAndInterests`), `AppPrivacy\LetAppsRunInBackground`, `DeliveryOptimization\DODownloadMode`, plano de energia clonado com USB e PCIe, limpeza de temporários |
| Diagnóstico | Inventário de inicialização, hardware, VBS/HVCI, MMCSS e captura WPR. Tudo somente leitura |
| "Laboratório" | MMCSS, Win32PrioritySeparation, svchost e HAGS aparecem como guias de texto, não como ajustes aplicáveis |
| Leituras | Feitas com **scripts PowerShell embutidos** (cerca de 100 trechos `$...` no exe). Cada consulta abre um `powershell.exe`, o que custa 300 a 800 ms e cerca de 60 MB por chamada. Isso é o oposto de "leve" |
| Textos da UI | Cheios de ressalvas ("isso não prova…", "não interprete…"). O usuário lê avisos, não resultados |

### Diagnóstico

O Codex otimizou para não errar, não para performar. Gastou a maior parte do esforço em infraestrutura de prova (WPR, WAL, nonce, hash de catálogo, pipe efêmera) e quase nada em ajustes que tiram processos do caminho. O resultado é um app seguro que não deixa o Windows mais leve.

### O que vale aproveitar da v0.7

- Detecção de Windows gerenciado ou Home antes de aplicar políticas.
- Plano de energia **clonado**, sem editar o plano do usuário.
- Leitura de `StartupApproved` com estado "desconhecido" honesto.
- A ideia de "valor alterado externamente: preservar, não sobrescrever".

## 2. O que aprender com o optimizerDuck

Ele é GPL-3.0. Podemos usar as **ideias e as chaves de Registro**, que são fatos do Windows, mas não copiar código, a menos que o Leve também vire GPL.

### Copiar o conceito

1. **As primitivas capturam o estado original sozinhas.** Os serviços de Registro, Serviço, Tarefa e Energia gravam o valor anterior e montam o passo de reversão, então o autor de um tweak não consegue esquecer o backup.
2. Distinção entre **"não existia"** (apagar ao reverter) e **"existia"** (restaurar valor e tipo), mais a limpeza das subchaves criadas.
3. Leitura de confirmação após cada reversão.
4. Serviços tratados via `ChangeServiceConfig` e `ChangeServiceConfig2` (preserva o "Automático atrasado"). **Só o tipo de inicialização muda**, sem parar o serviço.
5. APIs nativas no lugar de processos filhos: powrprof para o plano de energia, `CallNtPowerInformation` para hibernação, TaskScheduler para tarefas.
6. Gravação atômica do backup (temp seguido de replace), reversão LIFO e passo "corrompido" que falha de forma explícita.

### Evitar

- **240 serviços em massa**, no estilo winutil. Esse é o único item que eles próprios marcam como Risky.
- **Plano de energia sem idle states**: o Gerenciador de Tarefas mostra 100% de CPU e a temperatura sobe. Está marcado como "Safe".
- `Win32PrioritySeparation=38`, `NetworkThrottlingIndex`, `SystemResponsiveness`, `NoLazyMode` e `AlwaysOn`: placebo ou redefinição do padrão.
- Tweaks de GPU no Registro do driver, com um **bug de índice**: `VideoController N` vira a subchave `000(N-1)`, que pode apontar para a GPU errada.
- Limpeza de **Prefetch** (deixa o próximo boot mais lento).
- Apagar `Windows.old` sem aviso de que é irreversível.
- Remoção de apps Xbox (quebra Game Pass e Game Bar).
- Backup **por tweak**. Quando dois tweaks escrevem a mesma chave (e eles têm vários casos assim), a reversão fora de ordem restaura o valor errado.

## 3. Sobre "memória limpa" e "registro limpo"

- **Limpar RAM ou Standby List é placebo, e às vezes piora.** O Windows usa a RAM livre como cache. Esvaziar o cache força leitura do disco depois, o que gera **mais** stutter. O ganho real de memória vem de **ter menos processos residentes**, e é isso que o debloat, a inicialização e os serviços fazem. O Leve vai **mostrar** essa redução: RAM comprometida e número de processos, antes e depois.
- **Limpador de Registro não dá desempenho nenhum.** Chaves órfãs não são carregadas por ninguém. Fica fora do produto.

## 4. Perfis e "Aplicar tudo"

| Perfil | Comportamento |
|---|---|
| **Game Ultra** | Tudo pré-marcado (cerca de 60 itens). Um clique em **Aplicar tudo** e uma tela de confirmação com o resumo. |
| **Gamer + Dev** | Igual ao Ultra, mas **mantém** WSL2, Hyper-V, Docker, indexador de busca, ssh-agent, OneDrive e Spooler. |
| **Manual** | Lista completa, um a um. Filtro por texto e por risco. |

Regras do "Aplicar tudo":

- **Um snapshot por lote.** Antes da primeira escrita, o snapshot grava o valor original de cada chave, serviço ou tarefa **uma única vez**, mesmo que vários itens toquem nela. Isso resolve o bug de sobreposição do optimizerDuck.
- **Desfazer tudo** reverte o lote inteiro em LIFO. Também dá para desfazer um item isolado.
- Itens sem efeito no hardware atual ficam **ocultos**, não bloqueados. Exemplos: Bluetooth sem adaptador, impressora inexistente, tweak de laptop num desktop.
- **Única exceção ao "tudo marcado"**: desligar VBS/HVCI (Memory Integrity). Aparece na confirmação como caixa separada, desmarcada por padrão. Dá de 5 a 10% de FPS em alguns jogos, mas tira uma proteção real.

## 5. Catálogo v1 (proposta)

Legenda: U = Game Ultra, D = Gamer + Dev, R = risco (B baixo, M médio, A alto).

### 5.1 Debloat de apps (AppX)
| Item | U | D | R |
|---|---|---|---|
| Remover: Bing News, Weather, Finance e Search; Clipchamp; Solitaire; Tips; Get Help; Feedback Hub; People; Maps; Teams pessoal; Outlook (new); Copilot app; Dev Home; PowerAutomate; apps OEM de propaganda | ✓ | ✓ | B |
| **Nunca remover**: Store, App Installer (winget), Xbox App, Xbox Identity, Gaming Services, Game Bar, Terminal, Calculadora, Fotos, codecs HEVC/AV1/VP9 | — | — | — |

Os apps removidos são reinstaláveis pela Store ou pelo winget. O Leve guarda a lista e oferece "reinstalar".

### 5.2 Processos de fundo, telemetria e IA
| Item | U | D | R |
|---|---|---|---|
| Telemetria: serviços DiagTrack e dmwappushservice desativados; políticas `AllowTelemetry=0`; tarefas CEIP e Compatibility Appraiser desativadas | ✓ | ✓ | B |
| Windows Error Reporting (serviço WerSvc em Manual e tarefa QueueReporting) | ✓ | ✓ | B |
| Copilot, Recall e Click to Do (políticas WindowsAI e WindowsCopilot) | ✓ | ✓ | B |
| Widgets, Notícias e Interesses | ✓ | ✓ | B |
| Apps UWP em 2º plano (`GlobalUserDisabled=1`) | ✓ | ✓ | M |
| Edge e Chrome: Startup Boost e "continuar em 2º plano" | ✓ | ✓ | B |
| Delivery Optimization P2P desligado | ✓ | ✓ | B |
| Sugestões, anúncios, ContentDeliveryManager e apps instalados silenciosamente | ✓ | ✓ | B |
| Histórico de atividades, busca web no Iniciar e Bing | ✓ | ✓ | B |
| Windows Search indexer (WSearch) em Manual | ✓ | — | M |
| SysMain | mantém | mantém | — |

### 5.3 Serviços (lista curta e condicionada ao hardware)
| Serviço | U | D | Condição |
|---|---|---|---|
| Fax, RetailDemo, RemoteRegistry, MapsBroker, lfsvc (localização), WalletService, PhoneSvc, wisvc (Insider), AJRouter, WpcMonSvc (controle dos pais) | Desativado ou Manual | igual | — |
| Spooler (impressão) | Manual | mantém | nenhuma impressora física |
| bthserv, BTAGService | Manual | igual | sem adaptador Bluetooth |
| TabletInputService | Manual | igual | sem tela de toque |
| ssh-agent | Manual | mantém | — |
| Xbox (XblAuthManager, XblGameSave, XboxGipSvc, XboxNetApiSvc) | **mantém** | mantém | Game Pass e controle |

São mais ou menos 15 serviços, não 240.

### 5.4 Inicialização (Run, StartupApproved, tarefas)
| Item | U | D | R |
|---|---|---|---|
| Desligar o autostart de launchers e apps: Steam, Epic, EA, Ubisoft, Battle.net, Riot (sem o Vanguard), Discord, Spotify, OneDrive, Teams, Edge e afins | ✓ | ✓ (menos OneDrive) | B |
| **Mantidos sempre**: driver de áudio, painel de GPU, antivírus, anti-cheat (Vanguard, EAC, BattlEye), software de periféricos com perfil on-board opcional | — | — | — |
| Tarefas de updater de terceiros (Google, Adobe, Opera, etc.) em modo "só sob demanda" | ✓ | ✓ | B |

A reversão usa `StartupApproved`, igual ao Gerenciador de Tarefas. Nada é apagado.

### 5.5 Gaming
| Item | U | D | R |
|---|---|---|---|
| Game Mode ligado | ✓ | ✓ | B |
| Game DVR e gravação em segundo plano desligados (o app Xbox continua) | ✓ | ✓ | B |
| HAGS ligado (se a GPU e o driver suportarem; exige reinício) | ✓ | ✓ | B |
| Otimizações para jogos em janela (`SwapEffectUpgradeEnable=1`) | ✓ | ✓ | B |
| Aceleração do mouse desligada | ✓ | ✓ | B |
| VRR ligado (se o monitor suportar) | ✓ | ✓ | B |

### 5.6 Energia
| Item | U | D | R |
|---|---|---|---|
| Plano "Leve Ultimate" clonado do Ultimate Performance, **mantendo os C-states** | ✓ | ✓ | B |
| No plano: USB Selective Suspend desligado, PCIe ASPM desligado, core parking desligado e CPU mínima a 100% (só em AC) | ✓ | ✓ | B |
| Hibernação e Inicialização Rápida desligadas (libera GBs; evita drivers em "meio-boot") | ✓ | ✓ | B |
| Power Throttling global desligado | ✓ (só desktop) | ✓ (só desktop) | M |

### 5.7 Visual
| Item | U | D | R |
|---|---|---|---|
| Animações e transparência desligadas; `MenuShowDelay=0`; atraso de startup do Explorer zerado | ✓ | — | B |

### 5.8 Limpeza de disco (não RAM)
| Item | U | D |
|---|---|---|
| %TEMP%, Windows\Temp, cache do Windows Update, Lixeira, crash dumps, Delivery Optimization cache | ✓ | ✓ |
| **Nunca**: Prefetch, cache de shaders DirectX, NVIDIA ou AMD (apagar causa stutter de recompilação) | — | — |

### 5.9 Segurança (opt-in explícito)
| Item | Padrão |
|---|---|
| Memory Integrity (HVCI) desligado | desmarcado, pede confirmação e exige reinício. É compatível com WSL2, porque desliga só o HVCI e não o hypervisor |

### Fora do produto
RAM cleaner, limpeza de Standby List, limpador de Registro, Timer Resolution global, `Win32PrioritySeparation`, `NetworkThrottlingIndex`, Nagle e TCP, desativar Defender, desativar Windows Update, tweaks no Registro do driver de GPU, bcdedit.

## 6. Arquitetura proposta

```
Leve.exe  (.NET 10, WPF, single-file, framework-dependent)
├── Leve.Core      catálogo, perfis, snapshot de lote, motor apply/revert
├── Leve.Windows   primitivas: Registry, Service, ScheduledTask, StartupApproved,
│                  AppX, PowerPlan (powrprof + powercfg), Hibernation, limpeza de disco
└── Leve.App       UI terminal (WPF puro, sem WPF-UI)
```

Decisões, contra o PRD do Codex:

- **App inteiro elevado** (`requireAdministrator`), com uma trava: se a conta elevada não for o usuário logado, os itens HKCU são bloqueados. O modelo UI não elevada + worker + pipe + nonce + WAL foi o que consumiu a v0.8 inteira sem entregar ajustes.
- **Leituras sem PowerShell.** Todo estado é lido por Win32 e Registro. Duas escritas raras usam ferramenta do Windows:
  - remover ou re-registrar apps AppX usa `powershell` (a API WinRT somaria cerca de 25 MB ao exe);
  - criar o plano de energia usa `powercfg`.
- **O catálogo é dado**: records C# com `Id`, `Risco`, `Perfis`, `Condição` e `Ações[]`. Cada ação é uma primitiva que captura o estado anterior por conta própria.
- Snapshot em `%LOCALAPPDATA%\Leve\snapshots\NNN.json`, com gravação atômica.
- **Métrica simples de antes e depois**: número de processos, RAM comprometida, serviços em execução e itens de startup. Sem WPR nem PresentMon no 1.0.

## 7. UI terminal (v2 do visual)

- Uma janela, fonte Cascadia Mono (já vem no Win11), fundo `#0d1110` e acento verde-fósforo.
- Barra de status ao vivo: CPU, RAM e número de processos, e o alvo depois do perfil.
- Módulos à esquerda: scan, debloat, serviços, startup, gaming, energia, privacidade, limpeza, snapshots.
- Teclado em primeiro lugar (`espaço`, `enter`, `/`, `u`, `p`), mas tudo clicável.
- Log de execução embaixo, linha a linha: `✓ DiagTrack → Disabled (era Automatic)`.
- Textos curtos. Avisos só onde há risco real.

## 8. Ordem de entrega

| Marco | Conteúdo | Estado |
|---|---|---|
| **M1** | Core, primitivas de Registro, serviço e tarefa, snapshot por lote, 35 ajustes, perfis, **Aplicar tudo**, UI terminal | ✅ compilado, 26 testes passando (18/11) |
| **M2** | Apps (AppX) reversíveis, inicialização dinâmica (launchers, chat, navegadores), updaters de terceiros, plano Leve Ultimate, hibernação, **serviços parados na hora**, limpeza de disco | ✅ compilado, 52 testes passando (18/11) |
| **M3** | HVCI opt-in, exclusão do Defender para pastas de jogos (opt-in), atalho para o Dev Drive, relatório de antes e depois | ✅ compilado, 85 testes passando (18/11) |
| **v1.1** | **Modo Jogo dinâmico**: agente que rebaixa Discord, navegador, sync e indexador enquanto o jogo roda. Detector de jogos com GPU dedicada por jogo. Rede sem economia de energia. MPO em teste A/B. PresentMon para frametime e 1% low | — |

## 9. Itens acrescentados após o M1

### Maior impacto
- **Modo Jogo dinâmico** (v1.1): resolve o stutter causado por outro programa disputando CPU e disco durante a partida.
- **Exclusão do Defender nas bibliotecas de jogos** (M3, opt-in): reduz carregamento e engasgos de streaming e de shader.
- **Dev Drive** (M3, só Dev): ReFS com o Defender em modo desempenho para repositórios.
- **Parar serviços na hora** (M2): o ganho de RAM aparece sem reiniciar.

### Impacto médio
- **GPU dedicada por jogo** em notebooks híbridos (v1.1).
- **Economia de energia da placa de rede** (v1.1).
- **MPO desligado como teste A/B** para setups NVIDIA com vários monitores (v1.1).
- **Updaters de terceiros sob demanda** (M2).
- **Phone Link desligado** (M2).

### Recusados, com motivo
| Item | Motivo |
|---|---|
| Limpar RAM ou Standby List | Joga fora o cache e piora o stutter. |
| Limpador de Registro | Nenhum ganho. |
| Desligar as mitigações Spectre/Meltdown | Ganho pequeno, risco alto. |
| `bcdedit` (tick, HPET) | Resultado inconsistente entre placas. |
| Timer Resolution global, `Win32PrioritySeparation`, `NetworkThrottlingIndex`, Nagle | Placebo. |
| Pagefile ou compressão de memória desligados | Risco de crash, sem ganho. |
| MSI Mode em massa, afinidade fixa | Podem travar dispositivos. Se entrarem, só no laboratório. |

## 10. Decisões do M2

- **Apps**: o Leve remove só para o usuário atual (`Remove-AppxPackage`). Para desfazer, re-registra com `Add-AppxPackage -RegisterByFamilyName`, sem internet, enquanto o pacote ainda existir no PC. Se o pacote já tiver sumido, o log manda reinstalar pela Store.
  - Nunca são tocados: Store, App Installer, Xbox, Gaming Services, Terminal, Calculadora, Fotos, Media Player e codecs.
- **Inicialização**: o catálogo é montado na hora a partir do que existe no PC (Run, Run32 e pastas de Inicialização). O Leve desliga as entradas por `StartupApproved`, igual ao Gerenciador de Tarefas, e nada é apagado.
  - **Protegidos, nem aparecem**: antivírus, anti-cheat, drivers de áudio, GPU e touchpad, e software de periféricos.
  - **Launchers, chat e navegadores**: marcados no Ultra.
  - **Ferramentas dev** (Docker, JetBrains, Slack): só no Ultra.
  - **Sincronização de nuvem** (Google Drive, OneDrive, Dropbox): nunca vem marcada, porque parar a sincronização pode atrapalhar quem trabalha com arquivos na nuvem.
  - **Desconhecidos**: aparecem para marcar à mão, com risco médio.
- **Energia**: plano "Leve Ultimate" com GUID fixo, clonado do Ultimate Performance (ou do Alto desempenho). Só os valores de tomada mudam; a bateria segue o padrão. Desfazer reativa o plano anterior.
- **Hibernação e Inicialização Rápida**: desligadas só em desktop.
- **Limpeza de disco**: tela própria, fora do snapshot, porque apagar arquivo não tem volta. Nunca apaga Prefetch nem cache de shaders.

## 11. v1.1: o Leve em movimento

Até o 1.0, o Leve configura o Windows uma vez e sai. A v1.1 age **durante a partida** e **mede o FPS**.

| # | Peça | O que faz | Estado |
|---|---|---|---|
| 1 | **Modo Jogo dinâmico** (`Leve.Agent.exe`) | Detecta o jogo em primeiro plano. Rebaixa apps pesados (abaixo do normal + EcoQoS), deixa comunicação e música intocadas e sobe o jogo para "acima do normal". Restaura tudo quando o jogo fecha. Usa um diário para se recuperar de travamentos. | escrito (18/11), falta compilar |
| 2 | **GPU dedicada por jogo** | Grava "alto desempenho" por executável (`UserGpuPreferences`) para os jogos encontrados. Só aparece em PC com duas GPUs. | escrito, falta compilar |
| 3 | **PresentMon** | FPS médio, 1% low e temperatura máxima de GPU e CPU. Teste A/B com veredito (melhorou, piorou ou inconclusivo) que considera o ruído medido. Tela mínima. | escrito, falta compilar |
| 4 | **Placa de rede sem economia** | EEE, Green Ethernet e desligamento pelo Windows, por placa. Só mexe nas chaves que o driver realmente expõe. Só no Ultra. | escrito, falta compilar |
| 5 | **MPO em teste A/B** | Ajuste manual, fora de qualquer perfil. Meça na tela `> fps` e mantenha só se melhorar. | escrito, falta compilar |

### Interface de medição: mínima, por requisito
- Nada roda **sobre** o jogo: sem overlay, sem janela pulando e sem som. Overlay custa FPS e pode chamar a atenção de anti-cheat.
- A tela `> fps` tem:
  - o campo do jogo (já vem preenchido pelo agente) e a duração;
  - os botões **A** (sem otimização) e **B** (com otimização);
  - **uma linha por captura** e **uma linha de veredito**.
- A captura começa 5 s depois do clique, para dar tempo de voltar ao jogo, e o resultado só aparece no fim.
- **Temperatura:**
  - **GPU NVIDIA:** a temperatura máxima durante a captura é lida do próprio driver (`System32\nvml.dll`), uma leitura a cada 2 s e só enquanto a captura roda.
  - **AMD e Intel:** sem leitura por enquanto.
  - **CPU:** não é exibida. Ler a temperatura da CPU exige driver em modo kernel, que está vetado, e o PresentMon de console não expõe telemetria.
- **Segurança do PresentMon:**
  - o arquivo é copiado para `%ProgramData%\Leve\tools\<guid>`, uma pasta que só administradores acessam;
  - a cópia fica travada contra escrita e a assinatura é verificada pelo próprio handle aberto;
  - o signatário precisa ser exatamente "Intel Corporation" e o produto precisa ser o PresentMon;
  - o CSV é gravado na mesma pasta protegida.

### Orçamento do agente (requisito, não meta)
O agente não pode competir com o jogo. O desenho garante isso:

- **CPU ociosa ≈ 0%.** Não há nenhum timer nem polling.
  - O agente dorme num `MsgWaitForMultipleObjects`.
  - Só acorda quando a janela em primeiro plano muda (`SetWinEventHook`, evento do próprio Windows) ou quando o processo do jogo termina (espera no handle do processo).
- **A cada troca de foco**, ele lê o caminho de **um** processo e compara com uma lista em memória. O trabalho é de microssegundos.
- **Ao entrar em jogo**, ele lista os processos **uma vez**. Ao sair, restaura e fecha os handles.
- **O próprio agente roda em prioridade baixa e em EcoQoS** e devolve a RAM ociosa ao Windows (`EmptyWorkingSet`) depois de cada transição.
- **RAM:** em torno de 15 a 25 MB, o mínimo do runtime .NET.
  - Ele não carrega WPF, não tem janela e não grava disco a não ser nas transições.
  - Uma versão nativa em C++ ficaria em cerca de 2 MB. Só vale a pena se a medição real mostrar que o .NET pesa.
- **O agente é um ajuste como os outros**: um valor em `HKCU\...\Run`, marcado no Game Ultra e no Dev, com desfazer.
- **Roda sem administrador.** Só mexe nos processos da sua própria conta.

### Recusado para o agente
- Prioridade "tempo real" (trava o sistema).
- Suspender processos. Suspender o Google Drive travaria o `G:` inteiro.
- Matar processos.
- Limitar a RAM de outros processos.
- Driver em modo kernel.

## 12. v1.2: o que mais pesa além do Windows

| Peça | O que faz | Estado |
|---|---|---|
| **Etiquetas de impacto** | Coluna "impacto" na lista (FPS ▲▲, 1%low ▲, latência, carga, ping, RAM, privac., uso) e a faixa típica no detalhe. A confirmação do Game Ultra mostra uma estimativa honesta. O número real vem da tela `> fps`. | escrito |
| **Estado "feito / pendente"** | Substitui ON/off, que confundia com a caixa de marcar. | escrito |
| **`> diagnóstico`** | Uma linha por item, com "corrigir" só onde o Leve resolve sozinho. | escrito |
| **Monitor abaixo da taxa nativa** | Exemplo: 60 Hz num monitor de 165 Hz. O botão "corrigir" passa pelo snapshot e pede **manter** (Enter) ou **voltar** (Esc); sem confirmação, volta sozinho em 15 s, como o Windows. Fica fora de perfil de propósito. | escrito |
| **RAM sem XMP/EXPO** | Detectada pela velocidade configurada no SMBIOS (DDR4 até 2666, DDR5 até 4800 = padrão). Só orienta; o Leve não mexe no BIOS. Em notebook não aparece. | escrito |
| **Memória em canal único** | Um pente só em desktop: indica dual channel. | escrito |
| **Limite de FPS** | Sugestão Hz − 3 com VRR e Reflex/Anti-Lag. Escrever no perfil do driver NVIDIA ficou de fora: exigiria a API NVAPI DRS, pouco documentada. | escrito (orientação) |
| **Driver de vídeo antigo** | Alerta quando o driver tem mais de 6 meses. | escrito |
| **Disco quase cheio** | Menos de 10% livre, com destaque se tiver jogos. | escrito |

## 13. Proposta: modo Avançado (v1.3, a decidir)

Um modo separado, desligado por padrão, em que cada item exige **aprovação explícita do risco** e **medição A/B**. Entram só itens com **ganho real e risco real**:
- desligar as mitigações Spectre/Meltdown;
- desligar o VBS inteiro (além do HVCI);
- MSI Mode e afinidade de interrupção por dispositivo;
- desligar a compressão de memória em PCs com 32 GB ou mais;
- desligar o SysMain;
- desligar o Windows Update temporariamente durante o jogo.

Placebo continua fora, mesmo no modo Avançado, porque não há ganho a aprovar: Win32PrioritySeparation, NetworkThrottlingIndex, Timer Resolution global, limpeza de RAM, limpador de Registro.
