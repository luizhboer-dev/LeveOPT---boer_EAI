# Leve 1.1 (em andamento)

Otimizador de Windows 11 focado em desempenho para jogos. Tem um botão **Game Ultra**, que aplica tudo de uma vez, e um **snapshot** que permite desfazer o lote inteiro. O plano completo está em [PLANO-LEVE-1.0.md](PLANO-LEVE-1.0.md).

## Gerar o instalador (para distribuir entre amigos)

1. Instale o Inno Setup 6 (uma vez só):
   ```powershell
   winget install JRSoftware.InnoSetup
   ```
2. (Opcional) Coloque o `PresentMon-2.x.y-x64.exe` em `packaging\tools\` para ele entrar no instalador.
3. Na pasta `leve-next`, rode o script abaixo. Ele testa, publica, gera o Setup e calcula o SHA-256:
   ```powershell
   powershell -ExecutionPolicy Bypass -File packaging\build-installer.ps1 -Version 1.1.0
   ```
4. Saída em `packaging\out\`:
   - `Leve-Setup-1.1.0.exe`;
   - `Leve-Setup-1.1.0.sha256.txt`, para mandar por um canal diferente do instalador.

**O que o instalador faz:**
- instala em `C:\Program Files\Leve`, pasta protegida;
- cria um atalho no Iniciar (o da área de trabalho é opcional);
- instala o .NET Desktop Runtime 10 se faltar (via winget) e, se não conseguir, abre a página de download;
- mostra o LEIA-ME antes de instalar.

**Ao desinstalar:**
- avisa que os ajustes não são desfeitos sozinhos;
- para o agente e remove a tarefa e a entrada de inicialização dele.

## v1.1: Modo Jogo dinâmico

O `Leve.Agent.exe` fica ao lado do `Leve.exe` e liga com o ajuste **"Modo Jogo dinâmico (agente)"** em `> gaming`. O ajuste vem marcado no Ultra e no Dev e tem desfazer.

- **O jogo em primeiro plano** (qualquer exe dentro das bibliotecas da Steam, Epic, Xbox, EA, Ubisoft, GOG ou Riot) vai para prioridade **acima do normal**.
- **Pesados** (navegadores, launchers, OneDrive e Google Drive, VS Code, Obsidian) vão para **abaixo do normal + EcoQoS**.
- **Comunicação e música** (Discord, Teams, Spotify) **ficam intocados**: rebaixá-los com a CPU cheia faria a voz picotar.
- **A troca entre jogos é automática**: se o launcher (REDprelauncher, League Client…) passar o foco para o jogo, o agente muda de alvo.
- **Restauração cuidadosa**: só volta o que ainda está como o agente deixou, então o EcoQoS que o próprio Chrome/Edge aplicam nas abas de fundo é preservado. Se o agente morrer no meio da partida, a próxima execução desfaz o rebaixamento pelo diário `agent-demoted.json`.
- **Quando o jogo fecha, tudo volta** exatamente como estava.
- **Custo em repouso ≈ 0% de CPU.** Não há timer nem polling: o agente só acorda quando a janela em primeiro plano muda ou quando o jogo fecha. Usa cerca de 20 MB de RAM e roda sem administrador.
- **Status na barra do topo:** `agente: em jogo · jogo.exe · 7 rebaixados`.
- **Log** em `%LOCALAPPDATA%\Leve\agent.log`.

### Também na v1.1
- **`> fps` (PresentMon):** captura A (sem otimização) e B (com), com FPS médio, 1% low e um veredito que considera o ruído. A tela é mínima e nada roda sobre o jogo.
  - Temperatura: só a máxima da GPU NVIDIA, lida pelo driver.
  - A captura começa 10 s depois do clique, para dar tempo de voltar ao jogo.
  - Baixe o PresentMon 2.x de console (`PresentMon-2.x.y-x64.exe`) em github.com/GameTechDev/PresentMon/releases e coloque ao lado do `Leve.exe`.
  - O Leve só executa a cópia protegida, com assinatura válida da "Intel Corporation".
- **GPU dedicada por jogo:** só em PC com duas GPUs.
- **Rede sem economia de energia:** um ajuste por placa, no Ultra.
- **MPO desligado:** ajuste manual para teste A/B.

### Publicar (dois executáveis na mesma pasta)
```powershell
dotnet publish src/Leve.App/Leve.App.csproj -c Release -o publish
dotnet publish src/Leve.Agent/Leve.Agent.csproj -c Release -o publish
```

## O que o M3 acrescenta

- **Segurança × desempenho, só se você marcar.** Na confirmação do Game Ultra aparece uma caixa vermelha separada. Nem o perfil nem o "marcar todos" ligam esses itens:
  - **Desligar a integridade de memória (HVCI)**: costuma render 5 a 10% de FPS e pede reinício. Só aparece se o HVCI estiver ligado.
  - **Defender ignora as pastas de jogos**: o Leve encontra Steam (todas as bibliotecas), Epic, Xbox/Game Pass, EA, Ubisoft, GOG e Riot. Nunca aceita a raiz de uma unidade, o Windows, o Program Files inteiro ou um perfil de usuário.
- **Relatório de antes e depois** (`> relatório`): processos, RAM em uso, memória comprometida, serviços rodando e apps na inicialização.
  - A referência "antes" é gravada automaticamente na primeira vez que o Leve abre.
  - Botões para medir de novo, trocar a referência e copiar o texto.
- **Dev Drive** (perfil Dev): atalho para a tela do Windows que cria o volume. O Leve não cria volumes sozinho.

## O que o M2 acrescenta

- **Apps**: 24 apps de fábrica removíveis, entre eles Copilot, Teams, Outlook novo, Clipchamp, Bing e Dicas.
  - A remoção vale só para a sua conta. Desfazer re-registra o app sem internet.
  - Xbox, Store, Terminal, Fotos e codecs nunca entram na lista.
- **Inicialização**: o Leve lê o que abre com o Windows *neste* PC.
  - Launchers, chat e navegadores vêm marcados no Ultra.
  - Sincronização de nuvem (Google Drive, OneDrive) nunca vem marcada.
  - Antivírus, anti-cheat, drivers e periféricos ficam protegidos e nem aparecem.
- **Updaters de terceiros** (Google, Edge, Adobe, Opera, Brave e outros): as tarefas agendadas que acordam em segundo plano são desligadas.
- **Energia**:
  - Plano *Leve Ultimate*. Na tomada: CPU mínima 100%, sem core parking, USB e PCIe sem economia. Os C-states continuam ligados.
  - Hibernação e Inicialização Rápida desligadas (só em desktop).
- **Serviços parados na hora**: o ganho de RAM aparece sem reiniciar.
- **Limpeza de disco**: tela própria em `> limpeza`, com o tamanho de cada item.
  - Limpa temporários, cache do Windows Update e do Delivery Optimization, despejos de travamento e a Lixeira.
  - Nunca apaga Prefetch nem cache de shaders.
  - Não entra no snapshot, porque apagar arquivo não tem volta.

## O que o M1 entrega

- **35 ajustes** em 5 módulos: privacidade, processos de fundo, serviços, gaming e visual. O catálogo fica em `src/Leve.Core/Catalog.cs`.
- **Perfis**:
  - *Game Ultra*: tudo marcado, e é o padrão ao abrir.
  - *Gamer + Dev*: mantém o indexador, o spooler e o visual.
  - *Manual*: você marca item a item.
- **Aplicar tudo**: o botão `⚡ GAME ULTRA` ou `enter` abre uma confirmação com o resumo real do que vai mudar. O resumo é calculado lendo o estado atual da máquina.
- **Snapshot por lote**:
  - O valor original de cada alvo é gravado **antes** da primeira escrita.
  - Cada escrita é conferida por releitura.
  - `u` desfaz o último lote. "desfazer este ajuste" reverte um item só.
  - Se algum programa alterou um valor depois do Leve, a reversão preserva esse valor e avisa no log.
- **UI terminal**: barra ao vivo de CPU, RAM e número de processos; módulos à esquerda; log de execução embaixo; tudo pelo teclado.

| Tecla | Ação |
|---|---|
| `↑` `↓` | navegar |
| `espaço` | marcar/desmarcar |
| `a` / `x` | marcar/desmarcar visíveis |
| `enter` | aplicar marcados |
| `p` | trocar perfil |
| `/` | filtrar |
| `u` | desfazer último lote |
| `r` | reler estado |
| `?` | ajuda |

## Estrutura

```
src/Leve.Core      modelo, catálogo, motor (apply/revert), snapshots JSON   net10.0 (testável em qualquer SO)
src/Leve.Windows   Registro, SCM, Task Scheduler (COM), métricas, fatos     net10.0-windows (sem PowerShell)
src/Leve.App       WPF, tema terminal, MVVM sem dependências              net10.0-windows
tests/Leve.Core.Tests  xUnit: motor, catálogo, store
```

Os dados ficam em `%LOCALAPPDATA%\Leve\`: snapshots em `snapshots\`, além de `leve.log` e `crash.log`.

## Compilar e gerar o .exe

O build exige o .NET SDK 10. Para rodar o app, o PC precisa do .NET Desktop Runtime 10.

```powershell
dotnet test tests/Leve.Core.Tests
dotnet publish src/Leve.App/Leve.App.csproj -c Release -o publish
```

O resultado é `publish\Leve.exe`, um arquivo único que pede UAC ao abrir.

## Estado desta entrega

Este código foi escrito sem compilar: o ambiente onde ele foi gerado não tinha o .NET SDK. Uma revisão independente fez o papel de compilador e encontrou um erro de build (NETSDK1135), além de oito bugs de lógica. Todos foram corrigidos e cobertos por `EngineRegressionTests`. Mesmo assim, o primeiro `dotnet build` no Windows ainda pode acusar algum erro pequeno, e os testes do Core ainda não rodaram.

Antes de usar no PC principal, teste numa VM ou num PC secundário. O snapshot desfaz os ajustes do Leve, mas não substitui um ponto de restauração.
