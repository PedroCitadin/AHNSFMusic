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
