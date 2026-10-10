# AHNSF Music

Aplicação desktop Windows para reprodução offline de músicas ambientes da Associação Hospitalar Nossa Senhora de Fátima. Entrega atual: **Fase 0.8.2 — importação de playlists por link (spotDL)**, sobre a Fase 0.8 — logs, histórico operacional e diagnóstico, preservando o player, a programação e a recuperação das fases anteriores.

## Requisitos e execução

Windows 10/11 x64, dispositivo de áudio disponível e SDK .NET 8. A saída pode ser escolhida nas Configurações do aplicativo. O SDK global deste computador era 6.0.428; foi instalado o SDK **8.0.425** em `.tools/dotnet`, sem substituir a instalação global. `global.json` aceita SDKs 8 da mesma versão ou posteriores com roll-forward por feature band.

No PowerShell, dentro de `D:\AHNSF Music`:

```powershell
.\scripts\dev.ps1 Restore
.\scripts\dev.ps1 Build
.\scripts\dev.ps1 Test
.\scripts\dev.ps1 Run
```

`Validate` executa restore, build Debug, build Release e testes, interrompendo em caso de falha. `Build -Configuration Debug` permite compilar a configuração de depuração. Feche o aplicativo antes de recompilar para evitar DLLs bloqueadas; desde a Fase 0.6, fechar a janela pode apenas ocultá-la na bandeja — use **Sair** no menu do ícone. O script usa o SDK local, se disponível; caso contrário, usa `dotnet` do PATH. Dependências ficam em `.tools/packages`. A primeira restauração requer internet; a operação do aplicativo e as compilações com dependências já disponíveis são offline. Se a política do PowerShell impedir scripts, execute os comandos .NET correspondentes num terminal autorizado:

```powershell
dotnet restore AHNSF.Music.sln
dotnet build AHNSF.Music.sln -c Release
dotnet test AHNSF.Music.sln -c Release
dotnet run --project src/AHNSF.Music.Desktop -c Release
```

Os comandos acima exigem SDK 8 no PATH. Para desabilitar a telemetria de build do Avalonia, configure `AVALONIA_TELEMETRY_OPTOUT=1`; o script já faz isso.

Depois de compilar, também é possível abrir `src/AHNSF.Music.Desktop/bin/Release/net8.0-windows/AHNSF.Music.Desktop.exe`. Esta saída exige o runtime .NET 8 instalado. Para gerar uma pasta distribuível, com runtime incluído, numa máquina com SDK 8:

```powershell
dotnet publish src/AHNSF.Music.Desktop -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

Copie a pasta publicada inteira; publicar somente o EXE não basta. Publicação autocontida ainda não validada nesta entrega.

## Utilização

1. Abra **Selecionar pasta** e escolha a pasta local com seus MP3.
2. Aguarde a leitura da pasta e subpastas. Arquivos originais são somente lidos.
3. Selecione uma faixa e clique em **Reproduzir**. Selecionar outra linha sozinho não inicia reprodução.
4. Use Pausar/Reproduzir, Parar, Anterior e Próxima. A barra de progresso permite buscar uma posição, inclusive com a reprodução pausada.
5. Ajuste o volume. Pasta, volume e geometria normal da janela são persistidos. Ao reabrir em modo Manual, as músicas são carregadas e a reprodução permanece parada. O modo Automático explicitamente ativado inicia a regra vigente.
6. A biblioteca acompanha automaticamente arquivos criados, alterados, renomeados e removidos, inclusive em subpastas. Use **Atualizar biblioteca** para reconciliar manualmente.

A lista usa seleção visual e um símbolo musical junto à faixa carregada no player. Ao terminar uma faixa, a seguinte inicia automaticamente; ao terminar a última, o player para quando Repeat está desativado. Shuffle e os três modos de Repeat ficam no player inferior. Arquivos com metadados inválidos aparecem pelo nome do arquivo, acompanhados de aviso; erros de decodificação ou saída de áudio interrompem a reprodução e mostram mensagem em português. Uma falha de áudio não dispara tentativas infinitas.

Início mostra o painel operacional; Biblioteca exibe o catálogo. Playlists organiza coleções salvas; Fila mostra as ocorrências ativas. Configurações permite escolher a saída de áudio, a pasta e as preferências, e consultar o diagnóstico. Programação configura regras semanais e o modo operacional.

## Organização e decisões

```text
AHNSF.Music.sln
src/
  AHNSF.Music.Core/            Entidades, contratos e sequência de reprodução
  AHNSF.Music.Audio/           NAudioEngine e leitura de duração MP3
  AHNSF.Music.Infrastructure/  Catálogo, sincronização, metadados e cache de capas
  AHNSF.Music.Desktop/         Composição DI, MVVM e interface Avalonia
    Styles/DesignSystem.axaml  Tokens, estilos e modelos compartilhados (Fase 0.5)
    Views/                     Painel, Biblioteca, Playlists, Fila, Programação, Configurações e player
    Platform/                  Registro HKCU Run, instância única (mutex + pipe local) e suspensão/retomada (Fase 0.6)
    Shell/                     Política de janela/bandeja e menu da área de notificação (Fase 0.6)
tests/AHNSF.Music.Tests/       Testes e MP3 silencioso de 1 segundo
scripts/dev.ps1               Restore, builds Debug/Release, testes e execução
PROJECT_STATE.md              Continuidade entre sessões
docs/ACCEPTANCE.md             Evidências e limites da validação
```

Core não depende de UI, banco ou NAudio. Audio e Infrastructure dependem de Core; Desktop compõe os serviços por DI. A duração é fornecida por `ITrackDurationReader`, implementada em Audio, para que Infrastructure não dependa da engine. Code-behind integra seletor, ciclo da janela, duplo clique e arraste aos comandos/operações do ViewModel. `PlaybackService` controla a engine existente e `PlaybackQueue` controla ocorrências, histórico e modos; o ViewModel apresenta seu estado.

| Dependência | Versão fixada | Finalidade |
|---|---|---|
| Avalonia.Desktop / Themes.Fluent | 11.3.22 | Interface desktop |
| CommunityToolkit.Mvvm | 8.4.2 | Propriedades e comandos MVVM |
| NAudio | 2.2.1 | MP3, WASAPI compartilhado, endpoints Windows e duração |
| Microsoft.Data.Sqlite | 8.0.31 | Configurações locais |
| TagLibSharp | 2.3.0 | Título e artista |
| Serilog / Sinks.File | 4.4.0 / 7.0.0 | Logs diários |
| Microsoft.Extensions.DependencyInjection | 8.0.1 | Composição dos serviços |
| xUnit / runner Visual Studio | 2.9.3 / 3.1.5 | Testes |
| Microsoft.NET.Test.Sdk | 17.14.1 | Execução dos testes |
| SkiaSharp | 2.88.9 | Miniaturas, mesma versão já utilizada pelo Avalonia |

Versões estáveis verificadas no NuGet oficial e compatibilidade confirmada por restore/build. NAudio permanece na API 2.2.1 usada por esta implementação; a série 3 requer avaliação separada. Fontes: [Avalonia](https://www.nuget.org/packages/Avalonia.Desktop/11.3.22), [NAudio](https://www.nuget.org/packages/NAudio/2.2.1), [Toolkit](https://www.nuget.org/packages/CommunityToolkit.Mvvm/8.4.2), [SQLite](https://www.nuget.org/packages/Microsoft.Data.Sqlite/8.0.31), [TagLibSharp](https://github.com/mono/taglib-sharp).

SQLite direto mantém Settings e Tracks. LibraryMigrations registra as versões 1 (biblioteca), 2 (Playlists/PlaylistItems), 3 (ScheduleRules/ScheduleSettings) e 4 (histórico operacional), aplicadas transacionalmente e de forma idempotente. Metadados ficam somente em Tracks; itens de playlist referenciam TrackId, com chaves estrangeiras e índices. MP3 nunca entra no banco. `TagLibSharp` não chama `Save`. A duração usa NAudio porque foi constatada estimativa incorreta do TagLibSharp no MP3 gerado por FFmpeg.

A engine mantém somente uma faixa/saída ativa e libera recursos em troca, parada e encerramento. Usa WASAPI compartilhado no endpoint selecionado por ID Windows, com volume aplicado ao decodificador. Um mutex por sessão impede abrir uma segunda instância simultânea da aplicação; desde a Fase 0.6, a nova execução apenas pede à primeira que mostre sua janela (detalhes abaixo).

## Configurações e logs

Dados ficam em `%LOCALAPPDATA%\AHNSF Music`: `settings.db` e `logs/music-AAAAMMDD.log`. Logs técnicos têm rotação diária e por 10 MiB, retêm até 14 arquivos (aproximadamente 140 MiB) e podem incluir caminhos para diagnóstico. São separados do histórico operacional SQLite; a interface nunca interpreta arquivos de log. A aplicação deve ter permissão de escrita nessa pasta. Não há serviços externos em tempo de execução.

## Biblioteca persistente — Fase 0.2

A janela carrega o catálogo SQLite existente antes da reconciliação em segundo plano. Caminho absoluto normalizado, sem diferenças de caixa ou separador final, identifica cada localização. A migração não altera Settings. Transações publicam os lotes completos; os comandos SQL são parametrizados e os critérios de ordenação vêm de uma enumeração fechada.

Título, artista, álbum, artista do álbum, gênero, ano, duração, tamanho, modificação UTC, inclusão UTC, caminho relativo, identidade e chave da capa são persistidos. Arquivos inalterados (tamanho/data) não têm metadados relidos. Em **Configurações → Reindexar todos os metadados**, é possível forçar a leitura quando tamanho/data não revelarem a alteração. A data de inclusão e o identificador permanecem estáveis. Renomear para outro caminho cria outra identidade de localização; renomear apenas a caixa mantém a identidade. Registros removidos recebem estado Missing, saem da lista e não são carregados pela reconciliação normal; suas identidades permanecem no banco para uma eventual reintrodução no mesmo caminho.

Use o campo de pesquisa para título, artista, álbum ou arquivo: caixa e acentos são ignorados, com debounce de 250 ms. Combine os filtros de artista e álbum. A ordenação oferece título, artista, álbum, duração e data de inclusão, com opção decrescente. As contagens do cabeçalho representam a biblioteca inteira, incluindo os grupos de artista/álbum desconhecidos; a lista mostra o resultado filtrado. Duplo clique reproduz a faixa. A lista usa VirtualizingStackPanel e substituição em lote, sem criar controles para todos os registros.

A reprodução manual pela biblioteca carrega o catálogo completo em ordem de título, independentemente da pesquisa e da ordenação visual. A sincronização acompanha essa fila enquanto ela não tiver sido editada. Filas de playlists ou editadas pelo usuário preservam suas ocorrências e recebem apenas atualizações de metadados/disponibilidade. SelectedTrack representa a seleção; CurrentTrack mantém o objeto carregado na engine. Sincronizar metadados não chama Load nem altera posição. Filtrar uma faixa carregada para fora da lista não impede sua retomada. Se a faixa carregada desaparecer, a reprodução para com aviso; se o Windows negar remoção porque o arquivo está aberto, ela continua disponível.

FileSystemWatcher recursivo monitora arquivos e diretórios com debounce de 750 ms; uma fila limitada agrupa eventos duplicados. Antes da leitura, o arquivo precisa manter tamanho/data durante 500 ms e permitir leitura sem escritores. Pendências têm novas tentativas com espera crescente a partir de 3 segundos; novos eventos continuam acordando o monitor imediatamente. A reconciliação periódica padrão é de 5 minutos. Essas opções são internas em LibraryOptions. Erros/overflow recriam o watcher e solicitam reconciliação completa, sem reler metadados inalterados. Trocar de pasta cancela a sessão e aguarda seu encerramento antes de criar outro watcher. Pasta indisponível preserva registros; uma subpasta inacessível suspende a detecção de remoções daquele ciclo por segurança.

Capas ficam em `artwork/` no diretório de dados, nunca no SQLite. SkiaSharp 2.88.9 tem [licença MIT e compatibilidade .NET](https://www.nuget.org/packages/SkiaSharp/2.88.9); reutilizar a versão do Avalonia evita outro motor nativo. Miniaturas têm até 256 × 256 pixels; entrada limitada a 8 MiB, 16 milhões de pixels e 16 MiB de tags ID3. Imagens inválidas usam o placeholder musical com as cores AHNSF. Apenas controles visíveis mantêm bitmaps, descartados ao sair da árvore visual, com duas decodificações concorrentes no máximo. A extração/indexação é sequencial. Chaves são hashes da imagem incorporada, não hashes de todos os MP3. Alterações nas capas mudam a chave; imagens repetidas reutilizam o arquivo existente. A limpeza pós-lote remove miniaturas próprias sem referência após 7 dias e aplica teto de 256 MiB ao cache. Se uma miniatura referenciada for removida pelo teto, a próxima reconciliação poderá regenerá-la.

Arquivos sem metadados usam nome do arquivo, Artista desconhecido e Álbum desconhecido. Falhas em tags ou capas não impedem a tentativa de reprodução. Arquivos que não decodificam têm estado Invalid, continuam visíveis e geram aviso; a engine mantém seu tratamento de erro da Fase 0.1. Falhas individuais e duração/resultados das reconciliações aparecem nos logs.

Principais componentes: `SqliteLibraryRepository`, `LibraryService`, `TrackMetadataReader`, `ArtworkCache`, `MainViewModel` e `Controls/ArtworkView`. O scanner antigo continua disponível para compatibilidade e seus testes; o aplicativo utiliza o serviço persistente.

Suíte atual (Fase 0.8.2): **318 casos aprovados** — 285 até a Fase 0.8, 22 da 0.8.1 (`PlaylistImportTests`) e 11 da 0.8.2 (`PlaylistDownloadTests`, spotDL simulado por script PowerShell, sem rede). Na Fase 0.8 eram 285, incluindo os 249 anteriores e 36 novos de histórico e integrações. Os testes usam cópias temporárias da fixture silenciosa e dispositivos simulados, sem TagLib.Save, hardware USB obrigatório, MP3 comerciais, FFmpeg ou internet. As coleções xUnit rodam sequencialmente porque um teste verifica acesso exclusivo à fixture compartilhada; execute processos de teste sequencialmente também. Os testes explícitos de concorrência usam tarefas simultâneas. Resultados em `tests/AHNSF.Music.Tests/TestResults/phase08.trx`, `phase08-regression.trx` (249 casos) e `phase08-history.trx` (36 casos).

## Playlists e fila — Fase 0.3

Em **Playlists → Nova playlist**, informe nome e descrição opcional, marque **Playlist padrão** se desejar e salve. Nomes vazios são rejeitados; nomes repetidos são permitidos, pois cada playlist tem seu próprio ID. **Editar** permite renomear, mudar descrição ou padrão. A estrela identifica a única padrão; defini-la não inicia reprodução. **Excluir** abre confirmação e remove apenas a playlist e suas associações.

Na Biblioteca, selecione músicas (Ctrl/Shift para seleção múltipla), escolha a playlist de destino e use **Adicionar à playlist** ou o menu de contexto. Repetir a ação adiciona novas ocorrências intencionalmente. Na playlist, selecione itens para removê-los; use **Mover para cima/baixo** ou arraste uma linha sobre outra para mudar sua posição. A ordem é salva imediatamente em transação, sem modificar o MP3 nem outra playlist. A quantidade e a duração incluem referências ausentes, identificadas na lista.

**Reproduzir** carrega a playlist na fila em ordem manual; **Aleatório** a carrega com shuffle. Duplo clique reproduz uma ocorrência específica. Arquivos ausentes/inválidos permanecem salvos, mas são ignorados na navegação automática; seleção explícita de indisponível mostra aviso. Falha inesperada de decodificação interrompe o player. A biblioteca continua sendo a fonte dos metadados, inclusive para arquivos Missing.

Na Biblioteca, **Tocar a seguir** (antes “Próxima”) e **+ Fila** inserem as músicas selecionadas como próximas ou ao final. A tela **Fila** permite reproduzir uma ocorrência, remover itens e limpar a fila. Remover a ocorrência atual ou limpar para a reprodução; alterar outras ocorrências conserva áudio e posição. Excluir/editar uma playlist não modifica a fila já carregada, que continua independente. Seus IDs são próprios, diferentes dos IDs de PlaylistItem e Track.

Shuffle conserva a faixa atual e embaralha as ocorrências futuras, sem alterar a ordem manual salva/exibida. Uma passagem visita todas as ocorrências disponíveis antes de repetir; músicas duplicadas contam como ocorrências diferentes. Anterior percorre o histórico da sessão e Próxima refaz esse histórico antes de continuar. Desativar shuffle retoma a ordem manual a partir da ocorrência atual. Repetir fila usa novamente o percurso completo, com o mesmo embaralhamento da passagem; no começo do histórico aleatório, Anterior não inventa uma execução anterior. Uma seleção manual pode repetir intencionalmente uma ocorrência.

Repeat tem três modos: desativado (para no término da fila), fila (retorna ao início) e música (repete a ocorrência atual no término automático). Próxima manual avança mesmo em repetir música; nos limites sem destino, mantém a faixa atual e informa o limite. Shuffle/Repeat são preferências persistidas com as configurações anteriores.

A fila ativa e o histórico ficam **somente em memória**. Reiniciar não recupera a fila da sessão anterior; em modo Manual, a biblioteca carrega uma fila inicial e o áudio fica parado. Em Automático, o coordenador carrega a playlist da regra vigente. Playlists, itens, ordem e padrão permanecem no SQLite. `IPlaylistService` oferece consulta por ID/padrão; `IPlaybackService` oferece carregar por ID, iniciar/parar, estado e fila, sem depender dos comandos de botão. A composição Windows fornece despacho para eventos de término. O fechamento cancela e aguarda tarefas de pesquisa/playlists antes do descarte.

## Programação semanal — Fase 0.4

Em **Programação → Nova regra**, informe nome, playlist, dias de início, horários `HH:mm`, volume (0–100%), prioridade inteira (0–1000) e estado habilitado. Salve; selecione a regra para editar, habilitar/desativar ou excluir com confirmação. Horários iguais são rejeitados. Sexta 22:00–02:00 vale até sábado 02:00. A visão semanal mostra continuação na madrugada, regras desativadas, conflitos, vencedora atual e lacunas. As músicas e playlists originais são preservadas.

A atualização começa em **Manual**. Nesse modo, regras ficam salvas, mas não iniciam nem interrompem o player. **Ativar Automático** persiste o modo e reconcilia imediatamente: dentro de uma janela, inicia sua playlist e volume; fora de todas as janelas, para o player. Reabrir em Automático executa a regra vigente. O aplicativo precisa estar aberto e operacional; funciona minimizado e offline.

O intervalo inclui o início e exclui o fim. Vence a maior prioridade; empate usa início efetivo mais recente e depois menor ID em representação canônica. Ao terminar uma prioridade maior, a menor ainda vigente retoma sua playlist do início. A próxima transição exibida considera mudança de vencedora, ignorando limites de regras perdedoras.

**Intervenções manuais** (play/pause/stop, navegar, seek, volume, editar fila ou modos de reprodução) suspendem o controle automático até a próxima mudança de vencedora, incluindo a saída para uma lacuna. **Suspender automação** também pausa o áudio. Avaliações da mesma janela não desfazem a pausa. A suspensão fica em memória: reiniciar em Automático reavalia a regra. **Ativar Automático** novamente retoma imediatamente. Entrar em Manual conserva a reprodução atual, libera a repetição da sessão e restaura o volume manual.

Durante a sessão agendada, a fila inteira repete continuamente, mesmo se o usuário tiver Repeat desativado ou música. A preferência salva de Repeat e Shuffle permanece intacta. Avaliar a mesma vencedora não recarrega a fila nem altera posição; editar somente o volume aplica o novo valor sem reiniciar. Volume agendado não substitui o volume manual salvo. Editar itens da playlist não altera a fila já carregada; ela será recarregada na próxima sessão. Excluir a playlist conserva a regra com referência nula e gera aviso quando essa regra vence, sem executar outra regra de menor prioridade como substituição silenciosa.

O coordenador independente da UI usa relógio injetável, `IPlaylistService` e `IPlaybackService`. Reconciliará a cada cinco segundos, além da inicialização e das alterações feitas na tela. Mudanças do relógio, data, fuso e retomada são consideradas na próxima avaliação; não exige que um timer dispare em um instante exato. Limites reais podem ter atraso de até um ciclo, acrescido do tempo de consulta/carga e de eventual indisponibilidade do sistema. Regras inválidas/vazias/removidas ou falhas de áudio exibem aviso e limitam novas tentativas a uma por minuto; alteração de configuração ou troca de vencedora permite nova tentativa.

Os horários seguem o fuso local atual do Windows. No horário de verão, um limite inexistente avança ao primeiro minuto válido; limites ambíguos começam na primeira ocorrência e terminam na última, mantendo a janela contínua. Janelas inteiramente absorvidas pelo salto são omitidas nesse dia. SQLite migração 3 cria regras/configuração em transação, mantendo tabelas e dados anteriores. Logs registram modos, início/parada, suspensão/retomada, falhas e reavaliação por alteração significativa de tempo, sem registrar ciclos inalterados.

## Refinamento visual — Fase 0.5

A interface usa tema escuro com paleta, tipografia (Segoe UI), espaçamentos e componentes definidos em `Styles/DesignSystem.axaml`. Botões têm estados de foco, passagem do mouse, pressionado e desabilitado; ações destrutivas usam cor própria e pedem confirmação em uma janela sobreposta (Esc cancela). O contraste do texto principal e secundário fica acima de 4,5:1.

- **Navegação:** barra lateral com Início, Biblioteca, Playlists, Fila, Programação, Configurações e Histórico (Ctrl+1 a Ctrl+7). Ctrl+F abre a pesquisa da Biblioteca. O cabeçalho mostra o modo Manual/Automático e se a automação está suspensa. O player fica sempre visível na parte inferior.
- **Painel (Início):** alertas operacionais no topo, música atual com playlist/origem e estado do player, regra vigente ou prevista, próxima transição, horário previsto de encerramento e totais da biblioteca/playlists/modo.
- **Biblioteca:** pesquisa com botão para limpar pesquisa e filtros, contagem de resultados e de músicas selecionadas, nome do arquivo sob o título, ações “Adicionar à playlist”, “Tocar a seguir” e “+ Fila” (também no botão direito). A lista continua virtualizada.
- **Playlists:** indicação da playlist padrão, ações visíveis apenas com uma playlist selecionada, editor e exclusão em cartões, estados vazios com atalho para a Biblioteca. Ao arrastar uma linha, o destino é destacado; a ordem continua salva em transação. Remover itens pede confirmação.
- **Fila:** resumo da origem e posição; remover ou limpar pede confirmação; itens ausentes ou inválidos aparecem destacados.
- **Programação:** avisos em destaque, visão semanal em grade com o dia de hoje, regra vencedora, conflitos e lacunas.
- **Telas menores:** abaixo de 1080 px de largura as colunas da biblioteca são reduzidas; abaixo de 800 px de altura (por exemplo 1366×768 com barra de tarefas) cabeçalho, margens e player ficam mais compactos.

Esta fase não alterou serviços, banco de dados, fila, player ou agendador.

## Autonomia operacional — Fase 0.6

**Iniciar com o Windows.** Em **Configurações → Inicialização e janela**, marque *Iniciar AHNSF Music com o Windows*. A opção vem desativada, vale só para o usuário atual e não exige administrador: o aplicativo grava um único valor “AHNSF Music” em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` com o caminho completo entre aspas (`"…\AHNSF.Music.Desktop.exe" --autostart`), sem depender da pasta de trabalho. Não há serviço do Windows nem tarefa agendada. Desmarcar remove o valor; entradas duplicadas que apontem para o mesmo executável também são removidas. Ao abrir, o aplicativo confere o registro e avisa se a entrada foi removida, aponta para outro local (por exemplo, a pasta do programa mudou), está duplicada ou foi desativada no Gerenciador de Tarefas; **Aplicar novamente** recria ou corrige (a desativação feita pelo Windows não é sobrescrita: reative-a no Gerenciador de Tarefas › Aplicativos de inicialização). Funciona somente com o executável compilado, não com `dotnet run`.

**Iniciar minimizado.** *Iniciar minimizado na bandeja* só tem efeito quando o Windows inicia o aplicativo (`--autostart`) e o ícone da bandeja existe. Configurações, biblioteca, playlists e o coordenador carregam normalmente com a janela oculta; em Manual nada toca, em Automático a regra vigente começa como em qualquer abertura. O modo salvo nunca é alterado.

**Bandeja.** O ícone ao lado do relógio (pode estar em “Mostrar ícones ocultos”) mostra o estado na dica e oferece: Abrir AHNSF Music, estado atual, Reproduzir/Pausar, Próxima música, Modo de operação (Manual/Automático) e Sair. As ações usam os mesmos comandos da janela — não existe um segundo player. Reproduzir/Pausar pela bandeja conta como intervenção manual, como na janela.

**Fechar a janela.** Em *Ao fechar a janela*: **Minimizar para a bandeja** (padrão) ou **Encerrar o aplicativo**. Na primeira vez que a janela é fechada, uma confirmação explica que a música e a programação continuam na bandeja. Se o ícone não puder ser criado, fechar a janela encerra o aplicativo — ela nunca fica invisível e inacessível. **Sair** (ou fechar com “Encerrar”) pede confirmação quando há música tocando ou o modo Automático está ativo. O desligamento/saída do Windows não pergunta. O encerramento para o agendador, cancela operações pendentes, para o áudio, salva as configurações e libera os recursos, com limite de 15 s contra travamentos.

**Instância única.** Abrir o aplicativo de novo não cria outro player: a nova execução pede, por um canal local (pipe nomeado restrito ao usuário, acesso pela rede negado, sem portas), que a instância existente mostre e foque sua janela, e termina. O canal aceita somente esse pedido; não altera o player. Se a execução anterior terminou de forma inesperada, o controle de instância é recuperado e registrado no log.

**Recuperação de falhas (modo Automático).** Uma falha transitória de áudio (dispositivo indisponível, falha ao abrir a saída, interrupção do mecanismo) gera nova tentativa após **5 s**, **15 s** e **30 s**; se a terceira falhar, o painel mostra “Recuperação esgotada” e a programação aguarda intervenção (por exemplo, **Ativar Automático**) ou a próxima transição. Falhas de arquivo/mídia (MP3 inválido, ausente ou sem permissão) não geram novas tentativas de áudio. Playlist vazia/removida continua sendo reverificada a cada minuto, sem tentar tocar. Parar, pausar, **Suspender automação**, entrar em Manual ou o fim do horário cancelam as tentativas; música nunca recomeça fora do intervalo. O coordenador de programação é o único responsável pela recuperação. Em **Manual** não há reinício automático: o erro aparece no alerta e no diagnóstico.

**Suspensão e retomada.** O aplicativo recebe os avisos de suspensão/retomada do Windows e, independentemente deles, o coordenador detecta saltos de relógio. Na retomada: zera as tentativas, reconcilia a biblioteca e reavalia a programação imediatamente — dentro do expediente (Automático, sem suspensão manual) a música volta; depois do horário, nada toca.

**Diagnóstico.** Em **Configurações → Diagnóstico operacional**: player, modo, agendador, playlist ativa, próxima transição, pasta musical, biblioteca, mecanismo de áudio, último erro relevante, última recuperação, tentativas recentes, inicialização com o Windows, área de notificação, se esta execução foi iniciada pelo Windows e a última suspensão/retomada. Nada é enviado pela rede. Os logs registram início (manual/automático), bandeja, restauração, encerramento, falhas, tentativas, recuperação, esgotamento, suspensão/retomada e falhas de inicialização automática, sem repetir ciclos normais.

**Confirmações.** Enquanto uma confirmação está aberta, o restante da janela fica desabilitado: Tab circula apenas pelos botões da confirmação, o foco começa em **Cancelar** (Enter só confirma com o foco no botão de confirmar), Esc cancela, atalhos globais ficam bloqueados e o foco volta ao controle anterior ao fechar.

Limites: não houve suspensão física do computador, desconexão real da interface USB, reinício do Explorer nem ativação no registro real desta máquina; esses ensaios dependem de autorização e homologação física. Seleção e monitoramento de dispositivos foram implementados na Fase 0.7; fade, instalador e publicação autocontida permanecem pendentes.

## Saída de áudio — Fase 0.7

Em **Configurações → Dispositivo de saída de áudio**, mantenha **Utilizar o dispositivo padrão do Windows** ou desmarque e escolha uma interface específica. A lista mostra nome, disponibilidade e padrão. Clique em **Aplicar seleção** para validar, trocar e salvar; **Atualizar dispositivos** reconcilia a lista. Para a mesa USB, prefira a seleção específica. **Configurado** mostra a preferência; **Em uso** mostra o endpoint realmente aberto (ou nenhuma saída ativa quando parado).

A identidade persistida é o ID Windows, não o nome nem a posição na lista. Desconexão ou indisponibilidade para e libera o áudio, preserva a seleção e avisa o operador. Não existe fallback para alto-falantes internos. Um dispositivo com nome igual e ID diferente precisa ser escolhido manualmente. O padrão fica fixado durante a sessão: se o Windows mudar seu padrão, o aplicativo interrompe a saída e exige **Aplicar seleção**; ao iniciar novamente, resolve o padrão atual.

Notificações do Windows e uma reconciliação a cada 30 segundos detectam alterações. O retorno do mesmo ID apenas restabelece disponibilidade: no modo **Manual** nada começa sozinho; no **Automático**, somente o coordenador existente decide retomar, após verificar a janela vigente e a suspensão por intervenção. Não há outro mecanismo de recuperação no monitor. Falhas transitórias do driver continuam usando as tentativas limitadas da Fase 0.6; ausência conhecida do dispositivo aguarda seu retorno sem tentar tocar continuamente.

A troca manual é uma intervenção na automação. Quando possível, a engine conserva posição, estado tocando/pausado e volume vigente após essa intervenção (a política existente restaura o volume manual ao suspender uma sessão agendada). A troca libera a saída anterior antes de abrir outra. Se falhar, para e pede reaplicação. Após uma desconexão a faixa é descarregada; a recuperação automática recarrega a playlist vigente, sem garantir a posição anterior.

O diagnóstico acrescenta seleção/ID, saída efetiva, padrão, disponibilidade, última perda/retorno/falha e necessidade de intervenção. Settings ganha `AudioDevice` opcional no JSON atual; configurações antigas usam Default e conservam os demais campos. Nenhuma migração SQLite ou dependência nova. WASAPI compartilhado usa o endpoint explícito; ganho fica no `AudioFileReader`, sem mudar o volume global ou o padrão Windows.

Para continuidade entre agentes, leia `PROJECT_STATE.md`, este README e `docs/ACCEPTANCE.md`, compare a fonte com `git status`/`git diff` e preserve o trabalho não commitado. Execute testes sequencialmente e encerre via **Sair** na bandeja antes de recompilar. A validação desta sessão precisou executar VSTest fora do sandbox autorizado, pois dentro dele não chegou à descoberta dos testes.

## Validação e limites

Consulte [docs/ACCEPTANCE.md](docs/ACCEPTANCE.md) para critérios e evidências reais. Os testes usam um MP3 silencioso gerado por FFmpeg; o aplicativo não depende de FFmpeg.

Esta fase não valida operação prolongada, desconexão física da interface USB nem audição na mesa de som hospitalar. Esses ensaios continuam necessários antes de produção. Fade permanece no roadmap.

**Não iniciar a Fase 0.9 sem autorização.** O perfil observado nesta sessão aponta para `.tools/smoke`, playlist “QA — Fase 0.3”, modo Manual e volume 22%; registros de fases anteriores descrevem outro perfil QA. Ajuste as preferências antes do uso real. Git: branch `master`, commit inicial `a958cd3`; alterações das Fases 0.6–0.8 ainda não commitadas. Nenhum commit automático foi criado.

## Histórico operacional — Fase 0.8

Abra **Histórico** na navegação, no painel inicial ou por **Ctrl+7**. Escolha Hoje, últimos 7/30 dias ou datas personalizadas `AAAA-MM-DD` (início e fim inclusivos no horário local), tipo, severidade e busca; pressione **Aplicar filtros / atualizar**. Eventos aparecem em páginas de 50 com lista virtualizada; selecione um para ver UTC, horário local, componente, origem e IDs relacionados. Consultas anteriores são canceladas ao trocar de página/atualizar. A aba Reprodução filtra por data inicial e título/playlist; tipo/severidade aplicam-se aos eventos. Sessões do aplicativo têm paginação própria dentro da mesma página, sem os filtros de eventos.

São registrados início/autostart/saída/energia, reprodução/pausa/retomada/parada/fim/avanço/troca/erro, vencedora/modo/intervenção/suspensão/falha da programação, seleção/perda/retorno/falha/recuperação de saída e mudanças significativas da biblioteca. Eventos repetitivos de progresso, cada arquivo indexado e ciclos sem mudança não alimentam o histórico. Descrições operacionais são limitadas e evitam caminhos completos; dados extras são JSON objeto de até 2048 caracteres. Nada é enviado pela rede.

`OperationalHistoryService` (Core) recebe observações sem I/O no produtor e usa um writer em segundo plano. Fila máxima de 2048, reserva para eventos críticos, lotes transacionais de 64. Sob sobrecarga, informações podem ser descartadas antes dos críticos; perdas são contadas e geram aviso quando o banco permite. Falha de armazenamento não interrompe áudio/programação. `SqliteOperationalHistoryRepository` (Infrastructure) serializa gravações, faz consultas fora da UI e usa transação de leitura diferida. O encerramento tenta descarregar a fila por até 3 segundos; não espera indefinidamente por armazenamento.

A migração 4 adiciona `OperationalEvents` (UTC, tipo/severidade, componente/descrição, sessão/origem, IDs de música/playlist/regra/dispositivo/período, JSON), `OperationalSessions` (início/fim, duração observada, estado final, erros) e `PlaybackPeriods` (origem/IDs/títulos, início/fim, segundos efetivos, motivo e confirmação). Índices cobrem tempo/ID, tipo/tempo, severidade/tempo e sessão. IDs históricos são referências lógicas, sem cascata ao remover músicas/playlists. Configurações e migrações 1–3 são preservadas.

Sessão do aplicativo é uma execução; período de reprodução é uma faixa tocada nessa execução. Tempo reproduzido usa relógio monotônico e exclui pausas e suspensão do computador; não mede a audibilidade física. Período aberto ou execução anterior sem encerramento confirmado tem duração desconhecida. A causa de encerramento não confirmado é desconhecida: o sistema não conclui que houve crash nem inventa horários/duração.

**Exportar CSV…** usa os filtros de eventos, UTF-8 com BOM, separador `;`, aspas/escapes, cabeçalhos em português e proteção contra fórmulas de planilha. Inclui IDs, origens, títulos, tempos e classificação da duração. Duração numérica aparece apenas no evento terminal para evitar somas duplicadas; outros eventos relacionados remetem ao término. Exportação assíncrona usa cursor UTC/ID em páginas de 200 e um limite temporal. Arquivo existente exige confirmação; cancelamento/falha pode deixar arquivo parcial e é comunicado. Compatibilidade do formato é testada; abertura em Excel real ainda requer homologação.

Retenção pode ser **30, 90 (padrão), 180 ou 365 dias**, salva no JSON Settings pela tela. Limpeza começa em segundo plano e executa no máximo 500 remoções por tabela por ciclo, com novo ciclo a cada hora; preserva sessões/períodos atuais. Grandes atrasos são eliminados gradualmente. É retenção lógica: páginas SQLite são reutilizadas, sem VACUUM que bloqueie o áudio. Não há importação retrospectiva de logs técnicos. O painel mostra estado atual, última falha, recuperação, desconexão e contagem de eventos de erro nas últimas 24 horas, atualizada a cada 15 segundos.

Ensaio local com 10.000 eventos: inserção 351 ms; página 101 com 50 eventos, 2 ms; CSV, 147 ms; limpeza de 500, 18 ms. Testes também cobrem 8 produtores concorrentes (320 eventos), banco/fila indisponíveis, cancelamento, flush limitado e integrações. Consulte `docs/ACCEPTANCE.md` para resultados e limites; estas medidas não certificam operação contínua hospitalar.

## Importar músicas — Fase 0.8.2

Abra **Importar músicas** pela barra lateral ou **Ctrl+8**.

1. Cole o **Link da playlist** do Spotify (`https://open.spotify.com/playlist/…`, também álbum ou faixa) e clique em **Analisar**.
2. Confira o nome (editável; será o nome da playlist local), a quantidade e a lista de músicas. Faixas que já estão na biblioteca aparecem como “Já na biblioteca” e não são baixadas de novo.
3. Marque **Confirmo que a AHNSF tem autorização para obter e reproduzir o áudio destas músicas**. Sem essa confirmação o botão **Baixar** fica desabilitado: um link de playlist não é licença de uso.
4. Clique em **Baixar** e acompanhe a barra e a contagem concluída/total. **Cancelar** interrompe o processo auxiliar.
5. Ao final, o resumo mostra novas, já existentes e não disponíveis. Os MP3 ficam na pasta musical configurada em **Configurações** com o nome `Artista - Título.mp3`, a biblioteca é atualizada e a playlist é criada em **Playlists** na ordem original.

Uma música indisponível não impede as demais; ela aparece no resumo e a importação é registrada como “com pendências”, nunca como sucesso completo. Repetir a importação baixa somente as faltantes. Cancelar ou exceder o tempo limite preserva e indexa as músicas já concluídas, sem criar playlist. Nada é reproduzido, a fila ativa não muda e nenhum MP3 existente é alterado ou sobrescrito (nomes iguais recebem sufixo ` (2)`). O Histórico registra início, conclusão, cancelamento e falha com as contagens, sem URL, caminho ou credencial.

**Importar MP3 do computador…** continua disponível como ação secundária: selecione arquivos, confira a prévia e clique em **Importar selecionadas** (cópia sem sobrescrita, duplicatas por SHA-256, playlist ordenada).

### Como funciona

O aplicativo controla o [spotDL](https://github.com/spotDL/spotify-downloader) como processo auxiliar sob demanda, nunca durante a reprodução de forma acoplada ao player:

- **Prévia:** `spotdl save <link> --save-file <temp> --lyrics` (sem provedores de letra). Desde o spotDL 4.5.0 o padrão é o cliente público SpotipyFree, sem conta nem credenciais; o aplicativo não passa `--use-official-api`, `--user-auth`, cookies ou argumentos do yt-dlp.
- **Download:** o aplicativo grava um `.spotdl` apenas com as faixas faltantes (posições renumeradas) e executa `spotdl download <arquivo> --output <staging>\{list-position}.{output-ext} --format mp3 --overwrite skip --threads 2 --ffmpeg <Tools\ffmpeg\ffmpeg.exe>`. O spotDL procura o áudio no YouTube Music e converte com o FFmpeg empacotado.
- **Verificação:** código de saída não é confiável; só contam arquivos `N.mp3` que mapeiam para uma faixa pedida, têm tamanho mínimo, duração e título (tag gravada pelo spotDL) iguais ao esperado. Depois são copiados para a pasta musical sem sobrescrita, reconciliados e associados à playlist.
- **Isolamento:** caminho absoluto, `ArgumentList` (sem shell), stdin fechado, `USERPROFILE`/`TEMP`/`APPDATA` apontando para uma pasta temporária própria (`%TEMP%\AHNSF Music\Import\<guid>`, removida ao final), `PATH` restrito a `Tools\ffmpeg`, `Tools\deno` e Windows. Python global, FFmpeg global, pip e downloads automáticos de dependências não são usados. Cancelamento/timeout encerram a árvore de processos. Timeout: 3 minutos por faixa (+1), máximo 8 horas.
- **Integridade:** antes do primeiro uso (e sempre que os arquivos mudam) o manifesto, os SHA-256 e `--version`/`-version` são conferidos.

Limites: playlists do YouTube não são aceitas (o spotDL só aceita pares YouTube Music|Spotify); o spotDL pode escolher uma gravação diferente da original; o progresso conta arquivos recebidos no staging; sem Deno alguns downloads do yt-dlp podem falhar. Requer internet durante a importação; a reprodução continua offline.

### Ferramentas distribuídas

Layout no pacote, ao lado de `AHNSF.Music.Desktop.exe`: `Tools/manifest.json`, `Tools/spotdl/spotdl.exe` + `LICENSE.txt`, `Tools/ffmpeg/ffmpeg.exe` + `LICENSE.txt` e, opcionalmente, `Tools/deno/deno.exe`.

| Ferramenta | Versão | Artefato e origem | SHA-256 publicado | Licença |
|---|---|---|---|---|
| spotDL | 4.5.2 | `spotdl-4.5.2-win32.exe` (45.970.977 bytes), [release oficial](https://github.com/spotDL/spotify-downloader/releases/tag/v4.5.2); executável PyInstaller com Python 3.13 e yt-dlp embutidos | `4490ae3b38c4321173e17975a9990a130cf9a9aea8132ee2978afecefbeeb477` (digest do GitHub) | MIT (dependências embutidas com licenças próprias) |
| FFmpeg | 9.0.2 | `ffmpeg-9.0.2-essentials_build.zip` (114.768.076 bytes), [GyanD/codexffmpeg](https://github.com/GyanD/codexffmpeg/releases/tag/9.0.2) | `60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba` (digest do zip no GitHub) | GPLv3 (build “essentials” com bibliotecas GPL) |

`scripts/prepare-import-tools.ps1` recebe a publicação nova, `spotdl.exe` com seu SHA-256 publicado e licença, e o FFmpeg como executável+hash ou como **zip + hash publicado do zip** (`-FFmpegArchive`/`-FFmpegArchiveSha256`); nesse caso o hash do `ffmpeg.exe` é derivado somente do pacote verificado. O script confere versões, copia sem sobrescrever e preenche o manifesto; não baixa nada nem exige administrador. O manifesto do repositório mantém SHA-256 vazio, o que bloqueia a execução em compilações de desenvolvimento sem pacote verificado.

Pendências para o instalador: incluir `Tools/` e licenças, distribuir/oferecer o código-fonte correspondente do FFmpeg (GPLv3), revisar licenças das dependências empacotadas pelo spotDL, decidir sobre Deno, validar em máquina limpa sem Python/PATH/administrador e em pasta com espaços. O executável PyInstaller extrai arquivos em `%TEMP%` a cada execução (alguns segundos na primeira chamada); antivírus corporativos podem precisar de liberação.
