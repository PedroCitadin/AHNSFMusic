# Evidências de aceitação — AHNSF Music

A entrega atual é a Fase 0.8 (09/10/2026), documentada ao final deste arquivo. As seções anteriores preservam os resultados históricos e seus limites; não representam a configuração atual de todos os perfis QA.

# Validação da Fase 0.1

Data: 08/10/2026. Ambiente: Windows x64, SDK local 8.0.425. Não é certificação de operação no prédio.

## Critérios de aceitação

| Critério | Resultado e evidência |
|---|---|
| Solução compilar sem erros | Passou: build Release, zero erros e zero avisos |
| Aplicativo iniciar no Windows | Passou: janela real aberta pelo EXE compilado |
| Interface em português brasileiro | Passou: inspeção da janela, mensagens e seletor |
| Selecionar pasta | Passou: seletor nativo abriu e carregou a pasta escolhida |
| Encontrar MP3 em subdiretórios | Passou: teste automatizado e MP3 em `.tools/smoke/sub` na janela real |
| Lista exibir arquivos | Passou: título, artista e duração dos dois MP3 gerados |
| Reproduzir MP3 | Passou no pipeline NAudio/Windows: arquivo silencioso, posição avançando |
| Play/Pause | Passou: pausa observada e retomada da mesma faixa |
| Stop | Passou: saída encerrada, faixa descarregada e posição zerada |
| Próxima e anterior | Passou: alternância A → B e B → A na janela real |
| Volume | Passou: controle alterado de 35% para 22%, persistência observada; valor da engine testado com substituto |
| Progresso refletir reprodução | Passou: posição aumentou em capturas sucessivas |
| Avançar e retroceder | Passou: busca com pausa para 12 s e depois 2 s, refletida no tempo e slider |
| Próxima automática no término | Passou: A terminou e B iniciou sem ação no botão Próxima |
| Persistir diretório ao reiniciar | Passou: fechar/reabrir recuperou `.tools/smoke` e músicas |
| Persistir volume ao reiniciar | Passou: fechar/reabrir recuperou 22%; SQLite também testado entre instâncias |
| Arquivos inválidos não derrubar aplicação | Passou: arquivo inválido na lista; tentativa de reprodução mostrou erro; faixa válida reproduziu em seguida |
| Interface responsiva | Passou nas leituras pequenas e ações observadas; scanner roda em Task.Run. Não foi realizado ensaio com biblioteca grande |
| Testes automatizados | Passou: 10 testes, zero falhas e zero ignorados |
| README atualizado | Criado com execução, arquitetura, dependências e limites |
| PROJECT_STATE atualizado | Atualizado com estado real e continuidade |

## Testes automatizados executados

- Limites da sequência e ausência de repeat implícito.
- Preservação da faixa por caminho na atualização e remoção da faixa desaparecida.
- Play/Pause/retomada, seek, volume e parada no ViewModel com engine substituta.
- Mensagem em português quando carregamento de áudio falha.
- Player vazio não abre saída.
- Descarte repetido é seguro (janela e container DI).
- SQLite: valores padrão, salvamento, reabertura e atualização.
- Varredura recursiva e case-insensitive; metadados inválidos geram aviso; arquivo original intacto.
- Diretório ausente e cancelamento.
- MP3 real de um segundo: título, artista, duração correta, bytes preservados e arquivo liberado para acesso exclusivo.

Resultado TRX: `tests/AHNSF.Music.Tests/TestResults/phase01.trx` (gerado e ignorado pelo Git).

## Ensaios manuais e materiais

Foram gerados dois MP3 silenciosos de 20 segundos com FFmpeg, um na raiz e outro em subpasta, além de um arquivo deliberadamente inválido. Esses arquivos são exclusivamente de QA, em `.tools/smoke`, fora do código distribuído. Não são uma biblioteca pré-carregada do produto. O MP3 de um segundo em `tests/Fixtures` é recurso dos testes.

Foi detectada estimativa de 40 segundos pelo TagLibSharp para um arquivo de 20 segundos. A leitura de duração foi transferida para `Mp3DurationReader`; o teste de regressão passou e a lista real mostrou 00:20 após recompilação/reabertura.

O aplicativo iniciou parado após reabertura, preservando a configuração. A seleção de pasta usada no ensaio permanece nas configurações deste perfil; escolha a pasta real para utilizar o produto.

Ao minimizar a janela durante a reprodução e restaurá-la, a posição avançou de 2 s para 7,85 s: a reprodução continuou sem foco.

## Falhas intermediárias resolvidas

- Download na sandbox sem rede: repetido com execução de rede autorizada; SDK instalado localmente.
- Build inicial com acesso ao perfil `.dotnet` bloqueado: CLI_HOME e cache NuGet direcionados à pasta `.tools`.
- Telemetria de build do Avalonia tentou gravar fora do workspace: opt-out configurado no script de desenvolvimento.
- Build com aplicativo aberto encontrou DLLs bloqueadas: aplicativo fechado e build repetido; execução final sem avisos.
- Aviso de construtor XAML: adicionado construtor padrão para carregamento do recurso.

## Limites e validação ainda necessária

Não foi avaliada a audibilidade, qualidade sonora, ganho ou roteamento na interface USB/mesa hospitalar: os MP3 usados no ensaio eram silenciosos. Não foram simuladas desconexão física da saída, falta de permissão por ACL, execução por muitas horas ou bibliotecas grandes. A seleção/monitoramento de dispositivos pertence à Fase 0.7; produção e operação prolongada exigem as fases de estabilização.

O registro acima descreve a entrega histórica da Fase 0.1. A Fase 0.2 está descrita abaixo.

# Validação da Fase 0.2

08/10/2026, Windows x64, SDK local 8.0.425. Foram preservados os dez testes originais. A stack e a engine de áudio permanecem as mesmas; SkiaSharp 2.88.9 foi referenciado explicitamente para miniaturas, na versão já utilizada pelo Avalonia.

## Critérios e evidências

| Critério | Resultado observado |
|---|---|
| Catálogo persistido em SQLite | Teste real de indexação, gravação/reabertura e migração transacional |
| Carregar músicas indexadas | Repositório reaberto com os mesmos registros; catálogo carregado antes da reconciliação na janela |
| Não reprocessar arquivos inalterados | Contador de leituras e ensaio com 2.000 MP3 confirmaram zero novas leituras de metadados |
| Detectar novos MP3 automaticamente | Teste com FileSystemWatcher real e janela: cópia em subpasta elevou a contagem de 4 para 5 |
| Refletir remoção de MP3 | Teste real e janela: remoção do arquivo de QA retornou a contagem de 5 para 4 |
| Detectar alterações | Testes com mudanças em título/artista/álbum/capa, preservando Id e data de inclusão |
| Monitorar subpastas | Watcher recursivo testado; inclusão/remoção real na subpasta de QA |
| Busca | Quatro casos (título, artista, álbum, nome do arquivo), com caixa/acentos; busca "capa" observada na janela |
| Filtros | Teste combinado; artista AHNSF e álbum Ensaio local selecionados na janela |
| Ordenação | Cinco critérios testados em ambas as direções; controles presentes na janela |
| Exibir capas | PNG incorporado extraído, miniatura limitada a 256 px, exibida na janela; imagem inválida usa fallback |
| Interface responsiva | Varredura/SQLite fora da thread UI, lista virtualizada; ações responderam no ensaio pequeno. Backend medido com 2.000 arquivos |
| Player continuar funcionando | Dez testes originais aprovados; duplo clique, progresso e avanço automático observados; adição durante playback não reiniciou a faixa |
| Preservar configurações 0.1 | Migração testada; perfil real manteve pasta, geometria e volume de 22% |
| Falhas individuais não derrubarem aplicação | MP3/capa inválidos, SQLite inválido e arquivo com escritor aberto cobertos; MP3 inválido visível com aviso na janela |
| Testes automatizados | 42 aprovados, zero falhas/ignorados; integração filtrada: 31 aprovados; regressão filtrada: 10 aprovados |
| Build Release | Zero erros e zero avisos; Debug também passou |
| README | Atualizado com execução, biblioteca, arquitetura, cache e limites |
| PROJECT_STATE | Atualizado para a Fase 0.2 e continuidade |
| Documentação de aceitação | Esta seção preserva a evidência anterior e acrescenta a validação atual |

## Cobertura adicional

Renomeação, inclusive somente caixa; troca de pasta e cancelamento da tarefa anterior; pasta temporariamente indisponível sem perda de catálogo; arquivo em cópia adiado; reindexação forçada para alterações com mesmo tamanho/data; persistência sem duplicação; caminhos normalizados; eventos duplicados; overflow simulado; reconciliação periódica com watcher desabilitado para simular perda de eventos; cache inválido, redimensionamento, invalidação e limpeza restrita; falha de SQLite propagada; sincronização sem recarregar a engine e retomada da faixa pausada quando filtros a ocultam.

Os testes usam MP3 silencioso de um segundo e tags montadas diretamente em cópias temporárias. Não exigem rede, FFmpeg, áudio comercial ou saída sonora real. A engine de reprodução é substituída apenas em testes de lógica; a aplicação usa NAudio real.

Resultados gerados, ignorados pelo Git: `phase02.trx`, `library-integration.trx` e `phase01-regression.trx`, dentro de `tests/AHNSF.Music.Tests/TestResults`.

## Medição e ensaio gráfico

Um ensaio executado com 2.000 MP3 pequenos e silenciosos, 20 artistas e 40 álbuns, mediu indexação inicial de **3.264 ms**, reconciliação inalterada de **226 ms** (zero metadados relidos) e busca de **9 ms**. Esses números são de uma execução local e não são metas de desempenho nem projeções para uma biblioteca hospitalar real. Execuções posteriores registram suas próprias medidas no stdout do TRX.

Na janela real foram observados: catálogo com 4 faixas de QA, capa e placeholders, busca, filtros de artista/álbum, duplo clique, reprodução e progresso; adição/remoção automática em subpasta; mudança do catálogo durante reprodução sem reiniciar a faixa. O player avançou da faixa com capa para Teste local A enquanto a interface era utilizada. O ensaio foi encerrado com o player parado e a janela fechada.

## Limites conhecidos

- Não foi realizado ensaio gráfico de rolagem com milhares de capas distintas; virtualização está explícita e o backend foi medido com 2.000 MP3.
- Falha/overflow foram simulados; não foi provocado overflow real do buffer Windows. Permissões ACL reais e operação prolongada ainda precisam de ensaios.
- Arquivos removidos permanecem como Missing no banco para preservar identidade por caminho; ficam fora da lista e da carga normal de reconciliação. Registros históricos ocupam espaço em disco.
- Tamanho/data são uma heurística; alterações que preservem ambos exigem reindexação forçada. Uma imagem descartada pelos limites usa placeholder.
- Imagens de entrada são limitadas; a limpeza aplica o teto de disco após o lote. O volume transitório durante um lote pode superar o teto.
- O ensaio sonoro usou arquivos silenciosos, sem homologação de audibilidade na interface USB/mesa ou desconexão física.
- A pasta de QA permanece selecionada no perfil; escolher a pasta real para uso.

O registro acima descreve a entrega histórica da Fase 0.2. A Fase 0.3 está descrita abaixo.

# Validação da Fase 0.3

08/10/2026 (horário local), Windows x64, SDK 8.0.425. Os 42 testes anteriores foram preservados sem alterações. Nenhuma dependência adicionada; NAudioEngine e o catálogo da Fase 0.2 mantidos.

## Critérios e evidências

| Critério | Resultado observado |
|---|---|
| Criar playlists | Serviço SQLite e comandos MVVM testados; criação real de “QA — Fase 0.3” na janela |
| Editar playlists | Nome/descrição/padrão testados; descrição editada e salva na janela |
| Excluir playlists | Cascade limitado às associações, outras playlists/Tracks/bytes MP3 preservados em testes; confirmação e cancelamento observados na janela |
| Adicionar/remover músicas | Seleção múltipla, duplicatas intencionais e remoção por PlaylistItemId testadas; três ocorrências adicionadas pela interface |
| Reordenar e persistir | Transação com validação do conjunto completo; botões e operação MVVM testados; arraste real inverteu dois IDs, ordem mantida na reabertura |
| Reproduzir playlists | Carregamento por ID e item específico testados; NAudio real reproduziu playlist silenciosa com avanço automático |
| Visualizar/manipular fila | Ocorrências próprias, origem/posição, inserir próxima/final, remover/limpar testados; fila com quatro ocorrências exibida após inserção durante reprodução |
| Shuffle | Atual mantém identidade/posição; passagem sem repetição prematura, histórico anterior/próxima e combinação com Repeat testados; ativação real conservou progresso |
| Repeat em três modos | Eventos de término do serviço testados nos três modos e distinção do avanço manual; repetir música observado reiniciando a mesma ocorrência na engine real |
| Playlist padrão | Índice único parcial e atualização transacional testados, inclusive concorrência e reabertura; estrela e persistência observadas na janela |
| Músicas ausentes | Referência preservada, aviso, início misto/inteiramente ausente, remoção durante reprodução e reintrodução com ID estável testados |
| Player/biblioteca anteriores | 42 testes anteriores aprovados; seleção manual/filtros/pausa continuam cobertos |
| Migração preservar dados | Settings, Tracks e LibraryMigrations versão 1 preservados; versão 2 idempotente, foreign_key_check sem violação |
| Interface responsiva | SQLite fora da UI, listas virtualizadas; ações responderam no ensaio gráfico pequeno, inclusive durante áudio |
| Testes automatizados | 98 aprovados, zero falhas/ignorados; 56 novos casos |
| Integração SQLite separada | PlaylistTests: 19 aprovados |
| Regressão separada | PlayerTests + LibraryTests + PerformanceTests: 42 aprovados |
| Debug/Release | Restore aprovado; ambos os builds com zero erros e avisos |
| Documentação | README, PROJECT_STATE e esta matriz atualizados |

## Decisões de reprodução e persistência

PlaylistId, PlaylistItemId, TrackId e QueueItem.Id têm funções distintas. Duplicatas compartilham o Track imutável resolvido, mas conservam IDs independentes de ocorrência. Playlists/PlaylistItems têm índices e chaves estrangeiras ativadas nas conexões; excluir playlist não exclui Tracks nem arquivos. Alteração inválida de ordem ou referência gera rollback. Metadados permanecem somente em Tracks.

PlaybackService implementa IPlaybackService para carregar por ID, iniciar/parar, navegar e consultar estado/fila sem depender de botões. IPlaylistService consulta a padrão. Não há agendador. A composição Windows despacha eventos de término e rejeita callbacks de sessões antigas.

A fila ativa e o histórico são mantidos em memória. Ao reiniciar, a biblioteca fornece uma fila inicial e o áudio permanece parado; a fila anterior não é restaurada. Apenas playlists/itens/ordem/padrão e preferências Shuffle/Repeat persistem. Editar/excluir uma playlist não altera uma fila já carregada. Alterar itens futuros não recarrega a engine; remover a ocorrência atual ou limpar interrompe o áudio.

Shuffle mantém ordem manual exibida/salva e percorre uma rota separada de IDs, incluindo duplicatas. Anterior/Próxima usam histórico da sessão; ocorrências indisponíveis são ignoradas. Repetir fila reutiliza o percurso completo, conservando o embaralhamento; repetir música se aplica ao término automático, enquanto Próxima manual tenta avançar. A seleção explícita pode repetir intencionalmente uma ocorrência. Limites manuais sem destino apenas informam o limite; término automático sem Repeat para.

## Ensaios e resultados

`scripts/dev.ps1 Validate` executou restore, Debug, Release e suíte completa. Resultados em `tests/AHNSF.Music.Tests/TestResults/phase03.trx`; filtros separados em `playlist-integration.trx` e `phase03-regression.trx`. Testes usam fixture silenciosa e arquivos temporários, sem internet, FFmpeg ou MP3 comerciais.

Um teste SQLite adicionou/reordenou/reabriu uma playlist com 2.000 ocorrências de duas faixas. O último ensaio do backend de biblioteca com 2.000 MP3 registrou 2.452 ms de indexação inicial, 146 ms de reconciliação inalterada (zero metadados relidos) e 5 ms de busca. Medições locais, não metas nem homologação de desempenho hospitalar.

Na janela real: criação/padrão, adição de duplicata, arraste, reprodução de playlist, avanço automático entre ocorrências, ativação de shuffle sem reinício, inserção como próxima durante áudio e visualização da fila independente. Progresso observado após inserção: 6,15 s e depois 10,8 s, conservando a faixa. Repeat música: seek perto de 20 s seguido de retorno a 3,6 s, mesma ocorrência/posição 4. Reabrir preservou três itens da playlist, IDs/ordem, estrela, volume 22%, shuffle e Repeat música; iniciou parado. Descrição editada/salva e confirmação de exclusão cancelada. Ensaio terminou com janela fechada.

Falhas intermediárias resolvidas: assinatura do arraste ajustada à API Avalonia instalada; avisos do analisador xUnit corrigidos. Um teste novo expôs consulta ainda ativa na limpeza SQLite: fechamento assíncrono agora cancela e aguarda tarefas de pesquisa, seleção, operações de playlists e salvamento. Outro teste usava busca “A”, que também encontrava Artista/Álbum; o cenário de ocultação passou a usar o nome exato do arquivo. A validação final passou.

## Limites

- Ensaios gráficos usam quatro arquivos de QA e playlist pequena; não foi medida rolagem gráfica prolongada com milhares de capas distintas. A playlist grande foi verificada pelo SQLite, não por navegação gráfica extensa.
- Exclusão efetiva, remoção múltipla, Repeat fila/desativado e ausência de arquivo estão cobertos por testes de domínio/MVVM; nem todas essas combinações foram repetidas na janela real.
- Músicas de outras pastas mantêm referências; metadados seguem a reconciliação da pasta atualmente monitorada. Existência é verificada ao resolver playlists/filas; acesso/decodificação final continua sendo verificado pela engine.
- Permanecem pendentes operação por muitas horas, ACL reais, overflow físico e audibilidade/roteamento/desconexão USB na mesa hospitalar. MP3 silenciosos não comprovam som audível.
- Perfil de QA mantém pasta .tools/smoke, playlist padrão “QA — Fase 0.3”, volume 22%, shuffle ativado e repetir música. Ajustar antes do uso real.

O registro acima descreve a Fase 0.3. A Fase 0.4 está descrita abaixo.


# Validação da Fase 0.4

08/10/2026 local, Windows x64, SDK local 8.0.425. Sem novas dependências, mudanças de MP3, auto-start, bandeja, fade, dispositivo, publicação ou fases posteriores.

## Critérios e evidências

| Critério | Resultado |
|---|---|
| Criar, editar e excluir regras | Passou: SQLite e comandos MVVM; criação/salvamento na janela; confirmação/cancelamento de exclusão na janela |
| Dias/horários/playlist/volume/prioridade | Passou: editor e validação de nome, playlist, flags, horários distintos, volume finito 0–100 e prioridade inteira 0–1000 |
| Persistência e migração 3 | Passou: reabertura/edição/enable/delete/rollback; Settings, Tracks, Playlists e PlaylistItems conservadas; Manual padrão |
| Segunda a sexta e sábado | Passou: relógio determinístico e dias independentes |
| Limites de horário | Passou: início inclusivo, fim exclusivo, antes/depois, madrugada e dia de início |
| Iniciar/parar e inicialização no meio da janela | Passou: domínio com relógio controlado; ativar Automático em janela vigente iniciou NAudio real |
| Sobreposições | Passou: prioridade, início efetivo, menor ID, troca e retomada da regra menor |
| Manual/Automático e troca de modo | Passou: modo persistido, Manual não inicia/para, Automático reconcilia; Manual conservou reprodução na janela real |
| Intervenção e suspensão temporária | Passou: pause mantido em ciclos; retoma somente na mudança de vencedora ou ativação explícita; janela real mostrou Suspenso |
| Próxima transição/status/avisos | Passou: cálculo ignora limites perdedores; GUI mostrou modo, regra, playlist, horário de parada e visão semanal com lacunas |
| Não reiniciar mesma regra | Passou: dez avaliações preservaram IDs, faixa, cargas e posição 23 s; volume editado sem recarga |
| Sessão contínua | Passou: término percorre e repete a fila com Repeat Off/Track/Queue preservado; GUI avançou de Capa para Teste A |
| Volume | Passou: regra 62%/77%/80% por engine substituta; GUI aplicou 35%, restaurou 22%; MVVM não persistiu volume agendado |
| Vazias/removidas/indisponíveis/falhas de áudio | Passou: avisos e parada, tentativa limitada a uma por minuto; referência nula conserva regra após exclusão de playlist |
| Concorrência e encerramento | Passou: 16 avaliações concorrentes carregam uma vez; intervenção/cancelamento interrompem carga pendente sem iniciar áudio nem substituir fila manual |
| Mudanças do calendário/relógio/fuso e retomada | Passou em domínio: saltos para frente/trás, vários dias, fuso alterado, estado após período sem avaliações e DST com lacuna/ambiguidade |
| Janela minimizada | Passou em ensaio real: sessão continuou; restaurar mostrou Tocando com progresso e volume agendado |
| Regressão | Passou: 98 testes anteriores preservados; filtro isolado 98/98 |
| Builds e testes | Restore, Debug/Release sem erros/avisos; 155/155 xUnit, zero ignorados; filtro SQLite 24/24 |
| Documentação | README, PROJECT_STATE e esta aceitação atualizados |

## Testes e resultados

`scripts/dev.ps1 Validate` passou. TRX em `tests/AHNSF.Music.Tests/TestResults/phase04.trx`. São 98 casos anteriores e 57 novos em ScheduleTests. Filtros adicionais: `phase04-regression.trx` (98), `phase04-sqlite.trx` (24: 19 PlaylistTests e cinco cenários de persistência/migração de programação). Os testes usam relógio injetável e arquivos temporários, sem depender do horário real.

A primeira execução simultânea dos dois filtros causou IOException no teste antigo que abre Fixtures/silence.mp3 com FileShare.None: outro processo de testes lia a mesma fixture. O filtro de regressão foi repetido sequencialmente e passou 98/98. Uma repetição posterior da suíte também revelou disputa entre coleções xUnit dentro do mesmo processo (PerformanceTests copiando a fixture durante o acesso exclusivo). TestAssembly.cs desabilita a paralelização entre coleções; os cenários explícitos de concorrência continuam usando tarefas simultâneas. Executar também processos de teste sequencialmente. A validação completa foi repetida com essa configuração. Não foram alterados os arquivos dos 98 testes anteriores.

Testes cobrem os cenários 1–35 solicitados por agrupamentos de domínio/SQLite/MVVM, incluindo madrugada/DST, modos, ausência/erro, repetição, concorrência e volume. Os cenários 36–38 são os testes anteriores de player/biblioteca/playlists, preservados na regressão. “Retomada” automatizada significa salto do relógio entre avaliações; não envolve suspender fisicamente o Windows.

## Ensaio gráfico realizado

Foi criada “QA — Fase 0.4 (silêncio)”, dias úteis 00:00–23:59, playlist QA — Fase 0.3, volume 35%, prioridade 0. Manual conservou player parado. Ativar Automático às 21:47 iniciou a playlist e mostrou horário de parada 23:59. O player avançou de Capa de validação para Teste local A. Pause manual às 21:48 mostrou Suspenso, restaurou 22% e conservou posição 0,6 s depois de vários ciclos. Ativar Automático retomou; a janela foi minimizada e restaurada, com reprodução/progresso ativos. Entrar em Manual não interrompeu o áudio e restaurou o volume manual.

A regra foi desativada; abrir/cancelar confirmação conservou seus dados. Depois de fechar/reabrir o build final, a janela exibiu Manual/Parado, regra Desativada, volume 22%, sem próxima transição e sem autoplay. A versão final usa rótulos em português e marca vencedora somente no dia da ocorrência atual na visão semanal. Logs reais registraram inicialização, mudança de modos, início da regra e suspensão sem mensagens por ciclo. Aplicativo fechado ao terminar.

## Política e limites

- Regras controlam o player somente em Automático explicitamente persistido. A atualização inicia em Manual; reiniciar em Automático executa a vencedora vigente.
- Intervenção manual suspende até mudar ID/início efetivo/playlist da vencedora, incluindo lacuna; suspensão só em memória. Ativar Automático novamente retoma imediatamente. Entrar em Manual conserva áudio e libera repetição/volume agendados.
- Avaliação independente da UI a cada 5 s; inicialização e ações da tela reavaliam imediatamente. Início/fim, clock jumps, fuso e retomada entram no ciclo seguinte. Atraso pode incluir um ciclo e tempo de banco/carga/indisponibilidade do sistema; não é execução em tempo real rígido.
- DST: limite em lacuna avança ao primeiro minuto válido; hora repetida usa primeiro início/último fim. Janelas colapsadas são omitidas no dia. Política testada com fuso sintético, sem alterar o Windows.
- Playlist removida deixa regra com referência nula; quando vencedora, avisa e para, sem fallback silencioso. Itens editados não reescrevem a fila já carregada. Repetição de sessão mantém preferências do usuário.
- Não houve ensaio físico de suspensão/retomada do computador, mudança real do relógio/fuso, ACL, desconexão USB, audibilidade ou operação por muitas horas. MP3 de QA são silenciosos; não comprovam homologação na mesa hospitalar.
- Perfil final: modo Manual; regra de QA desativada; pasta .tools/smoke, playlist padrão QA — Fase 0.3, volume 22%, Shuffle ativado e Repeat música. Ajustar antes do uso real.
- Nenhuma funcionalidade da Fase 0.5 foi iniciada (registro histórico do fechamento da Fase 0.4; a Fase 0.5 está descrita abaixo).

O registro acima descreve a Fase 0.4. A Fase 0.5 está descrita abaixo.

# Validação da Fase 0.5

08/10/2026 local, Windows 11 x64, SDK local 8.0.425, monitor 2560×1440 a 100%. O Codex iniciou a fase (22:05–22:20) sem documentá-la; o Claude diagnosticou o estado encontrado (Release e 155/155 aprovados, Debug bloqueado pelo aplicativo aberto), preservou esse trabalho e concluiu a fase. Sem novas dependências, sem mudanças em serviços, SQLite, fila, player, agendador ou MP3; nenhuma funcionalidade da Fase 0.6.

## Critérios e evidências

| Critério | Resultado |
|---|---|
| Design system | Passou: tokens de cor, tipografia, espaçamento, ícones e estilos reutilizáveis em DesignSystem.axaml; estados hover/pressed/disabled/foco; modelos compartilhados |
| Tema escuro e contraste | Passou por cálculo WCAG: texto 15,8:1, secundário 7,6:1, destaque 9,6:1, aviso 8,1:1, perigo 6,9:1; desabilitado 3,6:1 (isento) |
| Layout principal | Passou na janela real: sidebar com seção ativa, cabeçalho com modo/automação, conteúdo em views, player inferior permanente |
| Painel | Passou na janela real: música atual, playlist/origem, estado “Reproduzindo/Parado”, modo, regra prevista, próxima transição, encerramento, totais; alerta coberto por teste MVVM |
| Biblioteca | Passou: limpar busca/filtros e contagens por teste MVVM; seleção múltipla, contagem de selecionadas e “Adicionar à playlist” pela janela real; virtualização preservada |
| Playlists | Passou: criação pelo editor real, padrão indicada, ações ocultas sem seleção, estados vazios, reprodução real pulando MP3 inválido; remoção com confirmação por teste MVVM |
| Arraste | Parcial: lógica de reordenação coberta pelos testes anteriores; indicador visual implementado, mas não observado (UI Automation não executa arraste) |
| Fila | Passou: confirmação de limpar exibida e cancelada na janela real; remover/limpar exigem confirmação por teste MVVM |
| Programação | Passou na janela real: regra criada, próxima transição exibida, grade semanal com Hoje, regra desativada mostrada como Desativada |
| Português e tooltips | Passou por inspeção: rótulos, mensagens, tooltips e nomes de acessibilidade em português |
| Teclado e foco | Parcial: Ctrl+1–6, Ctrl+F e Esc implementados; foco visível estilizado; percurso completo por Tab não ensaiado |
| 1366×768 e 1920×1080 | Passou em capturas: 1366×728 (área útil com barra de tarefas) e 1920×1040, todas as seções sem cortes; modo compacto de altura ativo em 1366 |
| Regressão 0.1–0.4 | Passou: 155/155 casos anteriores em filtro separado |
| Builds e testes | Passou: Validate com Debug/Release 0 erros/0 avisos e 161/161 testes, zero ignorados |
| Documentação | README, PROJECT_STATE e esta seção atualizados |

## Testes e resultados

`scripts/dev.ps1 Validate` passou (phase05.trx, 161 casos). Executados em sequência depois: filtro de regressão `FullyQualifiedName!~PresentationTests` 155/155 (phase05-regression.trx) e `PresentationTests` 6/6 (phase05-presentation.trx). Os seis casos novos cobrem navegação com uma única seção ativa sem afetar o player, contagem de resultados e limpeza de pesquisa, textos de seleção, confirmação obrigatória para limpar/remover da fila e remover de playlist, alertas de pasta indisponível e apresentação de itens ausentes.

## Ensaio gráfico realizado

O aplicativo Debug foi conduzido por UI Automation (sem mouse/teclado físico) e capturado em `docs/evidence/phase05/`. O perfil real do Windows não continha dados do aplicativo (o perfil QA das fases anteriores estava no ambiente isolado do Codex); foi criado um perfil novo apontando para `.tools/smoke`. Sequência: playlist “QA — Fase 0.5” criada, quatro músicas selecionadas e adicionadas, playlist reproduzida (o MP3 inválido foi pulado e “Capa de validação” tocou com progresso), painel mostrou música/origem/estado, “Limpar fila…” abriu a confirmação e foi cancelado, player parado, regra “QA — Fase 0.5 (silêncio)” seg–sex 08:00–18:00 criada (próxima transição sex 09/10 08:00) e depois desativada. Janela fechada ao final em modo Manual.

## Limites

- Arraste com mouse real, percurso completo por Tab e leitores de tela não foram ensaiados. A sobreposição de confirmação bloqueia o mouse, mas não prende o foco do teclado.
- Capturas usaram MP3 silenciosos e quatro arquivos; rolagem com milhares de capas e audibilidade na mesa continuam pendentes.
- Não houve avaliação com operadores do hospital.
- Perfil final: pasta `.tools/smoke`, playlist “QA — Fase 0.5”, regra QA desativada, modo Manual, volume 35%. Ajustar antes do uso real.

O registro acima descreve a Fase 0.5. A Fase 0.6 está descrita abaixo.

# Validação da Fase 0.6

08/10/2026 local, Windows 11 x64, SDK local 8.0.425. Estado encontrado: branch `master` limpo com o commit inicial, 161/161. Sem novos pacotes: bandeja pelo `TrayIcon` do Avalonia, registro por `Microsoft.Win32.Registry`, pipe/ACL e P/Invoke do próprio .NET/Windows. Nenhuma funcionalidade da Fase 0.7 (dispositivo USB, fade, instalador, publicação) foi iniciada.

## Critérios e evidências

| Critério | Resultado |
|---|---|
| Inicialização automática | Passou em software: entrada única entre aspas com `--autostart`, remoção, duplicatas, caminho com espaços, entrada removida/alterada/desativada pelo Windows e falha do registro (registro falso). Pendente: ativação no HKCU real e logon real (exige consentimento) |
| Inicialização minimizada | Passou: política testada; app real com `--autostart` iniciou oculto, sem janela, serviços e agendador carregados; em Automático com regra vigente iniciou a regra ~0,6 s após o início |
| Bandeja | Passou na janela real: ícone com dica de estado; menu Abrir, estado, Reproduzir/Pausar, Próxima, Modo, Sair refletindo o player; Reproduzir pela bandeja |
| Fechar para a bandeja | Passou: aviso do primeiro fechamento; depois oculta diretamente; sem bandeja nunca oculta (teste) |
| Sair encerra corretamente | Passou: confirmação com música/Automático; serviços encerrados em 17–29 ms; processo terminou; ícone removido; perfil salvo |
| Segunda instância restaura a primeira | Passou: nova execução saiu com código 0, um só processo, janela (oculta ou nunca exibida) restaurada e em primeiro plano; comando inválido no pipe ignorado (teste) |
| Recuperação limitada | Passou em domínio: 5/15/30 s e esgotamento (tentativas em 0, 5, 20 e 50 s), falha permanente sem nova tentativa, cancelamento por intervenção, Manual sem recuperação, fim da janela sem reinício. Pendente: falha física de USB |
| Suspensão/retomada | Passou em domínio: retomada dentro do expediente recupera; às 18:00 não toca; salto de relógio sem evento também recupera. Pendente: suspensão física |
| Agendador consistente | Passou: 57 testes de programação anteriores inalterados e aprovados |
| Preferências persistem | Passou: AutoStartEnabled, StartMinimized, CloseBehavior, CloseNoticeShown salvos sem alterar volume, Shuffle, Repeat, pasta, playlists e modo |
| Foco do modal | Passou na janela real: foco inicial em Cancelar; Tab/Shift+Tab circulam só nos botões; Ctrl+2 bloqueado; Enter em Cancelar não confirma; Esc cancela e devolve o foco |
| Regressões | Passou: 161/161 anteriores em filtro separado |
| Builds e testes | Passou: Validate com Debug/Release 0 erros/0 avisos e 207/207 |
| Documentação | README, PROJECT_STATE e esta seção atualizados |

## Testes e resultados

`scripts/dev.ps1 Validate` passou (`phase06.trx`, 207 casos); a suíte Debug foi repetida duas vezes sem falhas. Em sequência: regressão `FullyQualifiedName!~OperationsTests` 161/161 (`phase06-regression.trx`) e `OperationsTests` 46/46 (`phase06-operations.trx`). Os 46 casos cobrem os itens 1–27 solicitados: registro (falso) e persistência do autostart, caminhos com espaços, alteração externa, executável não suportado, falha do registro, política de início minimizado e de fechamento (incluindo bandeja indisponível e desligamento), ações da bandeja sobre o mesmo player, encerramento ordenado e idempotente, instância única, pipe de ativação, mutex abandonado, recuperação transitória/permanente/progressiva/limitada/cancelada, Manual, fim da janela, retomada dentro e fora do expediente, detecção de salto de relógio, preferências, diagnóstico e modal. Os itens 28–30 são os 161 testes anteriores. Nenhum teste toca o registro real, dispositivos USB ou a bandeja real.

## Ensaio no aplicativo real

Debug conduzido por UI Automation e mouse simulado sobre o ícone da bandeja; capturas em `docs/evidence/phase06/`. Sequência: Configurações e diagnóstico; fechar → aviso (foco/Tab/Esc/Enter verificados) → minimizar para a bandeja; segunda execução restaurou; menu da bandeja parado e reproduzindo; Sair com música → confirmação → encerramento; “iniciar minimizado” + `--autostart` em Manual (oculto, sem áudio) e em Automático com regra QA temporária 23:00–23:59 (oculto, regra iniciada); fechar com “Encerrar o aplicativo” e música → confirmação; capturas 1366×728 e 1920×1040; encerramento forçado do processo e reabertura normal. A regra temporária foi excluída e o perfil voltou a Manual, fechar→bandeja, iniciar minimizado desativado. O registro `HKCU\…\Run` desta máquina não foi alterado (conferido: nenhuma entrada AHNSF).

## Limites e homologação física pendente

- Ativar a inicialização automática no registro real e entrar no Windows; desativar pelo Gerenciador de Tarefas.
- Suspender/hibernar e retomar o computador dentro e fora do expediente.
- Desconectar/reconectar a interface USB durante reprodução automática (a política 5/15/30 s foi verificada só com falhas simuladas).
- Reiniciar o Explorer com o app na bandeja (o comportamento do ícone depende do Avalonia; uma nova execução sempre restaura a janela).
- Sem reinício automático no modo Manual (decisão de segurança). Falha que pare o áudio sem nenhum aviso do Windows/NAudio não é detectada (não há monitor de progresso).
- MP3 de QA silenciosos; operação prolongada e audibilidade continuam pendentes.

# Validação da Fase 0.7

Data: 09/10/2026. Windows x64, SDK local 8.0.425, NAudio 2.2.1 existente. Escopo autorizado: dispositivos de áudio; Fase 0.8 não iniciada.

## Continuidade, arquitetura e validação

Inspecionados PROJECT_STATE, README, este documento, solução, Git, player, recuperação, bandeja, energia e agendador. Baseline da entrega Claude 0.6: restore e builds aprovados, **207/207 testes**. Working tree já continha a 0.6 não commitada; nenhum reset/clean/commit foi executado. As fontes dos 207 testes anteriores permanecem sem reescrita.

Core ganhou contratos/status/seleção e AudioDeviceService serializado. Audio usa MMDeviceEnumerator/IMMNotificationClient para endpoints render e WASAPI compartilhado por ID. NAudioEngine recebe factory testável e mantém somente uma saída. Monitor combina notificações coalescidas com reconciliação de 30 s; não inicia música. ScheduleCoordinator existente recebe disponibilidade, cancela cargas, acorda seu único worker e decide recuperação com nova avaliação da vencedora. App compõe uma engine compartilhada; Settings/alertas/diagnóstico usam o serviço. Preferência AudioDevice é opcional no JSON existente; sem migração SQLite ou pacotes novos.

Validação final sequencial via scripts/dev.ps1: **restore aprovado; Debug e Release: zero erros/avisos; 249/249 aprovados, zero falhas/ignorados**. Filtros sequenciais: **207/207 regressão** e **42/42 dispositivos/engine/integração**. TRX: phase07.trx, phase07-regression.trx, phase07-devices.trx em tests/AHNSF.Music.Tests/TestResults (ignorados pelo Git). VSTest no sandbox ficou antes da descoberta; execuções autorizadas fora dele concluíram normalmente. Falhas intermediárias de assinatura IWavePlayer foram corrigidas antes da validação final.

## Critérios e cobertura

| Critério | Evidência |
|---|---|
| Enumeração/padrão/seleção/ID estável (1–5) | AudioDeviceTests: DiscoveryListsStableIdsAvailabilityAndDefault, SelectionUsesChosenEndpoint, ReorderedEnumerationNeverChangesSpecificIdentity, SqlitePersistsSelectionAndAllExistingPreferences, OldSettingsJsonDefaultsToWindowsDefaultWithoutLosingFields; lista real USB/HDMI/indisponíveis conferida |
| Ausência/desconexão/reconexão/IDs diferentes (6–10) | MissingOrDisabledSelectionDoesNotFallBack, DisconnectDuringPlaybackStopsAndKeepsSelection, SameIdReconnectRestoresAvailabilityWithoutPlaying, SameNameDifferentIdNeverRecovers; monitor com notificação/reconciliação simulado |
| Troca/posição/volume/falha/sem fallback/padrão (11–16) | AudioOutputEngineTests: SwitchPreservesPositionVolumeAndStateWithoutDuplicateOutput (tocando e pausado), SwitchFailureReleasesResourcesAndBlocksAllLoadsUntilReapplied, FailedInitDisposesCandidateAndReader, DisconnectDoesNotAdvanceQueueAndCannotUseDefaultFallback; WindowsDefaultChangeHasConservativePolicy; troca real pausada USB→HDMI manteve 4,8 s e 22% |
| Manual/Automático/dentro/fora/intervenção/cancelamento (17–22) | AudioDeviceRecoveryTests: AutomaticWaitsWhileMissingAndRecoversSameIdInsideWindow, ManualReconnectionNeverStarts, ReconnectionAfterWindowNeverStarts, ReconnectionPreservesPauseAndStopInterventions, DifferentIdNeverStartsDespiteSameName, ReconnectedDriverFailureUsesExistingBoundedRecovery, OperatorCanCancelRecoveryAfterReconnect, SwitchingOutputIsInterventionAndPreservesPlaybackQueueAndVolume, ShutdownCancelsDeviceDrivenRecovery, DisconnectCancelsPendingPlaylistLoadBeforeItCanStart |
| Duplicação/concorrência/descarte (23–25) | DuplicateSnapshotsDoNotSwitchOrPublish, CancelledSelectionDoesNotTouchResources, MonitorHandlesNotificationsAndPeriodicReconciliationAndDisposes, ConcurrentDisposeWaitsForTheSameCleanup, ConcurrentReconnectEvaluationsStartOnlyOneSession, StopAndDisposeReleaseEveryOutputOnceAndKeepVolume |
| Regressão player/agendador/0.6 (26–28) | 207 testes anteriores passaram em filtro separado; NaturalEndStillAdvancesAndOldOutputsCannotAdvanceNewSession testa avanço/generação da nova engine; integração SQLite/PlaybackService/coordenador usa relógio e endpoints falsos |
| Diagnóstico/erro de enumeração | ViewModelShowsMissingDeviceAndPersistsExplicitApply, DiscoveryFailureBlocksAndReportsError, InvalidPersistedModeCannotRouteToAnAlternative, MissingFriendlyNameDoesNotHideTheEndpoint; textos e saída efetiva observados na janela real |

## Decisões operacionais

- ID Windows é a identidade. Mesmo nome com ID novo nunca recupera automaticamente. Seleção ausente é conservada e não redireciona áudio.
- O padrão é resolvido no início e fixado durante a sessão. Mudança do padrão interrompe e exige Aplicar seleção. Reiniciar resolve o padrão atual.
- Manual não reinicia sozinho. Automático só retoma a vencedora atual dentro da janela, sem suspensão manual. Monitor não contém um segundo mecanismo de recuperação; ausência conhecida aguarda dispositivo, falha transitória do driver segue 5/15/30 s existentes.
- Troca manual chama a política de intervenção; em sessão agendada o volume manual é restaurado pela política 0.6 antes da troca. Engine conserva o estado/posição/volume resultante quando possível. Falha libera recursos e para; reaplicação necessária. Desconexão descarrega a faixa, portanto retorno automático recarrega a playlist vigente.
- Nenhuma mudança do padrão Windows/volume global/registro real, escrita MP3, serviço online ou porta de rede. Ganho somente no AudioFileReader.

## Ensaio gráfico e limites

Aplicativo Debug real: abertura Manual/Parado sem autoplay; enumeração identificou Alto-falantes (WJK USB Audio) como padrão, HDMI ativos e endpoints indisponíveis. Seleção USB específica aplicada. MP3 silencioso Capa de validação avançou até 3,68 s e pausou em 4,8 s, volume 22%; diagnóstico mostrou USB como saída efetiva. Troca manual para 2 - 24G2W1G3- (AMD High Definition Audio Device) manteve mesma faixa, pausa, posição 4,8 s e volume 22%, com novo endpoint em uso. Parar descarregou a faixa. Encerramento ordenado salvou configurações/liberou serviços em 24 ms.

A reabertura confirmou seleção específica HDMI persistida e estado Manual/Parado sem autoplay. Ao terminar, a seleção foi restaurada para Padrão do Windows (USB atual), e a preferência fechar→bandeja foi restaurada. Aplicativo deixado aberto, Manual/Parado, volume 22%; nenhuma regra foi ativada pelo ensaio.

A primeira enumeração real expôs COM 0xE000020B ao ler FriendlyName de endpoint NotPresent: corrigida leitura por endpoint com nome substituto contendo ID, teste adicionado e validação completa repetida. Nomes ausentes não escondem os dispositivos saudáveis.

A ferramenta não expôs o menu da bandeja. Foi usado o caminho existente Configurações→Encerrar aplicativo→fechar (mesmo encerramento ordenado) antes de recompilar, limitação comunicada ao usuário. Alguns estados de janela eram capturados antes da conclusão da ação; observações posteriores confirmaram os controles. Janela maximizada para conferir o player inferior. Este ensaio não substitui a validação anterior do menu da bandeja.

Não houve retirada física do USB, alteração real do padrão, audibilidade na mesa hospitalar, suspensão física, reinício do Explorer, mudança de registro de autostart ou operação prolongada. Desconexão/retorno/horários/intervenções/conflitos estão cobertos em software com dispositivos simulados. Homologação física permanece pendente; não declarar certificação hospitalar.

## Git e transferência

Branch master, único commit a958cd3 (commit inicial). Alterações não commitadas da 0.6 foram preservadas, junto às novas alterações 0.7; nenhum commit automático. Ler PROJECT_STATE/README/ACCEPTANCE e fonte; comparar git status/diff sem descartar arquivos. SDK local e scripts/dev.ps1; processos de testes sequenciais; encerrar por Sair na bandeja antes de recompilar. Preservar Settings/migrações/recuperação única/estilos compartilhados. Este era o estado ao concluir a 0.7; a autorização e a entrega 0.8 estão registradas abaixo.

# Validação da Fase 0.8

Data: 09/10/2026. Logs, histórico operacional e diagnóstico implementados no repositório existente, sem novas dependências, serviços online, commits ou início da 0.9. Working tree anterior das fases 0.6/0.7 preservado; 249 testes de baseline aprovados antes das mudanças.

## Build e testes finais

SDK local 8.0.425 e cache local de pacotes. `scripts/dev.ps1 Validate`: restore aprovado, Debug e Release **0 erros/0 avisos**, **285/285 testes**, 0 falhas/ignorados (22 s). Restore/Build/Test foram executados também pelas operações do script; processos de testes sempre sequenciais. VSTest precisou da execução autorizada fora do sandbox, como na 0.7.

Filtros separados contra o código final: **36/36** históricos/integração (7 s) e **249/249** regressão (15 s). TRX em `tests/AHNSF.Music.Tests/TestResults/phase08.trx`, `phase08-history.trx` e `phase08-regression.trx`, ignorados pelo Git. Nenhuma fonte de teste anterior foi reescrita.

Uma repetição direcionada encontrou SQLite Error 5 em gravações concorrentes; a correção serializa mutações do repositório e usa transação diferida na consulta. Depois da correção passaram o filtro novo, Validate completo e a regressão separada. Falhas intermediárias de escape literal na busca e um aviso xUnit foram corrigidos antes da validação final; não estão ocultados por mudança de teste antigo.

## Cobertura dos 33 critérios solicitados

| Critérios | Evidência automatizada |
|---|---|
| 1: migração | `MigrationIsIdempotentAndPreservesExistingTables`: migração 4 repetível, preserva Settings/playlist e versões anteriores |
| 2–4: persistência/reabertura/ordem | `EventsPersistAcrossReopenAndOrderByUtc`: IDs idempotentes, ordenação UTC e nova instância de repositório |
| 5–7: período/tipo/severidade | `FiltersSelectOnlyMatchingEvents` (4 casos); busca literal/SQL parametrizado em `SearchEscapesSqlWildcardsAndInjection` |
| 8–10: página/concorrência/cancelamento | `PaginationHasNoOverlapForEqualTimestamps`; `ConcurrentWritesRemainComplete` (8 produtores, 320 eventos); `CancelledQueryAndWriteDoNotMutateDatabase` |
| 11: retenção | `RetentionDeletesOnlyOlderEventsIncrementally` (30/90/180/365): limite de 500 e fronteira temporal preservada |
| 12–14: banco/fila/flush | `DatabaseFailureDoesNotEscapeToPlayerOrRecurse`; `FullQueueReservesCriticalCapacityAndDoesNotBlockPlayback`; `DisposeFlushesPendingEventsAndIsIdempotent`; `ShutdownFlushTimeoutIsBoundedAndDoesNotConfirmLostSession` |
| 15–16: sessões | `NormalSessionIncludesDurationFinalStateAndErrors`; `UnconfirmedSessionAndPlaybackNeverInventCrashOrDuration` |
| 17–20: reprodução/pausa/tempo/avanço | `ActualPlayerPublishesAdvanceStopAndIndependentPeriods`; `PlaybackPauseResumeExcludesPausedTimeAndDeduplicatesPause`; `ComputerSuspensionIsExcludedFromEffectivePlaybackTime` |
| 21–22: intervenção/regras | `ScheduleTransitionsInterventionAndModesAreObservedWithoutCycles`; `RuleWinnerChangeAndWindowEndAreObserved` |
| 23–27: dispositivos/recuperação/deduplicação | `DisconnectReconnectAndWindowsDuplicatesAreObservedOnce`; `RecoverySuccessAndExhaustionAreStructured` (2 casos); `DeviceSelectionChangeAndFailuresHaveStableIds` |
| 28–29: CSV/Excel | `CsvHasBomQuotesExcelSafetyFiltersAndObservedDurations`: BOM, `;`, aspas/linhas/fórmulas, filtros, duração e recusa de sobrescrita; `InvalidExportDestinationReportsFailure`. Formato compatível verificado por bytes/conteúdo; Excel real não executado |
| 30–33: regressões | 249 anteriores intactos, incluindo player, agendador, dispositivos, inicialização e bandeja; `AutoStartPowerEventsAndShutdownAreAssociatedWithOneSession` acrescenta associação observacional |
| Complementares | `LibraryObservesSignificantChangesButNotEveryFileOrNoOpCycle`; `StructuredDataAndDescriptionsAreBounded`; `ViewModelFiltersPagesDetailsRetentionAndValidation`; ensaio de 10.000 eventos |

Os testes de integração utilizam serviços reais com áudio/dispositivos/relógio substitutos, não hardware USB real. A duração efetiva é observação monotônica dos estados, com pausa e suspensão excluídas; não é medição de som audível.

## Persistência, exportação e retenção

`IOperationalHistoryService` central recebe observações de App/PlaybackService/ScheduleCoordinator/AudioDeviceService/LibraryService. Fila limitada a 2048, reserva crítica, lotes de 64 e writer fora da UI. Sob falha/saturação, perdas são contadas e reportadas quando possível sem recursão. Falhas não comandam o player nem criam recuperação adicional. Flush de encerramento limitado a 3 s e espera adicional do worker até 1 s.

Migração 4: OperationalEvents, OperationalSessions e PlaybackPeriods, índices temporais/tipo/severidade/sessão. IDs de entidades históricas não possuem cascata ao remover catálogo/playlist. UTC persistido, horário local apenas na apresentação. JSON objeto e descrições limitados, sem paths completos nos eventos operacionais. Serilog continua técnico e separado; rotação diária/10 MiB, 14 arquivos. Nenhum log técnico é interpretado para construir histórico.

Consultas parametrizadas e canceláveis, eventos paginados em 50, detalhes e resumo a cada 15 s no painel. Reprodução usa data/busca; Sessões lista execuções em páginas de 50 sem filtros de eventos. CSV UTF-8 BOM com campos escapados/proteção contra fórmulas, filtros de eventos, cursor UTC/ID e páginas de 200. Duração numérica somente no evento terminal para evitar soma repetida. Sobrescrita exige confirmação; falha/cancelamento informa possível arquivo parcial.

Retenção 30/90/180/365 dias, padrão 90 no Settings existente; exclusão incremental até 500 por tabela no início/a cada hora, fora da UI. Preserva registros ativos. Não faz VACUUM: arquivo pode manter tamanho, páginas são reutilizadas. Histórico começa na implantação 0.8, sem backfill a partir de logs.

## Ensaio de desempenho

Medições no TRX direcionado final, banco temporário e 10.000 eventos sintéticos:

| Operação | Resultado local |
|---|---|
| Inserção transacional de 10.000 | 351 ms |
| Consulta da página 101, 50 itens, total 10.000 | 2 ms |
| Exportação CSV dos eventos | 147 ms |
| Retenção incremental de 500 eventos | 18 ms |
| Gravações simultâneas | 8 produtores × 40 = 320 eventos persistidos |

Ensaio sem limite de tempo artificial para mascarar lentidão. Fila bloqueada e banco com falha foram exercitados com o player em reprodução simulada. Medidas locais não garantem desempenho de operação hospitalar prolongada, bibliotecas enormes ou disco em falha.

## Inspeção gráfica 0.8 e limites

Versão Debug final aberta no Windows, modo Manual, saída Padrão do Windows (USB atual), volume 22%, Repeat música e shuffle existentes. Painel inicial mostra resumo discreto; Histórico carrega eventos persistidos de duas execuções, filtros padrão legíveis, seleção/detalhes UTC/local e ID de sessão. Busca sem correspondência exibiu mensagem de vazio e total zero; remoção da busca recuperou os eventos. A reabertura preservou histórico e iniciou parada, sem autoplay.

Fixture silenciosa “Capa de validação” reproduziu com posição avançando em consultas do Histórico. Exportação pelo seletor nativo salvou novo `.tools/phase08-gui-1937.csv` enquanto o player continuava tocando: mensagem de sucesso, BOM EF-BB-BF e 14 linhas de eventos verificadas por leitura. Sem sobrescrever arquivo existente. Pausa observada em 5,57 s no player e parada descarregou a faixa. Banco real mostrou três períodos recentes: dois completos de aproximadamente 20,09 s por repetição e um período de 19,76 s com 5,42 s efetivos, sem incluir pausa. Essa diferença do tempo da engine é esperada da observação dos comandos/buffer; não representa medição de som físico.

Leitura do SQLite real confirmou migrações 1–4, sessão atual em execução e anterior encerrada com duração 254,17 s e zero erros. A ferramenta não expôs o menu da bandeja; antes da compilação foi usado Configurações→Encerrar aplicativo→fechar, com encerramento ordenado em 29 ms no log, limitação comunicada. Algumas capturas imediatas ainda exibiam estado anterior; observações posteriores confirmaram cada resultado. Falha abrupta, indisponibilidade de banco, fila cheia, energia e USB foram exercitadas por testes automatizados, não por retirada física/interrupção da máquina.

Pendente: audibilidade na mesa hospitalar, USB e suspensão físicos, mudança real do padrão, autostart/Explorer reais, operação longa, layouts/DPI e avaliação com operadores. Excel real não foi aberto; o formato CSV foi verificado em software. Nenhuma homologação hospitalar declarada.

Preferência final fechar→bandeja restaurada pela interface e confirmada no Settings real (CloseBehavior=0). Retenção 90 dias e volume 22% preservados. Aplicativo deixado aberto Manual/Parado. Atalhos Ctrl+1/Ctrl+7 não produziram mudança observável pela automação nesta inspeção; bindings declarados e navegação por botões conferida, homologação manual de teclado pendente.

## Git e transferência final

Branch `master`, único commit `a958cd3`. Mudanças 0.6–0.8 permanecem sem commit; `git diff --check` aprovado (avisos LF/CRLF apenas). Revisar estado/fonte/diff sem reset/clean. Persistência é independente do catálogo e do Serilog; preservar recuperação única no coordenador, fila finita, durações desconhecidas honestas, consultas off-UI e SQL parametrizado. **Fase 0.9 não autorizada.**

# Validação da Fase 0.8.1

Data: 09/10/2026. Baseline antes da implementação: **285/285** testes. Implementados contratos de importação, serviço de importação local, adaptador spotDL para prévia de metadados, verificação de binários e manifesto, seção **Importar músicas**, eventos resumidos no Histórico e preparação de pacote. Nenhum commit, download real, alteração do player/agendador ou instalador.

## Comportamento verificado

- URLs HTTPS dos provedores reconhecidos são canonizadas; credenciais, hosts falsos, caminhos extras e IDs inválidos são rejeitados. Parâmetros de compartilhamento não passam ao processo auxiliar.
- Prévia de metadados preserva ordem e não declara disponibilidade de áudio sem faixa local correspondente. Sem ferramentas/credenciais, o diagnóstico bloqueia a consulta; a API YouTube ainda não está integrada.
- MP3 local válido é copiado sem modificar o original, indexado no SQLite e associado a nova playlist na ordem da prévia. Cópia idêntica já catalogada é reutilizada; duplicata dentro do mesmo lote cria só um arquivo e duas ocorrências. Fonte inválida fica indisponível enquanto itens válidos do lote terminam. Nenhum arquivo existente é sobrescrito.
- Processo auxiliar tem argumentos separados, caminho absoluto, captura assíncrona, timeout, cancelamento com encerramento da árvore e retorno de código de saída. Testes usam script local fictício, sem rede ou música baixada. A limpeza temporária específica do adaptador e a CLI real do spotDL não puderam ser executadas sem pacote verificado e credenciais.
- Histórico registra início e resumo de conclusão com contagens e ID da playlist, sem URL ou caminho. O serviço de importação não depende de `IPlaybackService`, `ScheduleCoordinator` ou `IAudioDeviceService`; a suíte anterior cobre regressões deles, mas reprodução física simultânea com importação ainda exige ensaio.

## Resultados e pendências

`scripts/dev.ps1 Restore`, Build Debug e Release: **0 erros/0 avisos**. `scripts/dev.ps1 Test` após Build Release: **307/307**, 0 falhas/ignorados; `phase081.trx`. Os 285 testes anteriores permaneceram, com 22 casos novos em `PlaylistImportTests.cs`. VSTest precisou da execução autorizada fora do sandbox; nele a descoberta ficou presa. Ensaio do script em publicação isolada com executável fictício e SHA-256 errado recusou o pacote antes de copiar.

Versão Debug abriu no Windows em Manual/Parado, biblioteca disponível com quatro músicas. A seção Importar músicas abriu pela navegação; prévia, seleção, nome da playlist, controles de importação/cancelamento e diagnóstico “Ferramentas internas ausentes” foram exibidos. Não foi efetuada importação no perfil real nem reprodução simultânea. Nenhuma configuração foi alterada pelo ensaio. Sem executáveis spotDL/FFmpeg distribuídos, SHA-256 do manifesto permanece vazio e o diagnóstico online informa ausência; nenhuma versão real foi executada. O script `scripts/prepare-import-tools.ps1` exige hashes e licenças externos, valida antes de copiar para `Tools/` de uma publicação nova. Falta fonte de áudio licenciada, credenciais oficiais, auditoria das licenças/builds e teste isolado com binários reais; não há instalador pronto. A operação local deve ser inspecionada visualmente e validada com operador antes de produção.

# Validação da Fase 0.8.2

Data: 09/10/2026. Baseline antes de editar: **307/307** (Release, `phase081.trx`). Escopo: simplificar e tornar funcional a importação de playlists por link usando o spotDL como processo auxiliar. Player, agendador, histórico e dispositivos não foram refatorados. Nenhum commit.

## Investigação da CLI real

Inspecionado o código-fonte da tag `v4.5.2` do spotDL (`spotdl/utils/arguments.py`, `console/save.py`, `utils/search.py`, `utils/config.py`, `utils/deno.py`, `download/downloader.py`) e as notas das releases 4.4.4–4.5.2:

- Desde a 4.5.0 o cliente padrão é **SpotipyFree**, sem credenciais. `--use-official-api` passou a exigir Spotify Premium + client ID/secret; era o motivo do bloqueio da 0.8.1 e foi removido.
- `save` grava JSON (`name`, `artist`, `duration` em segundos, `song_id`, `list_name`, `list_position`, `list_length`…); `--lyrics` sem valores desativa a busca de letras.
- `download` aceita um arquivo `.spotdl` (`Song.from_dict`), `--output` com `{list-position}`/`{output-ext}`, `--format mp3`, `--overwrite skip`, `--threads`, `--ffmpeg`, `--print-errors`, `--simple-tui`. A conversão grava diretamente no destino, por isso a saída vai para staging temporário.
- Pasta de estado: `Path.home()/.spotdl` (config, cache, temp, Deno local). Deno é procurado no PATH e em `~/.spotdl`. O aplicativo isola `USERPROFILE`/`TEMP`/`APPDATA`/`LOCALAPPDATA` numa pasta própria e restringe `PATH` a `Tools\ffmpeg`, `Tools\deno` e Windows.
- Playlists YouTube isoladas não são aceitas pelo spotDL (só pares YouTube Music|Spotify); o aplicativo recusa esses links antes de iniciar processo.

Distribuição (metadados obtidos da API pública do GitHub): `spotdl-4.5.2-win32.exe`, 45.970.977 bytes, digest `sha256:4490ae3b38c4321173e17975a9990a130cf9a9aea8132ee2978afecefbeeb477`; `ffmpeg-9.0.2-essentials_build.zip` (GyanD/codexffmpeg), 114.768.076 bytes, digest `sha256:60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba`. FFmpeg 9.0.2 confirmado como release estável em ffmpeg.org.

## Comportamento implementado e coberto por testes

`PlaylistDownloadTests` (11 casos, spotDL simulado por script PowerShell em pasta com espaços, sem rede):

| Requisito | Teste |
|---|---|
| Prévia, ordem, argumentos seguros, URL canônica sem `?si=`, sem `--use-official-api`/cookies/`--user-auth`, ambiente isolado e pasta temporária removida | `PreviewRunsSaveWithSafeArgumentsIsolatedEnvironmentAndKeepsOrder` |
| Link YouTube não chega ao processo | `YouTubePlaylistIsNotSentToSpotDl` |
| Download, staging fora da pasta musical, verificação, cópia `Artista - Título.mp3`, indexação, playlist na ordem original, falha parcial, progresso concluído/total, histórico sem URL | `DownloadMovesVerifiedFilesIndexesAndCreatesOrderedPlaylistWithPartialFailure` |
| Nova tentativa baixa só faltantes, sem duplicar arquivos | `RetryDownloadsOnlyMissingTracksAndDoesNotDuplicateFiles` |
| Música repetida na playlist: um download, duas ocorrências | `RepeatedSongInPlaylistIsDownloadedOnceAndKeepsBothOccurrences` |
| MP3 existente com mesmo nome não é sobrescrito | `ExistingMusicFileWithSameNameIsNeverOverwritten` |
| Arquivo com tag diferente/incompleto é rejeitado | `FileWithWrongTagsFromHelperIsRejected` |
| Cancelamento encerra o processo, preserva e indexa concluídas, sem playlist, evento de cancelamento | `CancellationKeepsFinishedDownloadsIndexedWithoutPlaylist` |
| Timeout encerra o processo e marca indisponível | `TimeoutStopsHelperAndReportsTracksAsUnavailable` |
| Reprodução simultânea: estado, faixa e cargas do player inalterados | `DownloadDoesNotTouchActivePlayerOrQueue` |
| Pedido `.spotdl` com posições reescritas; entradas sem metadados recusadas | `DownloadRequestRewritesPositionsAndRejectsEntriesWithoutSourceData` |

Ferramenta ausente, manifesto/versão incompatível, SHA-256, processo com espaços, código de saída, timeout e cancelamento do runner, duplicatas locais e importação local continuam cobertos pelos 22 casos de `PlaylistImportTests`. Regressões de player, agendador, histórico e dispositivos: suíte anterior inteira.

## Resultados

Build Debug e Release: **0 erros/0 avisos**. `scripts/dev.ps1 Test` (Release, após os ajustes do ensaio): **318/318**, 0 falhas/ignorados (`phase081.trx`).

## Ensaio prático

Autorizado pelo usuário em 09/10/2026 o download das ferramentas para `.tools/import-tools` (ignorado pelo Git). Nenhuma playlist com download claramente autorizado foi fornecida, portanto **o download de áudio real não foi executado**.

| Etapa | Resultado |
|---|---|
| Ferramentas obtidas | `spotdl-4.5.2-win32.exe` e `ffmpeg-9.0.2-essentials_build.zip`; SHA-256 locais iguais aos digests publicados no GitHub. |
| Pacote | `dotnet publish` Release em `.tools/ensaio 0.8.2/AHNSF Music` (caminho com espaços); `prepare-import-tools.ps1 -FFmpegArchive` conferiu o zip, extraiu `bin/ffmpeg.exe` (SHA-256 derivado `3256173F3F8BFFD7DF12227C68ADF68025EDB1832273A9530688A7BB1ED8EDEC`), licenças e versões, e preencheu o manifesto. Republicar preservou o manifesto preenchido. |
| 1. Link reconhecido | Sim (`open.spotify.com/playlist/…?si=…` canonizado sem `si`). |
| 2. Metadados exibidos | Sim. Via adaptador: playlist pública editorial, 49 faixas em 129 s, nomes/artistas/durações corretos. Na GUI publicada: outra playlist pública analisada, 63 músicas listadas com nome, quantidade, “Será baixada” e Baixar desabilitado até a confirmação de autorização (captura `.tools/ensaio 0.8.2/import-preview.png`). |
| 3. Processo auxiliar iniciado | Sim: diagnóstico (hash + `--version`/`-version`) 3,7 s e `spotdl save` real; sem Python/FFmpeg globais no PATH do processo; pastas `%TEMP%\AHNSF Music\Import\<guid>` removidas ao final; nenhum `spotdl` remanescente. |
| 4–7. Arquivo obtido, pasta, biblioteca, playlist | **Não executado no mundo real** por falta de conteúdo autorizado. Cobertos apenas por `PlaylistDownloadTests` com processo simulado. |
| 8. Reprodução não interrompida | Não ensaiado com download real; coberto por `DownloadDoesNotTouchActivePlayerOrQueue`. A análise na GUI não alterou player/fila. |

Achados corrigidos durante o ensaio: o tempo limite da análise (3 min) era curto para playlists grandes, já que 49 faixas levaram 129 s; passou a 20 min, com Cancelar disponível durante a análise. O `spotdl --version` do script de pacote criava `~/.spotdl/config.json` no perfil real; o script passou a usar perfil temporário e o arquivo criado pelo ensaio foi removido. O aplicativo em si sempre usa perfil isolado.

Para completar o ensaio: usar uma playlist cujo áudio a AHNSF tenha direito de obter (por exemplo, faixas próprias ou com licença que permita download), marcar a confirmação e verificar itens 4–8 com uma música tocando.

## Limites

- O spotDL escolhe a gravação no YouTube Music; correspondência e direito de uso não são garantidos pelo aplicativo. O checkbox de autorização é obrigatório para baixar, mas é declaração do operador, não verificação jurídica.
- Progresso conta arquivos `.mp3` no staging; uma faixa em conversão pode ser contada antes de terminar. O resumo final usa apenas arquivos verificados.
- Deno não é empacotado; sem ele o yt-dlp pode falhar em alguns vídeos.
- Instalador, inclusão de `Tools/`, código-fonte/obrigações GPLv3 do FFmpeg e validação em máquina limpa sem administrador continuam pendentes.

## Correção 0.8.2 — classificação de falhas e Deno (10/10/2026)

Motivo: no teste real do usuário com spotDL 4.5.2, a consulta ao Spotify e a correspondência no YouTube Music funcionaram, mas o yt-dlp respondeu `ERROR: [youtube] …: Sign in to confirm you're not a bot.` e o spotDL avisou `Some YouTube downloads require Deno.` A interface só mostrava “Não foi possível obter o áudio”.

Investigação no código da tag v4.5.2: falhas de faixa entram em `Downloader.errors` como `"{song.url} - {Exceção}: {mensagem}"` e são impressas no fim com `--print-errors`; `AudioProviderError` só cita a URL do YouTube (`YT-DLP download error - …`), e o motivo real é a linha `ERROR: [youtube] <id>: …` do próprio yt-dlp. O aviso do Deno é `WARNING` (antes oculto por `--log-level ERROR`). Deno é procurado com `shutil.which("deno")`, portanto `Tools\deno` no PATH restrito do processo basta.

Implementado: `SpotDlOutputClassifier` (Infrastructure) e `ImportFailureKind`/`ImportFailures` (Core); motivo por faixa em `ImportItemResult.Failure`, resumo `FailureBreakdown` na tela e no Histórico; log técnico sanitizado; log WARNING, `COLUMNS=4000`, captura dos últimos 256 KiB; Deno opcional verificado (manifesto `DenoVersion`/`DenoSha256`, `deno --version`); `prepare-import-tools.ps1 -DenoArchive`. Nenhum cookie, conta, token de prova, proxy ou contorno do bloqueio foi adicionado; a mensagem orienta a tentar mais tarde ou usar outra fonte autorizada.

Testes novos (PlaylistDownloadTests): `ClassifierSeparatesVerificationRuntimeNotFoundConversionAndNetwork` (saída real reproduzida: verificação, Deno, sem correspondência, FFmpeg, rede, texto desconhecido, e Deno presente não é culpado), `SanitizedLogDropsPathsQueryStringsAndSecretsButKeepsVideoId`, `YouTubeBotCheckIsReportedPerTrackInSummaryAndHistoryWithoutCookiesOrUrls` (processo simulado com as mensagens reais; nenhum `--cookies` passado; histórico sem URL), `DenoInPackageMustMatchPinnedHashBeforeAnyProcessRuns`. Suíte Release: **322/322**, Build 0 erros/0 avisos.

Ensaio: `deno-x86_64-pc-windows-msvc.zip` 2.9.7 baixado do release oficial, SHA-256 igual ao digest publicado; `ensaio 0.8.2 deno` montado pelo script com spotDL, FFmpeg e Deno (hash do `deno.exe` derivado do zip verificado: `E020F3E232BD16E33768DEE528E5983349C962952051CED0A5D58AD42F5D9B33`). Diagnóstico do adaptador: pronto em 4,4 s, “spotDL 4.5.2, FFmpeg 9.0.2 e Deno 2.9.7”; no ambiente isolado `where deno` resolve `Tools\deno\deno.exe`.

**Download real continua NÃO validado.** Não foi fornecida fonte de áudio com obtenção autorizada, e o Deno não resolve o bloqueio `not a bot`, que depende do YouTube/rede. Itens 4–8 do ensaio permanecem pendentes.

### Segunda correção (10/10/2026)

O usuário repetiu a importação com o pacote com Deno e todas as faixas continuaram como “Não foi possível obter o áudio”. Causa confirmada no código da v4.5.2: `YTDLLogger.error` levanta `AudioProviderError(msg)` com a mensagem do yt-dlp, `get_download_metadata` faz `logger.debug(exception)` e relança `AudioProviderError("YT-DLP download error - url") from exception`, e `notify_error` só imprime o traceback completo em DEBUG. Com `--log-level WARNING` o console recebia apenas o resumo sem motivo, e o classificador caía em “desconhecido”, como esperado para texto sem padrão. O download passa a usar `--log-level DEBUG` (captura dos últimos 2 MiB; o log técnico continua com 60 linhas sanitizadas). Testes novos: `ReasonOnlyInDebugTracebackIsStillClassifiedAndSummaryAloneIsUnknown` e `DownloadRunsSpotDlAtDebugSoTheSourceReasonIsVisible`. Suíte Release **324/324**. Pacote novo: `.tools/ensaio 0.8.2 deno v2`.

Terceira execução real do usuário (10 faixas únicas, pacote v2): log técnico confirmou `ExtractorError: [youtube] …: Sign in to confirm you’re not a bot` em todas as faixas, com Deno verificado e sem aviso de runtime. A tela mostrou 6 como verificação do YouTube e 4 como erro não identificado. Causas: o rich preenche cada linha até a largura `COLUMNS=4000`, então a janela de 2 MiB guardava poucas linhas e perdia o motivo das primeiras faixas; e as linhas de resumo (que contêm o ID do vídeo) eram tratadas como detalhe e impediam o motivo geral. Correção: captura com `TrimEnd`, linhas de resumo excluídas do detalhe e motivo geral da execução usado quando a faixa não tem motivo próprio. Testes `RichSummaryLinesDoNotHideTheRunWideReason` e `CaptureTrimsConsolePaddingSoLongRunsKeepTheirReasons`; suíte **326/326**; pacote `.tools/ensaio 0.8.2 deno v3`.

Conclusão do ensaio: a cadeia aplicativo → spotDL → busca no YouTube Music funciona; o YouTube recusa a obtenção nesta rede/máquina com verificação anti-robô. Isso não é falha do aplicativo e não tem correção dentro do escopo (sem cookies, contas ou evasão).

Esta correção só torna o motivo visível. Se o motivo for a verificação do YouTube (“not a bot”), o download continua falhando naquela rede: o aplicativo não usa cookies, contas nem mecanismos de evasão. Download real **não validado**.
