# SotC-PC Randomizer: como funciona e como reaplicar

Para o Claude (ou quem for mexer): tudo o que é preciso para pôr o mod de volta depois de uma
atualização do lançador, e o que cada peça faz. O port é o `D:\SotC-PC` (Shadow of the Colossus
PS4 remake, eboot EU 1.01 / US 1.00, loader bb-probe do bloodborne_pc + núcleo shadPS4).

## Reaplicar depois de atualizar o lançador

Rode **`SotC-Randomizer-Patch.exe`** (ou `APLICAR-RANDOMIZER.bat`): ele pede o `SotC.exe` do
lançador (acha o `D:\SotC-PC` sozinho), guarda o lançador novo como `SotC.orig.exe`, põe a casca
(`files\SotC.exe`) no lugar e copia `launcher_mod.py`, `SotC-Randomizer.exe`, `psarc.exe`,
`RANDOMIZADOR.bat` e este tutorial para `D:\SotC-PC\mod_backup`. Serve para qualquer versão do
lançador (o mod é aplicado em memória, nada do lançador é editado). "Remover patch" devolve o
lançador original. Modo sem janela: `SotC-Randomizer-Patch.exe --apply <SotC.exe>` / `--remove`.

No lançador, tela **Jogar → Ajustes rápidos**, abaixo de "Modo PS4 Pro" (só as caixas marcadas no patch,
`mod_backup\randomizer_mods.txt`: `items/colossi/roupas/calango=0/1`):
- **"Randomizer de itens (mod)"** (`"randomizer": true` em `user\launcher_settings.json`);
- **"Randomizer de colossos (mod)"** (`"randomizer_colossi": true`);
- **"Wander com roupa da Mono (mod)"** (`"mod_roupas"`) e **"Som do calanguinho (mod)"** (`"mod_calango"`):
  mods de arquivo, aplicados/desfeitos nos psarc do jogo ao clicar JOGAR (seção "Mods de arquivo").
Todas podem ficar ligadas juntas. Ao clicar JOGAR, a casca inicia `SotC-Randomizer.exe --once
--items --colossi` conforme as opções. O atualizador do port sobrescreve `SotC.exe` (a casca some);
nada do randomizer depende dos arquivos do jogo.

## As peças

- **`SotC.exe` (`shell.cs` + `randomizer.cs`)**: a casca. Se existe `mod_backup\launcher_mod.py`,
  executa `SotC.orig.exe --script mod_backup\launcher_mod.py <args>` (o lançador original é um
  PyInstaller com Python 3.12 e aceita `--script <arquivo>`). Sem o script, forma antiga: `extra_env`
  com `RANDOMIZER=1` roda o randomizador de itens numa thread.
- **`launcher_mod.py`**: tira o code object `sotc_launcher_win` do pacote PyInstaller de
  `SotC.orig.exe` (cookie `MEI\014\013\012\013\016`, TOC `!iIIIBc<nome>`), executa com
  `__name__ = 'sotc_launcher_win'` e troca métodos da classe `Launcher`: `check` (cria os dois
  checkboxes depois de `neo`), `summary` ("randomizer: itens + colossos"), `play` e
  `play_without_window` (iniciam o randomizer com `--items`/`--colossi`), `restart` e `shortcut`
  (passam pela casca). Falhas vão para `mod_backup\launcher_mod.log` e o lançador original abre sem o mod.
- **`SotC-Randomizer.exe` (`standalone.cs` + `randomizer.cs`)**: espera o `bb-probe` da pasta do
  port, acha a base do eboot (bytes únicos em eboot+0x577967), instala os patches em memória e vigia
  as flags. `--once` termina com o jogo; `--items` e/ou `--colossi` escolhem os modos (sem modo =
  itens). `RANDOMIZADOR.bat` roda à mão com os dois modos. Estado em `randomizer_state.txt`
  (`order`/`given`/`pending`/`collected` dos itens; `corder` = ordem dos colossos, `kills`), log em
  `randomizer.log`. Apagar o estado = sortear tudo de novo.
- **`SotC-Randomizer-Patch.exe` (`patcher.cs`)**: o instalador com janela, embute os arquivos acima
  (`csc /resource:`). Detecta o lançador original pelo cookie PyInstaller e a casca pela string
  `SotC.orig.exe`.
- **`psarc.exe` (`psarc.cs`)**: lista/extrai/`replace`/`restore` em PSARC v1.4 zlib (nomes casados
  pelo MD5 do caminho); usado pelo mod de roupas e `RESTAURAR_ORIGINAL.bat`.

## O que acontece dentro do jogo (endereços do eboot, iguais em EU 1.01 e US 1.00)

- Flags: array de bits em `0x325F3B0`; `FlagGet 0x7dbba0`, `FlagSet 0x7dbb00`, `FlagClear 0x7dbb50`;
  notificação de mudança `0x7e3470`. Nomes em `G_FLAG_<NOME> <n>` dentro do eboot (398 flags).
  `DEAD_<colosso>` = 40..55 (**1 vivo, 0 morto**; ordem KNIGHT, BIRD, KAME, SNAKE_C, MAMMOTH,
  MINOTOUR_A, MINOTOUR_B, MINOTOUR_C, KIRIN, LEO, POSEIDON, NARGA, CERBERUS, YAMORI_B, EEL, EVIS).
  Ordem da campanha (slots 0..15): Valus=MINOTOUR_A, Quadratus=MAMMOTH, Gaius=KNIGHT, Phaedra=KIRIN,
  Avion=BIRD, Barba=MINOTOUR_B, Hydrus=EEL, Kuromori=YAMORI_B, Basaran=KAME, Dirge=NARGA,
  Celosia=LEO, Pelagia=POSEIDON, Phalanx=SNAKE_C, Cenobia=CERBERUS, Argus=MINOTOUR_C, Malus=EVIS.
  Outras: BOOT_x 66..81, LAYOUT_DISABLE_x 93..109, LAYOUT_HOME_ITEMn_LIGHT 164+n,
  HOME_ITEMn_START_CAM 190+n, HOME_ITEMn_GET_EVENT 216+n, SAVE_HOKORA_DEMO_START 27,
  SAVE_HORSE_NO_EXIST 28, EVENT_<célula>_x 449..465, x_HOME_END 540..554, NEXT_x 555..569.
- Patch 1, proteção: `0x577960` (finalizador do item) recebe nulo e derruba o jogo; vira
  `test rdi,rdi; jz ret` (trampolim nos nops em `0x577955`).
- Patch 2, gancho de flags: `FlagGet` salta para a *code cave* `0x1593980` (padding do código);
  se a caixa de correio `0x3956FF0` (padding do bss) tem `flag+1`, chama `FlagSet(flag)` (bit 16:
  `FlagClear`) na thread do jogo. Os pedidos ficam numa fila no randomizador e são reenviados se não
  forem consumidos (durante a cinemática de morte o jogo passa segundos sem chamar `FlagGet`).
  Páginas de código aceitam `PAGE_EXECUTE_WRITECOPY` (não `READWRITE`).
- Patch 4, contador de mortes: `KillBoss(entrada) 0x7D1700` (grava `[0x325E8A0]` = último morto e faz
  `FlagClear(DEAD)`) salta para a *cave* `0x1593A80`, que incrementa `0x3956FE0` e guarda a entrada em
  `0x3956FE8` antes de limpar a flag. Morte real = bit DEAD 1→0 **junto com** o contador; 1 bit sozinho
  = save carregado. (Antes o critério era "colosso atual == morto", que falhava porque o script do
  santuário mata e já escolhe o próximo no mesmo quadro.) A mesma cave corrige o recarregamento do
  autosave feito logo após a morte (flag 27 SAVE_HOKORA_DEMO_START): nesse caso o fluxo (`0x7E084C`)
  chama `KillBoss(atual - 1 entrada)` supondo a ordem da tabela; com `ORDER_VALID` = 1 a cave troca
  pelo colosso que antecede o atual em `ORDER` (ou não mata nada se for o primeiro).
- **Randomizer de itens**: 16 itens em 15 mortes úteis (o 1º colosso dá 2 itens, do 2º ao 15º dão 1
  cada, o 16º nenhum, pois não há jogo depois dele; alvo de `given` = min(16, mortos+1), o que também
  repõe um item perdido). A cada morte (DEAD 1→0 junto com o contador do patch 4) o item sorteado vai
  **direto para o inventário**: o randomizador liga o bit do item em `0x325F7A2` (bit `id`, byte
  `id>>3`; a mesma conta de `GiveItem(id) 0x7DBF60`, que é o que `shadow_giveAllNormalItems` e
  `shadow_timeAttackSetRetroactiveCompletionCount 0x7FD590` usam) e confere o bit por 2 min
  (`KeepItems`). A v2.3 chamava `GiveItem` pela cave do gancho de flags (pedido `(id+1) | 0x20000`, ainda
  aceito pela cave), mas `GiveItem` termina em `0x7837F0` (aviso do menu) e rodar isso numa chamada
  qualquer de `FlagGet` (cena da morte) coincidiu com um crash no autosave depois do Malus (free com
  ponteiro corrompido, thread auxiliar, `crashes\2026-10-26_22-48-15`); por isso a v2.4 escreve o bit.
  Versão anterior acendia a fonte (`LAYOUT_HOME_ITEMn_LIGHT` 164+n e `START_CAM` 190+n), mas o objeto
  da fonte só existe para recompensas liberadas pela contagem do Time Attack (`TIME_ATK_CLEAR_x`
  120..135, listas normal/difícil em `0x128C70C`/`0x128C74C`, tabela `[0x1BBBF60+0x20]`): o raio
  aparecia, o item ficava invisível e pegar não dava nada.
  Ids (tabela de itens `0x1BCA0C0`, nomes em japonês no eboot): 19 Flecha assobiante, 7 Capa de
  dissimulação, 2 Máscara de força, 38 Pedra dos lagartos, 37 Mapa de árvores, 3 Máscara de poder,
  6 Capa de energia (活力の前掛け), 22 Espada do sol, 20 Arpão do trovão, 18 Flecha explosiva, 5 Capa do
  Xamã, 1 Máscara do Xamã, 35 Tecido do desespero, 23 Espada da rainha, 29 e 34 = cavalos (não estão
  na tabela de itens; confirmar no jogo). Itens equipam-se no mapa (Tab) → Itens.
- **Randomizer de colossos** (achados de `gameflow.cpp`/`st_gameflow 0x7DFB70`):
  - Tabela dos colossos: 16 entradas de 0x1E8 bytes em `0x16D4010` (cabeçalho em `0x16D21F8`:
    +0x18 = 16, +0x20 = ponteiro das entradas), na ordem da campanha. Campos: +0 nome, +8/+C/+10
    posição do colosso (**é para onde a espada aponta**), +0x28 flag SAVE, +0x2C flag DEAD, +0x30 BOOT,
    +0x34 x_HOME_END, +0xBD byte de área, +0xC0..+0xDC posições de retorno, +0x168/16C/170 itens do
    Time Attack, +0x18C TIME_ATK_CLEAR, +0x1B0 BGM, +0x1E0 objeto em tempo de execução.
  - Variável de jogo 8 = slot do colosso atual (`0x7DBC70(8)` lê, `0x7DBC10(8,n)` grava); cópia em
    `0x325F7B8`; entrada atual = `0x7D1890`. `SetCurrentBoss(entrada) 0x7D1900` liga o layout do
    colosso (flags LAYOUT dos sub-objetos), grava a var 8 e `0x325F7B8`, limpa os x_HOME_END.
  - **`FirstAliveBoss() 0x7D1A70`** devolve a primeira entrada da tabela cuja DEAD ainda está em 1
    (vivo) ou NULL. É chamada por `nico_shamanStart 0x747A60` (script do santuário: marca o atual
    como morto com `0x7D1700`, escolhe o próximo e chama `SetCurrentBoss`; **sem próximo, dispara o
    evento `[0x222FB30]` = final do jogo**) e no início do fluxo (`0x7E011F`, partida nova).
  - Patch 3: `FirstAliveBoss` salta para a *cave* `0x1593A00`: se `ORDER_VALID` (`0x3956FD0`) = 1,
    percorre `ORDER` (16 bytes de slots em `0x3956FC0`) e devolve a primeira entrada viva (NULL se
    nenhuma); senão executa a função original. Assim espada, arena, cutscene de chegada, volta ao
    santuário e final seguem a ordem sorteada, e o final só vem com os 16 mortos.
  - Identidade por ponteiro: tabela de 16 ponteiros em `0x16D5E90` (slot 15 = Malus) usada para
    "é o último colosso?" no reinício da luta (`0x7E19DF` → DEMO_F8_EVIS 570), ídolos do templo
    (`0x7FCC10`), estatísticas, tentáculos do Hydrus. Nada disso depende da ordem, por isso a
    permutação é feita só na escolha do próximo, não na tabela.
  - **Flags NEXT_x (555..569)**: "este colosso é o próximo"; os scripts das áreas dependem delas (o
    portão de F8 para o Malus só abre com NEXT_EVIS 569; a luz da espada também falhou lá sem ela). O
    jogo liga a flag por script do santuário seguindo a ordem da tabela (NEXT do seguinte = HOME_END do
    morto + 15: Valus 540 → NEXT_MAMMOTH 555, Argus 554 → NEXT_EVIS 569; nenhum `mov edi,569` no
    código). Com a ordem sorteada o randomizador (v2.4, `KeepNextFlags`) mantém ligada só a NEXT do
    colosso atual (var `0x325F7B8`) e apaga as outras. Tabela de nomes das flags: ponteiros em
    `0x1604D00 + n*8` (relocações SCE). Flags do portão: DEMO_F8_EVIS 570 (`FlagSet` em `0x7E1B3B`),
    DEMO_F8_GATE_EVIS 571 (`FlagGet 0x7E46A9`, `FlagSet 0x7EA87C`, `FlagClear 0x7EAEBA`),
    DEMO_F8_GATE_PLAYING 572. Estado do mundo do remake `A16_Gate_Open` (registro em `0x557ED9`).
    Se o portão continuar fechado com NEXT_EVIS, o próximo passo é ligar também a 571.
  - Malus antes do fim: ao morrer com colossos restantes o randomizador pede `FlagClear(28)`
    (SAVE_HORSE_NO_EXIST) para o Agro voltar no santuário (`0x7E2910` chama `0x7E6600` se a flag
    está em 0). A ponte (SANCTUARY_BRIDGE_BREAK 369) fica quebrada depois.
  - Partida nova: o randomizador deixa pronta a ordem da próxima partida (`cnext` no estado) em
    `NEXT_ORDER` (`0x3956FA0`, válida em `0x3956FB0`). Quando o fluxo do jogo pede o primeiro colosso
    de um "Novo jogo" (`FirstAliveBoss` chamada de `0x7E011F`, retorno `0x7E0124`), a cave 2 copia
    `NEXT_ORDER` para `ORDER` e liga `NEWGAME` (`0x3956FD1`); o randomizador vê a flag, adota a ordem,
    zera as mortes, descarta pedidos de item pendentes, sorteia os itens de novo e prepara outra
    `NEXT_ORDER`. Assim cada partida nova começa por um colosso diferente, sem corrida com o jogo.
    (Reserva: na primeira morte de uma partida com progresso anterior ainda há o sorteio antigo, com
    o colosso morto em primeiro.) Save carregado = vários bits DEAD mudando ao mesmo tempo, ou 1 bit
    sem o contador de mortes do patch 4 acompanhar; nesse caso a fila de pedidos de item é descartada.
  - O patch grava `mod_backup\randomizer_mods.txt` (`items=0/1`, `colossi=0/1`) conforme as caixas da
    janela; o `launcher_mod.py` só cria no lançador as opções instaladas e o patch liga as escolhidas em
    `user\launcher_settings.json` (sempre sem BOM).
  - Depuração: comando de console `shadow_jumpToBoss <0-15>` existe no eboot (`0x7DDEB0` monta
    as flags de "n mortos"; tabela de saltos `0x1289B2C`); `killBoss`; cvar `cp10_debugSwordLight`.

## Crash na volta ao santuário (3 casos) e a espera por cutscenes da v2.5

Três crashes, todos na **primeira morte de uma partida com o randomizer de colossos ligado**, na transição
de volta ao santuário (vídeo: luz branca → sombras em volta do Wander → santuário com faixas pretas → cai;
o usuário descreveu a última como "a cena da fonte, como se fosse mostrar 2 itens"). O `shadow\events.log`
do loader mostra "Carregando um save ou uma área nova" 1,4–2 s antes da queda, logo após o autosave.
**Recarregar o autosave pós-morte não cai** (02:36 de 2026-10-27, com os mesmos itens no inventário e sem
GET_EVENT/LIGHT: chegou ao santuário e jogou 1,5 min), então a causa está no que só acontece ao vivo.

- 2026-10-26 22:48 (Malus 1º, v2.3): `free()` com ponteiro `0x7e7b00007e7b` numa thread auxiliar
  (`crashes\2026-10-26_22-48-15_0x3.log`).
- 2026-10-27 01:20 (Basaran 1º) e 11:34 (Quadratus 1º), ambos v2.4: o mesmo `eboot+0x295B43` =
  `Mutex::Lock(this)` com `this = 0x1A`. O `shadow\last_run.log` do lançador plus20 passou a trazer a pilha
  (frames `#0..#5` com offsets do eboot): thread **`cameraman_manager_task`** (criada em `0x7CC3F3` com
  `0x7CE890`; laço `0x7CCAB0` → `0x79EC10` → `0x5902C0`, que atualiza os slots de câmera) → `0x6111C0` →
  `0x60B610` (lê `[obj+0x10]`, que valia 2: objeto já liberado) → `0x295B20` (lock). O gerente de câmeras de
  cutscene tem **10 slots de 0x138 bytes em `0x2FD8C70`** (slot+0 = corte ativo, +0x28/+0x2C do corte =
  duração/estado, slot+0xB8 = objeto alvo/animação, +0x100 posição, +0x130/+0x131/+0x132 estados; nomes de
  corte tipo `Boss03DeathCut02`). O slot ativo apontava para um objeto da cena anterior destruído quando o
  santuário carregou (use-after-free).
- Fator comum exclusivo da transição ao vivo na v2.4: na hora da morte (início da cena de morte) o
  randomizer apagava/ligava as flags NEXT via caixa de correio (`KeepNextFlags`), ou seja, `FlagSet`/
  `FlagClear` rodando na thread que chamasse `FlagGet` em plena cutscene. `FlagSet` (0x7DBB00) não é só o
  bit: chama `0x7E3470` (sincroniza o world state `A2_Wall_Intact` a partir da flag 70, mapa em `0x2EFD160`)
  e salta para `0x5707E0` (para GET_EVENT 217..232 sincroniza os world states `Collected_<Item>`). Na v2.3 o
  que rodava nessa hora era `GiveItem` (aviso de menu), e o crash foi no autosave. Os cortes de câmera não
  usam `mov edi,NEXT` em código (as NEXT só aparecem em scripts), por isso o mecanismo exato (script da
  área abortado pela flag ou corrida de thread nas notificações) não foi fechado.
- **Teste da v2.5 (12:54, Phalanx 1º): caiu igual**, com a espera ativa e nenhum pedido de flag enviado durante a
  cinemática — as flags NEXT estão descartadas como causa. O usuário confirmou (3ª vez) que a queda é na
  **cutscene da fonte mostrando o item novo**, logo após a cutscene de chegada. Leitura: o santuário dispara a
  câmera da fonte para um item recém-possuído, e com o slot apagado (sem LAYOUT_HOME_ITEMn_LIGHT) não existe
  objeto para a câmera seguir → ponteiro liberado no slot do cameraman. Na v2.3 o slot era aceso (LIGHT +
  START_CAM) e a câmera rodou sem cair (item invisível e não coletável, mas já no inventário).
- **Testes de 13:28–13:48**: v2.6 caiu igual com o slot aceso (Phaedra); **sem o mod a volta funciona** (Valus,
  plus21); **só com o randomizer de itens cai igual** (Valus, ordem original) → não é a cave 2. O fator comum de
  todos os 5 crashes e ausente nos sucessos (v2.3, recarregamento) é o item posto no inventário por **escrita direta
  do bit** (v2.4..v2.6). `GiveItem` 0x7DBF60 = bit + `call 0x571DF0` (é um `ret` vazio) + `jmp 0x7837F0` (aviso
  de item do remake); sem o 0x7837F0 o lado do remake não conhece o item e a apresentação na fonte segue um objeto
  inexistente. Recarregar o save reconstrói o estado do remake a partir dos bits, por isso nunca cai.
- **v2.7**: nada mais é escrito direto no inventário. Na morte o item é só sorteado (`togive=` no estado) e, quando
  a espera por cutscenes termina (`Deliver`, jogador no controle no santuário), vai pela caixa de correio:
  `GiveItem(id)` (pedido `(id+1)|0x20000`) + LIGHT + START_CAM por `FlagSet`. `KeepItems` repete o GiveItem se o
  item sumir (save recarregado) e `EnsureLit` acende slots apagados, ambos só fora de cutscene e com a fila vazia.
- **v2.7 CONFIRMADA (14:01, Valus, só itens)**: morte → santuário sem queda; aos 60 s o jogo aplicou GiveItem(38),
  GiveItem(5) e LIGHT/START_CAM dos slots 11 e 10. O jogador não notou nada na chegada; ao sair e voltar ao
  santuário veio a cutscene de pegar o item (START_CAM) e a Capa do Xamã; na fonte restavam 2 luzes, uma invisível
  (Pedra dos lagartos, o modelo do slot segue a ordem do Time Attack), e os itens apareceram no menu. Um terceiro
  item era fantasma: o jogo apaga a LIGHT ao apresentar o item e `EnsureLit` a reacendia → **v2.8 não chama mais
  `EnsureLit`**. Comportamento atual: item no inventário logo após a chegada + cutscene de pegar na próxima entrada
  no santuário (START_CAM). Se o usuário preferir sem a cutscene/luz, basta não pedir LIGHT/START_CAM em
  `LightSlot` (não testado sem elas).
- **v2.9 CONFIRMADA (14:30, Valus, só itens)**: chegada sem queda; slots 12 e 8 acesos após a espera; ao pegar na
  fonte, `GiveItem 3` e `GiveItem 1` aplicados pelo jogo e START_CAM apagadas. Observações do usuário: sem cutscene
  na chegada (as flags só entraram depois; o jogo avalia START_CAM ao entrar no santuário) e 4 itens em vez de 2:
  um slot pendente da partida anterior foi reaceso antes da morte, e os prêmios especiais parecem persistir no
  **perfil** (SAVEDATA0ShadowProfile0: "Saving dirty user profile" a cada GiveItem), sobrevivendo ao novo jogo.
- **v2.10 CONFIRMADA com itens + colossos (14:49 Phalanx, 15:10 Avion)**: duas voltas ao santuário sem queda, a
  ordem sorteada funcionando (NEXT aplicadas após a espera). A fonte do jogo tem a **própria tabela slot → item**:
  um slot aceso com modelo visível entrega o item do jogo ao pegar (slot 16 deu a Flecha de luz, slot 2 a Flecha
  assobiante, slot 10 a Capa do Xamã), e slots "invisíveis" (3, 9, 11, 13) não entregam nada. `0x7837F0` (fim do
  GiveItem) é a cena "Esse é o <item>…" com legenda. O id 29 é um cavalo ("corcel"): 29 = Agro branco, 34 = Wander
  corrompido. A tabela em `[0x1BBBF60+0x20]` é um mapa item → (nome, posição 1..16, modo difícil), igual em
  memória e no arquivo; não dá o slot. Crash à parte às 14:57 em **Continuar** com o mundo carregado:
  `0x49500`, diagnóstico do próprio loader "lista de memória livre de um heap corrompida (escrita da GPU sobre
  memória reaproveitada)", pilha no loader: bug do port, não do mod.
- **v2.12 TESTADA (20:36-20:57, itens + colossos, partida nova)**: 1ª morte (Kuromori) → cena do Valus
  (`Valus_HOME_END` 28 s após a morte, espera liberada aos 31 s, NEXT corrigida para Celosia), sem queda; slot 14
  da fonte (visível) entregou a Capa de dissimulação (7), slot 11 invisível → GiveItem 38. 2ª morte (Celosia) →
  cena do Quadratus (`Quadratus_HOME_END` aos 33 s) e **o jogo caiu 1 s depois do carregamento pós-chegada**: falha
  em guest offset 0x490a0, no percurso da lista livre do alocador do jogo (0x49030, `mov rbx,[rax-8]` com rax lixo),
  mesma base de pilha (0x38ad2, 0x33586) da queda do port das 14:57 (0x49500, diagnosticada pelo próprio loader como
  memória livre corrompida por escrita da GPU). O usuário viu bugs visuais crescentes antes (vídeo 5,9 GB, limpezas de
  emergência de textura): bug do port, não do mod. Pendente da partida: slot 1 aceso com a Flecha assobiante
  sorteada, ainda não pego. O "brilho" que o usuário pediu para analisar é a **moeda dourada secreta** do remake (79
  relíquias; todas liberam a Espada de Dormin no topo do santuário): no eboot há `CollectibleManager`, `Collectible`,
  `PlayerCollectibleState`, evento `CollectibleCollected`, cvars de depuração `s_CollectibleCount`,
  `s_CollectibleForceComplete`, `s_CollectibleShowLocations`, world state `SecretRoomCollectibleCount`,
  `BeginCollectiblePrayer`/`EndCollectiblePrayer`, `Collected_SwordOfDormin` — ainda não investigado além dos nomes. **v2.12b**: depois de ver o
  HOME_END a espera segura os pedidos por mais 10 s (o santuário carrega uma área ~2 s depois do HOME_END).
- **v2.12 (cave 4: cena de chegada pela contagem de mortes + espera pelo HOME_END)**. Pedido do usuário: com a
  ordem sorteada, a k-ésima volta ao santuário deve mostrar a cena do k-ésimo colosso do jogo original (1 sombra na
  primeira, 2 na segunda...), não a do colosso que morreu. Achados no eboot: a rotina de chegada é `0x7E78A0`
  (gameflow estado 5, `0x7E1358`; roda na volta ao vivo e ao recarregar o autosave com a flag 27). Ela começa com
  `call 0x7D1710` (GetLastKilled = `[0x325E8A0]`) e usa só essa entrada do boss para tudo: demo A em `+0x130/+0x138`
  (a cena das sombras, `Temple_(NN)_boss_disappears_demo_Kageotoko`; pulada quando a flag 27 está ligada = reload),
  demo B (`+0x128`, `+0x140/+0x148`), demo C opcional (`+0x150..+0x160`), caso Pelagia (`entrada == [0x16D5EE8]`:
  Agro ausente na chegada) e, no fim, `FlagSet([entrada+0x34])` = `<boss>_HOME_END` (540..554; Malus não tem). O
  número de mortos vem de `0x7D1740` (conta DEAD == 0 na tabela; na chegada já inclui o colosso recém-morto). As
  sombras paradas no santuário e a sujeira do Wander já seguem essa contagem (16 getters de world state em
  `0x53E9C0..0x53EF60`). **Cave 4** (`0x1593BC0`, 75 bytes, só com `ORDER_VALID`): troca a chamada em `0x7E78BF`
  (`E8 4C 9E FE FF`) por `call cave4`, que devolve `ENTRIES + (mortos-1)*0x1E8` (k em 1..16; fora disso ou sem
  ordem → `jmp 0x7D1710`) e anota a entrada em `DEMO_ENTRY 0x3956F90` para o log ("cena de chegada: a do kº
  colosso"). `LASTKILLED` não muda. Efeitos aceitos: demo B/C, caso Pelagia e `HOME_END` passam a ser os do k-ésimo
  colosso (o script liga `NEXT_(k+1)`, que `KeepNextFlags` já corrige); o ídolo que quebra na cena é o do k-ésimo,
  mas o ídolo apagado no santuário é o do colosso real (cosmético). Malus morto antes do fim usa a entrada k-1 (no
  original ele nunca volta ao santuário; provável correção do travamento no portal brilhante de 2026-10-26); 16ª
  morte com outro colosso por último usa a entrada do Malus (fluxo vanilla do fim). **Espera**: na morte lê
  `[ENTRIES + e*0x1E8 + 0x34]` (e = mortos-1 com colossos, o próprio slot só com itens); se a flag está em 540..554
  e apagada, `StartHold(20 s, 300 s, flag)` e a espera só termina quando o jogo liga essa flag **e** não há câmera
  de cutscene há 3 s (log "o jogo ligou X_HOME_END: sequência de chegada concluída"); senão (Malus, 16ª morte, flag
  já ligada por save antigo) vale a regra antiga de 60 s. Resultado: o GiveItem da fonte sai na hora do pegar
  mesmo correndo para a fonte. O log da partida nova agora lista a ordem completa dos itens. Variante cirúrgica
  guardada caso o ídolo/fala do Dormin incomodem: trocar só a demo A patcheando `mov rax,[r12+0x130]` (0x7E78FD,
  8 bytes) e `bextr ebx,[r12+0x138],ecx` (0x7E7945, 10 bytes) em vez da chamada. Fonte: as listas
  `0x128C70C` (normal) / `0x128C74C` (difícil) são 16 **dwords** (slot HOME_ITEMn por posição) e a tabela de
  prêmios `[0x1BBBF60+0x20]` (16 × 0x40, só posições pares: +0 item normal, +0x1C item difícil) coincide com
  `Collected_*`/`RewardIds`, mas o que o jogo entrega ao pegar o pedestal aceso pela LIGHT n não segue isso
  (14:49: slots 9 e 16 acesos, jogo deu Flecha de luz 18) → a tabela aprendida continua valendo.
  Ferramenta: `scratchpad\cavedump.exe` (compila com `randomizer.cs` + `Iced.dll` + `netstandard.dll` do
  Framework64) desmonta as caves 3 e 4 geradas.
- **v2.11**: aprende o que o jogo entrega em cada slot observando o inventário 2,5 s após a luz apagar
  (`pickups`, `fonte_slots.txt` "slot=item", 0 = nada). Se o jogo entregou, o randomizer **não** dá o sorteado por
  cima (sem item duplo); se não entregou, `GiveItem` do sorteado. Sementes: 2=19, 10=5, 16=18, 3/9/11/13=0.
  Pendência: usar a tabela aprendida para escolher o slot certo do item sorteado (hoje o slot vem do mapa
  `Collected_*`, que não é o da fonte). A espera mínima de 60 s após a morte pode atrasar o GiveItem de quem corre
  para a fonte (cena do item sai ao se afastar); reduzir só com um sinal de "carregamento terminado".
- **v2.10**: `Award` acende LIGHT+START_CAM **na hora da morte por escrita direta do bit** (flags puras, sem código
  no jogo; a v2.6 fez isso e a câmera da fonte tocou na chegada) para a cena da fonte vir já na chegada; o item
  continua entrando ao pegar. Slots herdados do estado não são reacesos (`relightAllowed`); ao começar partida nova
  os slots acesos da partida anterior são apagados (`NewItemRun(g, …)`). Popup da morte sem nomes de itens.
  Ideia pendente: ao começar partida nova, remover do inventário os 16 prêmios (função em 0x7DBFB0 limpa o bit,
  `mov edx,0xFFFFFFFE`), já que o perfil os carrega entre partidas.
- **v2.9 "descoberta" (pedido do usuário: ver o item na fonte e ir buscá-lo)**: na morte só sorteia (`togive`);
  após as cutscenes `Deliver` acende o slot (LIGHT + START_CAM via FlagSet) **sem GiveItem**; o laço de `Pending`
  espera ver a LIGHT ligada (`litSeen`) e, quando ela apaga (o jogo apaga ao pegar, mesmo para prêmio que o Time
  Attack não liberou, caso em que o jogo não dá nada), faz `GiveItem` pela caixa de correio e apaga START_CAM.
  Slot nunca visto aceso em 2 min → pede a luz de novo. `litSeen` zera ao carregar save / partida nova. A câmera
  da fonte (START_CAM) toca na próxima entrada no santuário. Modelo na fonte pode ser invisível/outro item.
- **v2.6** (superada): ao dar o item, o randomizer também ligava **LIGHT 164+n e START_CAM 190+n por escrita direta do bit**
  (`LightSlot`), e a cada tick (`EnsureLit`) acende o slot de qualquer item nosso que esteja no inventário com o
  slot apagado (uma vez por slot por sessão; zera ao carregar save). Mapeamento slot→item corrigido pelos world
  states `Collected_*` de 0x5707E0 (slot n = flag GET_EVENT 216+n): 1 Flecha assobiante 19, 2 Capa de energia 6,
  3 Arpão 20, 4 Máscara de força 2, 5 Espada do sol 22, 6 Agro branco 34?, 7 Mapa 37, 8 Máscara de poder 3,
  9 Wander corrompido 29?, 10 Capa do Xamã 5, 11 Pedra dos lagartos 38, 12 Máscara do Xamã 1, 13 Capa de
  dissimulação 7, 14 Flecha explosiva 18, 15 Tecido do desespero 35, 16 Espada da rainha 23. O estado antigo é
  migrado pelo id (`map=2` em `randomizer_state.txt`). GET_EVENT continua sem ser ligada.
- **v2.5 (`Game.Hold`)**: nenhum pedido vai para a caixa de correio enquanto houver cutscene. Depois de uma
  morte a espera dura no mínimo 60 s e até não haver câmera de cutscene ativa por 3 s (`ActiveCuts()` lê os
  10 slots), no máximo 300 s; depois de save carregado, novo jogo ou início da sessão: mínimo 20 s, máximo
  180 s. Um pedido já na caixa e ainda não consumido é retirado e reenfileirado. `KeepNextFlags` não roda
  durante a espera. Para o Malus antecipado a flag 28 é apagada por escrita direta do bit no início da cena
  de morte (`ClearFlagDirect`) e conferida ao fim da espera. O log registra cada pedido aplicado ("pedido
  aplicado pelo jogo: flag n ligada"), as mudanças de NEXT feitas pelo próprio jogo ("o jogo ligou NEXT_x")
  e as esperas. **Se ainda cair** com a v2.5, o próximo passo é impedir a cave 1 de executar em qualquer
  thread: só consumir a caixa quando o retorno da chamada a `FlagGet` for um site conhecido do thread
  principal, ou trocar a caixa por um gancho num ponto por quadro desse thread.
- Caminho da transição: gameflow `0x7E0823` (`[0x2F29714]=1; if FlagGet(27) { KillBoss(CurrentBoss()-1
  entrada) }; estado [0x326021C]=5`), switch de estados `0x7E1275` (tabela `0x128A120`: estado 5 →
  `0x7E12B2`); `0x7E1380` acha o índice pela tabela de ponteiros `0x16D5E90`. A flag 27 ao vivo é ligada só
  para o autosave (`0x7E7B33`) e apagada em seguida (`0x7E7B4C`, `0x7E7D75`); no recarregamento é apagada em
  `0x7E1A9B` — por isso não serve de sinal de "chegada terminada" (o poller de 500 ms não a vê).
- Fonte do santuário (nomes reais das flags): LIGHT = 165..180 (164+n), START_CAM = 191..206 (190+n),
  GET_EVENT = 217..232 (216+n) ↔ world states `Collected_WhistlingArrow`, `Collected_CloakOfForce`,
  `Collected_MaskOfStrength`… (`0x5739xx` registra; `0x5707E0` sincroniza). `shadow_timeAttackSetRetroactive
  CompletionCount` (0x7FD590) liga START_CAM + GET_EVENT + `GiveItem` para as n primeiras recompensas
  (tabela `[0x1BBBF60+0x20]`, entradas de 0x40 com id do item em +0x1C; listas de slots `0x128C70C` normal /
  `0x128C74C` difícil). O randomizer não liga GET_EVENT (o recarregamento mostrou que não é preciso).
- Ferramentas da sessão de 2026-10-27 (scratchpad `d21eb018-...`): `dis.exe <va> <bytes decimais>` (Iced),
  `leascan.exe <imm...>` (acha `lea/add r32,disp32` e `mov r32,imm` com índices de flag calculados e o call
  seguinte), `dumptool.exe <dmp> <base>`, `xref.exe <elf> <va...>`, `callscan.exe <va...>`, `immscan.exe lo hi`.
- **Vigia de crash — DESLIGADO por padrão**: com ele grudado o jogo fechou três vezes seguidas uns 20 s
  depois de abrir ("event flag ... waited 5s", código 0xff/0xffffffff, 2026-10-27 02:23-02:26). Não
  grude depurador no bb-probe. O randomizer só o inicia se existir `mod_backup\crashwatch.on` (e o exe
  ficou como `crashwatch.exe.desligado`). `mod_backup\crashwatch.exe` (fonte `src\crashwatch.cs`), quando
  ligado, é iniciado pelo randomizer após os patches (`StartCrashWatch`). Ele gruda no bb-probe como depurador
  (`DebugActiveProcess`, `DebugSetProcessKillOnExit(false)`); numa violação de acesso que não seja escrita
  em memória mapeada (o loader usa páginas protegidas para vigiar a memória), grava em
  `mod_backup\crashwatch.log` registradores, cadeia de rbp (o jogo tem frame pointers) e uma varredura
  da pilha com os endereços `eboot+...`, e repassa a exceção (`DBG_EXCEPTION_NOT_HANDLED`), então o
  loader ainda gera o `.dmp`. Termina quando o jogo fecha. Para diagnosticar: reproduzir a morte (ou
  carregar o autosave pós-morte, que passa pelo mesmo caminho) e ler o log.

## Mapa das 78 moedas no mapa do mundo (v2.13, `--coins`, cave 5) — 2026-10-27 23:10

Pedido do usuário: a imagem `Downloads\a1h21qoxzxf01.jpg` (mapa do jogo 1920x1080 com 78 pontos amarelos = as
relíquias/moedas douradas do remake, 79 no total, que liberam a Espada do Dormin) "no lugar do mapa das frutas",
com as moedas aparecendo perto de onde estão. O item 37 (Mapa de árvores frutíferas) deixa de mostrar frutas.

Como o mapa do mundo desenha (engenharia reversa, eboot):
- UI: `CComponentUIBehaviorShadowWorldMap` (strings em arquivo 0x1005A00+). Função de desenho grande em
  `0x4B5400..0x4B6D00` (componente `comp`, dados da UI em `[comp+0xA0]`, projeção `[dados+0x1E0..0x1F0]`, divisores
  600/-600 em `0x1001D0C/0x1001D14`). Listas no componente: `+0xD0` rastro (xyz+byte), `+0xF0` pontos de salvamento
  (12 B, bit 8), `+0x110` colossos mortos (xyz+índice do ícone, bit 4, ícones em `[dados+0x150]`), `+0x130` e `+0x150`
  (12 B, bits 1 e 2 = frutas e lagartos, sprites `[dados+0x110]`/`[dados+0x130]`), `+0x170` índices de grupos de ícones
  (`ShadowMapIconGroup`, 0x30 cada, ícones fixos de 0x20). Máscara de visibilidade `[comp+0x1C0]` (inicial 0xF; □
  cicla 0xF → 0xC colossos+santuários → 0x3 frutas+lagartos → 0 → 0xF; rótulos `UI_TOGGLE_CUR_*` em 0x4B7520).
- Lado do jogo: `cp10::ShadowWorldMapState` (objeto global `0x2EF9EC0`, CP10UI.cpp) com 6 vetores e métodos push:
  `0x498D80` (xyz+flags → +0x18, rastro), `0x498EA0` (xyz+int → +0x58, colossos mortos: posição `entrada+0xB0`,
  índice `+0x28`-1, só com DEAD==0), `0x498FA0` (int → +0xB8, grupos), `0x499060` (12 B → +0x38, pontos de salvamento
  da tabela estática `0x1BBBF10`), **`0x499180` (12 B → +0x78, FRUTAS)**, `0x4992B0` (12 B → +0x98, lagartos).
  `BeginStateUpdate 0x498CB0` zera os vetores; `GetMapState 0x4993F0` copia para a UI. Quem enche é
  **`0x78FA30`** (a cada atualização do mapa): frutas só se `[0x3256464]!=0` (debug "Show Remaining Fruit Trees",
  cvar `0x2FED838`) ou `0x7DCCE0(0x25)` (tem o item 37); lê o gerenciador de frutas `0x7F72B0`/`0x7F72C0`
  (entradas de 0x100 com sub-listas de árvores de 0x20, posição = origem da área `0x7F76D0` − int16) e chama
  `0x499180` por árvore. Lagartos: `[0x3256468]` ou item 38 (0x26), via callback `0x7720A0(0x790D10)`.
- **Cave 5** (`0x1593C10`, 54 B, só com `--coins`): `call 0x7F72B0` em `0x78FEAA` vira `jmp cave5`; a cave faz
  `push rbx/r13`, percorre `COINS 0x3956AC0` (`COIN_COUNT 0x3956AB8` dwords; 12 B por moeda: x, 0, z; máx. 85, folga
  da página de dados depois do fim do segmento 0x3956AA4 e antes dos mailboxes 0x3956F00) chamando
  `0x499180(0x2EF9EC0, &moeda)`, restaura e salta para `0x7900E2` (seção dos lagartos). O `je 0x7900E2` de
  `0x78FEA4` (sem o item 37) vira NOPs: as moedas aparecem sem o item. r12 (contexto do montador) é preservado.
  As moedas usam o ícone e o bit de visibilidade das frutas. `cavedump.exe` mostra a cave.
- **Posições**: `mod_backup\moedas.txt` (recurso do patch): 78 linhas `px py` (centros dos pontos amarelos da imagem,
  extraídos por `scratchpad\dots.exe`, todos ~17x17 px) + `calib ax bx az bz` (mundo x = ax·px+bx, z = az·py+bz; +x =
  leste/direita, +z = sul/baixo: conferido com as posições dos colossos em `entrada+8`/`+0xB0`: Avion leste, Dirge
  oeste, Malus norte z≈-2110, Gaius/Celosia sul). Calibração PROVISÓRIA: origem na seta do santuário (1008,480) =
  mundo (0,0) e 0,25 px/m (`calib 4.0 -4032 4.0 -1920`). Para refinar: captura do mapa do jogo (zoom padrão) com
  os ícones dos colossos mortos visíveis (posições `+0xB0` conhecidas) e/ou a seta do Wander (posição lida ao vivo);
  ajustar `calib` por mínimos quadrados e reaplicar (o randomizer grava a tabela ao iniciar a sessão; `w x z` aceita
  coordenadas do mundo direto).
- **Calibração feita (2026-10-28 00:21)**: o usuário mandou a captura do mapa do jogo (2000x1125, vista padrão) com as
  moedas desenhadas pela calibração provisória. `scratchpad\fitcal.exe <moedas.txt> <captura> 1920 1080 <saída>`
  extrai os ícones desenhados (contornos amarelos ~23x27 px; só os isolados, 42 de 78), faz busca global dos
  parâmetros de `G = alpha·p + beta` (pixel da referência → pixel do jogo) com os 4 sinais por eixo e refina por
  mínimos quadrados: **42 pares, rms 0,5 px**, `alpha=(0,9120, −0,8530)`, `beta=(66,9, 969,9)`. Ou seja, o padrão
  saía **espelhado no eixo vertical** (o montador do jogo nega x e z ao empurrar as frutas — `vxorps` com a máscara em
  `0x12846D0` — e o eixo z do mapa cresce para o norte nas coordenadas da lista). Mapa do jogo (tela 2000x1125):
  `Gx = 0,22800·lx + 986,2`, `Gy = −0,21325·lz + 560,5` (lx, lz = valores gravados na lista/moedas.txt). Linha
  gravada: `calib 4.56880 -4325.51 -4.88476 2628.37` (`moedas.txt` em `mod_backup`, `v25\files`, recurso do patch).
  Seta do santuário (1008,480) ↔ lista (280, 284). `overlay.exe` desenha círculos numerados de uma hipótese sobre a
  captura (`ov3.png` = ajuste final, todos os 78 em cima dos ícones). NÃO conferido no jogo ainda após a calibração.
- Pendências: esconder moedas já pegas (`PlayerCollectibleState`/`SecretRoomCollectibles`, sem RE ainda); ícone
  próprio (hoje é o da fruta); as posições reais dos `Collectible` (objetos de fase) não foram extraídas dos dados —
  o `CollectibleManager` (`0x516310`, cvars `s_Collectible*` em `[this+0x90/0x98/0xA0]`) só tem um nome por
  propriedade (`0x514360`), sem lista de posições.
- Opção "Mapa das 78 moedas (mod)" no lançador (`randomizer_coins`, `coins=` em `randomizer_mods.txt`, arg
  `--coins`) e caixa no patcher (`--no-coins`). Instalado 23:08; NÃO testado no jogo ainda.

## Mods de arquivo pelo lançador (roupa da Mono, som do calango) — 2026-10-27 22:40

Pedido do usuário: as duas caixas no patch e no lançador, ligando/desligando. Como funciona:
- O patch embute `files\mods.zip` (`v25\mods\`: `roupa_mono\materials\shadow\characters\{mono,wander}\_ps4\*.cmat`,
  os **34** materiais que diferem entre `cachedFiles.psarc` original e `cachedFiles_MOD_ROUPAS.psarc`, uns 45 KB;
  `som_calango\sound\shadow\com_sys.sxd`, a gravação do usuário) e o extrai em `mod_backup\mods\<mod>\<caminho
  dentro do psarc>`. A pasta espelha o caminho da entrada no psarc; o lançador monta `nome=arquivo` sozinho.
- `launcher_mod.py` (`FILE_MODS`, `apply_file_mods`): ao clicar JOGAR (antes de abrir o jogo) e no `--play`,
  para cada mod: caixa ligada e `mod_backup\<psarc>.tocbak` ausente → `psarc.exe replace <psarc do jogo>
  mod_backup nome=arquivo...` (anexa os dados, reescreve a TOC, guarda a TOC original no `.tocbak`); caixa
  desligada e `.tocbak` presente → `psarc.exe restore` e apaga o `.tocbak`. **`.tocbak` presente = mod aplicado.**
  `roupa_mono` mexe em `cachedFiles.psarc`, `som_calango` em `pakN.psarc` (12,8 GB; só a TOC e um anexo, rápido).
  A pasta do jogo vem de `"game_dir"` do `launcher_settings.json`. Erros vão para `launcher_mod.log` e o jogo abre
  mesmo assim. Em `cachedFiles.psarc` há 23 KB de folga depois da TOC (TOC 1286954, dados em 1310720) — 34
  entradas de 2 bytes cabem com sobra; o `replace` recusa se não couber ("not enough room").
- Patcher: caixas "Wander com roupa da Mono" e "Som do calanguinho"; `--apply` aceita `--no-roupas`/`--no-calango`;
  desmarcar um mod de arquivo que está aplicado o desfaz na hora; "Remover patch" restaura os dois psarc.
  **Migração** da forma antiga (cachedFiles.psarc inteiro trocado pelo `APLICAR_MOD_ROUPAS.bat`): se o MD5 do
  `cachedFiles.psarc` do jogo é o da cópia com mod (`9EED33EA…`) e `mod_backup\cachedFiles.psarc` é o original
  (`0BE74F94…`), o original volta e o lançador passa a aplicar pela caixa. Feito em 2026-10-27 22:38 no port.
- O `pakN.psarc.tocbak` de 25/10 22:31 (criado pelo primeiro `replace`) é a TOC original: `restore` devolve o pakN
  intocado. Os bats antigos (`APLICAR_MOD_ROUPAS.bat`, `APLICAR_SOM_CALANGO.bat`, `RESTAURAR_ORIGINAL.bat`) seguem
  funcionando, mas o bat de roupas copia o psarc inteiro (sem tocbak): depois dele, rode o patch de novo (migração).
- Testes: `scratchpad\test_filemods.py` (aplica/mantém/desfaz numa cópia do cachedFiles.psarc, MD5 volta ao
  original) e `test_install.py` (`SotC.orig.exe --script test_install.py <launcher_mod.py>`: carrega o lançador e
  aplica `install()` sem abrir a janela; o lançador é PyInstaller onedir — precisa de `_internal\` ao lado, por isso
  uma cópia só do exe não roda). Compilação do patcher: csc com `/r:System.IO.Compression.dll` e
  `/resource:files\mods.zip,mods.zip` (ver "Fontes e ferramentas").

## Som do calango (mod de áudio, fora do randomizer)

`mod_backup\som_calango\`: `com_sys.sxd` modificado (onda 12 = `get_tail` trocada pela gravação do
usuário; `sxdtool.exe replace`), `com_sys.original.sxd`, `APLICAR_SOM_CALANGO.bat` (psarc restore +
replace de `sound/shadow/com_sys.sxd` no pakN). Confirmado no jogo para o calango de cauda preta. O de
cauda brilhante não passa por `get_tail`: no código do pickup (`0x7147A5..0x714AD4`, strings "PICK UP %s",
"GEX TAIL") o ramo `[r15+4]==0` toca `get_tail` por nome (`0xA5FB70(mgr, "get_tail")`) e o ramo com
ganho de atributo só conta estatísticas e dispara o evento "JustCollectedItem"; o som dele vem de outro
lugar (provavelmente `healing`, id 12 do `com_sys`, tocado pelo HUD ao subir a barra). Em 2026-10-27 o
tone 13 (`healing`, byte em 0x1AC do arquivo: onda 0x0B → 0x0C) foi apontado para a onda 12, à espera de
teste (`com_sys.v1_so_get_tail.sxd` é a versão só com get_tail). Mapa: `NAME id → SEQD[id]` (bytecode em
`entrada + 0x10 + offset`, voz `A4 01 NN`) → `tone[NN-1]` (registros de 12 bytes em 0x110; byte 0 = onda).
Bancos comuns: `com_sys` (UI/pickups), `com_amb` (bichos, `gex_*`), `com_obj` (armas), `com_v` (vozes).

## Skins do Zelda: Link no lugar do Wander, Zelda no lugar da Mono, Master Sword — 2026-10-28

Caixa "Skins do Zelda (Link, Zelda, Master Sword)" no lançador (`mod_zelda`, pasta `mod_backup\mods\zelda_skins\`,
~380 MB, por isso NÃO vai dentro do patcher: a caixa aparece sempre que a pasta existe). Mexe em três psarc:
`cachedFiles` (2 normal maps), `pak0` (`wander.cmsh`, `wander_lod1.cmsh` + 24 texturas) e `pakN` (Zelda:
`mono.cmsh`, `mono_lod1`, `mono_lod2` + texturas; Link: texturas incl. os níveis lv1/4/8/12; espada:
`wander_sword.cmsh`, `wander_sword_tsuka.cmsh` + texturas). Estado aplicado em `mod_backup\<psarc>.mods`
(nomes `zelda_skins/<psarc>`, `som_calango`); `psarc.exe` v3 (`restore` + `replace @lista`, cresce a TOC
movendo entradas e o manifesto, `--inplace` grava por cima do espaço original quando cabe, desfaz pelo
`.tocbak.patch`). Marque também "Som do calanguinho": os dois moram no pakN e o lançador reaplica o conjunto.

Como foi feito (ferramentas em `mod_backup\skins\tools\`, Python do Blender 5.2, modelos TOTK do UltraTears em
`D:\zelda`): `romfs_extract.py` tira `.bfres.mc`/`.txtg` do `romfs.bin`; `mesh_codec.exe` descomprime (bat
`RODAR-10.bat`); `bfres_reader.py`/`bfres_to_glb.py` leem BFRES v10; `txtg_decode.py` + `astcenc` decodificam
texturas; `retarget.py link|zelda <scratchpad>` encaixa o personagem no esqueleto do SotC (mapa de ossos por nome,
pesos remapeados, materiais do TOTK agrupados em atlas por material do Wander/Mono, `pack.json`); `retex.py`
gera os `.ctxr` + chunks (`bp_ctxr.py`, `fastimg.py`); `build_chars.py <scratchpad> --install` monta
`mods\zelda_skins` e instala. Link usa a roupa clássica (TOTK `Armor_005`: túnica verde, chapéu, botas; `--naked`
= torso nu, `--no-hat` = sem chapéu); Zelda = `Npc_Zelda_AncientHyrule`.

Formato Bluepoint (`.cmsh`), as 4 pegadinhas que travaram o jogo na tela de loading (sem erro no log):
1. o `i32` depois da contagem de ossos é o tamanho da TABELA DE NOMES (6412 no Wander), não da seção;
2. arquivos comprimidos por mim com o oo2core 2.6 dão "OODLE ERROR" — grave SEM compressão (corpo + rodapé com
   tamanho simples, sem o bit 0x60000000);
3. materiais com esqueleto precisam de número PAR de triângulos (o original tem 48/48 pares);
4. cores de vértice são entrada do shader: vestido "morto" da Mono escurece pelo COL1.R (48) → Zelda usa o
   material vivo `mono_mono_dress1` (COL1 255,0,0,0); pele da Mono tem COL1 alpha 0; cabelo do Wander COL1 alpha 0.
Outros: NRM0/TAN0 = 10/10/10 unorm (`(v-511.5)/511.5`, 2 bits altos 3 / sinal da bitangente), TEX half2 sem
inversão de V, blocos alinhados a 16 bytes, esferas por osso em espaço do modelo (tabela original mantida),
olhos no atlas da pele do rosto (os shaders de olho/dente do SotC esperam outras texturas), cabelo do Link
tingido de loiro (no TOTK a cor vem do shader). `.ctxr`: mips pequenos no arquivo, grandes nos `.chunkN.ctxc`
(PS4 swizzle 8x8, BC1/3/4/5/RGBA8); as texturas por nível do Wander (`wander_cloth_upper_lvN_*` etc.) são
arquivos separados e o jogo começa no lv1.

## Port trocado para o repositório original (AjncSystem 0.60.6-C3) — 2026-10-27 22:50

Pedido do usuário: sair do fork (Lucas-blip-png, `plusNN`) e usar o original `AjncSystem/ShadowOfTheColossus-PC`
(`v0.60.6`, `C1`, `C2`, `C3`; um asset `SotC-PC.zip` de 120 MB por release, mesma estrutura: `SotC.exe`
PyInstaller onedir + `_internal\`, `bin\`, `scripts\`, `launcher\`, `glyphs\`, `sotc.py`, `sotc.ini`, `CHANGELOG.md`).
Feito como o atualizador do port faz (copiar tudo por cima, sem tocar `user\`, `shadow\`, `mod_backup\`):
- Backup do fork plus22 em `mod_backup\port_fork_plus22\` (`SotC.exe` = lançador do fork, `bin`, `_internal`,
  `scripts`, `launcher`, `glyphs`, `sotc.py`, `sotc.ini`, `CHANGELOG.md`, `README.md`, 258 MB). Para voltar ao
  fork: copiar essa pasta por cima do port, `SotC.exe` dela → `D:\SotC-PC\SotC.exe`, e rodar o patch de novo.
- `robocopy /E` de `bin`, `_internal`, `scripts`, `launcher`, `glyphs` do zip e cópia de `sotc.py`, `CHANGELOG.md`,
  `README.md`, `LICENSE`, `SotC.bat`; `sotc.ini` era idêntico. Arquivos só do fork ficaram (`bin\libomp.dll`, licenças).
  O lançador original (5,4 MB; o do fork tem 2,9 MB) foi copiado para `SotC.exe` e o patch `--apply` o guardou
  como `SotC.orig.exe` (fork em `mod_backup\SotC.orig.anterior.exe`).
- Verificação sem abrir janela: `SotC.orig.exe --script scratchpad\test_install.py` → `VERSION 0.60.6-C3`, hooks
  `check/summary/play/play_without_window` instalados, 4 opções. O patch também foi testado numa cópia limpa do zip
  (`scratchpad\dl_upstream\x\SotC-PC`): funciona igual. O atualizador do lançador original aponta para o repositório
  do AjncSystem; depois de cada atualização, rodar o patch de novo (como antes).
- Cuidado: no zip extraído, depois de rodar o patch nele, o `SotC.exe` vira a casca e o lançador verdadeiro fica em
  `SotC.orig.exe` (copiei a casca por cima da casca uma vez por isso).

## Lançador 0.60.6-plus15 (2026-10-27)

O atualizador do port sobrescreve `SotC.exe` (a casca some; `SotC.orig.exe` fica na versão velha): basta rodar
o patch de novo (`SotC-Randomizer-Patch.exe --apply <port>\SotC.exe`), que guarda o lançador anterior em
`mod_backup\SotC.orig.anterior.exe`. O plus15 original cai ao abrir com `KeyError: 'fx_sunshadows'`
(`Launcher.var` faz `APP_DEFAULTS[key]` e a caixa nova "Sombras do sol" não tem padrão); o
`launcher_mod.py` acrescenta `APP_DEFAULTS['fx_sunshadows'] = True` e, por garantia, dá padrão ligado a
qualquer caixa futura sem padrão (log em `launcher_mod.log`). Para inspecionar o código do lançador use o
Python embutido: `SotC.orig.exe --script inspect.py` (ver `scratchpad\inspect_launcher.py` da sessão).

## Fontes e ferramentas

`src\` tem `shell.cs`, `randomizer.cs`, `standalone.cs`, `psarc.cs`, `patcher.cs` (C#, csc do .NET 4,
já no Windows): `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /codepage:65001
/platform:x64 /target:winexe /r:System.Windows.Forms.dll /r:System.Drawing.dll
/win32icon:launcher\sotc.ico /out:SotC.exe shell.cs randomizer.cs` (e `standalone.cs randomizer.cs`
para o SotC-Randomizer.exe; o patcher: `csc /codepage:65001 /platform:x64 /target:winexe
/r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /win32icon:sotc.ico
/out:SotC-Randomizer-Patch.exe /resource:files\SotC.exe,shell.exe /resource:files\launcher_mod.py,launcher_mod.py
/resource:files\SotC-Randomizer.exe,SotC-Randomizer.exe /resource:files\psarc.exe,psarc.exe
/resource:files\crashwatch.exe,crashwatch.exe /resource:files\RANDOMIZADOR.bat,RANDOMIZADOR.bat
/resource:files\TUTORIAL-CLAUDE.md,TUTORIAL-CLAUDE.md /resource:files\mods.zip,mods.zip src\patcher.cs`, rodado em `v25\`). `launcher_mod.py` roda no Python do lançador. Análise estática: `D:\SotC-PC\out\eboot.elf`
(texto em arquivo+0x4000, dados va 0x1604000 = arquivo 0x1608000; ponteiros de dados vêm das
relocações SCE em PT_SCE_DYNLIBDATA), com Python 3.12 embarcado + capstone (ferramentas
`sweep.py`, `fn.py`, `strs.py`, `rela.py`, `jmps.py`, `mem.exe`, `flag.ps1` no scratchpad da sessão
de 2026-10-26). Saves: entradas `[hash FNV-1a][BEEF][tipo][tam][valor]` com checksum FNV-1a.
