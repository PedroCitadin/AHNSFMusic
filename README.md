# AHNSF Music

Aplicação desktop Windows para reprodução offline de músicas ambientes da Associação Hospitalar Nossa Senhora de Fátima. Entrega atual: **Fase 0.5 — refinamento visual e experiência de uso**, sobre a programação semanal da Fase 0.4.

## Requisitos e execução

Windows 10/11 x64, saída de áudio padrão configurada no Windows e SDK .NET 8. O SDK global deste computador era 6.0.428; foi instalado o SDK **8.0.425** em `.tools/dotnet`, sem substituir a instalação global. `global.json` aceita SDKs 8 da mesma versão ou posteriores com roll-forward por feature band.

No PowerShell, dentro de `D:\AHNSF Music`:

```powershell
.\scripts\dev.ps1 Restore
.\scripts\dev.ps1 Build
.\scripts\dev.ps1 Test
.\scripts\dev.ps1 Run
```

`Validate` executa restore, build Debug, build Release e testes, interrompendo em caso de falha. `Build -Configuration Debug` permite compilar a configuração de depuração. Feche o aplicativo antes de recompilar para evitar DLLs bloqueadas. O script usa o SDK local, se disponível; caso contrário, usa `dotnet` do PATH. Dependências ficam em `.tools/packages`. A primeira restauração requer internet; a operação do aplicativo e as compilações com dependências já disponíveis são offline. Se a política do PowerShell impedir scripts, execute os comandos .NET correspondentes num terminal autorizado:

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

Início e Biblioteca exibem o catálogo. Playlists organiza coleções salvas; Fila mostra as ocorrências ativas. Configurações permite escolher a pasta e explica a persistência. Programação configura regras semanais e o modo operacional.

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
| NAudio | 2.2.1 | MP3, WaveOutEvent e duração |
| Microsoft.Data.Sqlite | 8.0.31 | Configurações locais |
| TagLibSharp | 2.3.0 | Título e artista |
| Serilog / Sinks.File | 4.4.0 / 7.0.0 | Logs diários |
| Microsoft.Extensions.DependencyInjection | 8.0.1 | Composição dos serviços |
| xUnit / runner Visual Studio | 2.9.3 / 3.1.5 | Testes |
| Microsoft.NET.Test.Sdk | 17.14.1 | Execução dos testes |
| SkiaSharp | 2.88.9 | Miniaturas, mesma versão já utilizada pelo Avalonia |

Versões estáveis verificadas no NuGet oficial e compatibilidade confirmada por restore/build. NAudio permanece na API 2.2.1 usada por esta implementação; a série 3 requer avaliação separada. Fontes: [Avalonia](https://www.nuget.org/packages/Avalonia.Desktop/11.3.22), [NAudio](https://www.nuget.org/packages/NAudio/2.2.1), [Toolkit](https://www.nuget.org/packages/CommunityToolkit.Mvvm/8.4.2), [SQLite](https://www.nuget.org/packages/Microsoft.Data.Sqlite/8.0.31), [TagLibSharp](https://github.com/mono/taglib-sharp).

SQLite direto mantém Settings e Tracks. LibraryMigrations registra as versões 1 (biblioteca), 2 (Playlists/PlaylistItems) e 3 (ScheduleRules/ScheduleSettings), aplicadas transacionalmente e de forma idempotente. Metadados ficam somente em Tracks; itens de playlist referenciam TrackId, com chaves estrangeiras e índices. MP3 nunca entra no banco. `TagLibSharp` não chama `Save`. A duração usa NAudio porque foi constatada estimativa incorreta do TagLibSharp no MP3 gerado por FFmpeg.

A engine mantém somente uma faixa/saída ativa e libera recursos em troca, parada e encerramento. Usa a saída padrão do Windows, sem seleção de dispositivo. Um mutex por sessão impede abrir uma segunda instância simultânea da aplicação.

## Configurações e logs

Dados ficam em `%LOCALAPPDATA%\AHNSF Music`: `settings.db` e `logs/music-AAAAMMDD.log`. Logs diários são retidos por 14 arquivos e incluem detalhes técnicos e caminhos para diagnóstico. A aplicação deve ter permissão de escrita nessa pasta. Não há serviços externos em tempo de execução.

## Biblioteca persistente — Fase 0.2

A janela carrega o catálogo SQLite existente antes da reconciliação em segundo plano. Caminho absoluto normalizado, sem diferenças de caixa ou separador final, identifica cada localização. A migração não altera Settings. Transações publicam os lotes completos; os comandos SQL são parametrizados e os critérios de ordenação vêm de uma enumeração fechada.

Título, artista, álbum, artista do álbum, gênero, ano, duração, tamanho, modificação UTC, inclusão UTC, caminho relativo, identidade e chave da capa são persistidos. Arquivos inalterados (tamanho/data) não têm metadados relidos. Em **Configurações → Reindexar todos os metadados**, é possível forçar a leitura quando tamanho/data não revelarem a alteração. A data de inclusão e o identificador permanecem estáveis. Renomear para outro caminho cria outra identidade de localização; renomear apenas a caixa mantém a identidade. Registros removidos recebem estado Missing, saem da lista e não são carregados pela reconciliação normal; suas identidades permanecem no banco para uma eventual reintrodução no mesmo caminho.

Use o campo de pesquisa para título, artista, álbum ou arquivo: caixa e acentos são ignorados, com debounce de 250 ms. Combine os filtros de artista e álbum. A ordenação oferece título, artista, álbum, duração e data de inclusão, com opção decrescente. As contagens do cabeçalho representam a biblioteca inteira, incluindo os grupos de artista/álbum desconhecidos; a lista mostra o resultado filtrado. Duplo clique reproduz a faixa. A lista usa VirtualizingStackPanel e substituição em lote, sem criar controles para todos os registros.

A reprodução manual pela biblioteca carrega o catálogo completo em ordem de título, independentemente da pesquisa e da ordenação visual. A sincronização acompanha essa fila enquanto ela não tiver sido editada. Filas de playlists ou editadas pelo usuário preservam suas ocorrências e recebem apenas atualizações de metadados/disponibilidade. SelectedTrack representa a seleção; CurrentTrack mantém o objeto carregado na engine. Sincronizar metadados não chama Load nem altera posição. Filtrar uma faixa carregada para fora da lista não impede sua retomada. Se a faixa carregada desaparecer, a reprodução para com aviso; se o Windows negar remoção porque o arquivo está aberto, ela continua disponível.

FileSystemWatcher recursivo monitora arquivos e diretórios com debounce de 750 ms; uma fila limitada agrupa eventos duplicados. Antes da leitura, o arquivo precisa manter tamanho/data durante 500 ms e permitir leitura sem escritores. Pendências têm novas tentativas com espera crescente a partir de 3 segundos; novos eventos continuam acordando o monitor imediatamente. A reconciliação periódica padrão é de 5 minutos. Essas opções são internas em LibraryOptions. Erros/overflow recriam o watcher e solicitam reconciliação completa, sem reler metadados inalterados. Trocar de pasta cancela a sessão e aguarda seu encerramento antes de criar outro watcher. Pasta indisponível preserva registros; uma subpasta inacessível suspende a detecção de remoções daquele ciclo por segurança.

Capas ficam em `artwork/` no diretório de dados, nunca no SQLite. SkiaSharp 2.88.9 tem [licença MIT e compatibilidade .NET](https://www.nuget.org/packages/SkiaSharp/2.88.9); reutilizar a versão do Avalonia evita outro motor nativo. Miniaturas têm até 256 × 256 pixels; entrada limitada a 8 MiB, 16 milhões de pixels e 16 MiB de tags ID3. Imagens inválidas usam o placeholder musical com as cores AHNSF. Apenas controles visíveis mantêm bitmaps, descartados ao sair da árvore visual, com duas decodificações concorrentes no máximo. A extração/indexação é sequencial. Chaves são hashes da imagem incorporada, não hashes de todos os MP3. Alterações nas capas mudam a chave; imagens repetidas reutilizam o arquivo existente. A limpeza pós-lote remove miniaturas próprias sem referência após 7 dias e aplica teto de 256 MiB ao cache. Se uma miniatura referenciada for removida pelo teto, a próxima reconciliação poderá regenerá-la.

Arquivos sem metadados usam nome do arquivo, Artista desconhecido e Álbum desconhecido. Falhas em tags ou capas não impedem a tentativa de reprodução. Arquivos que não decodificam têm estado Invalid, continuam visíveis e geram aviso; a engine mantém seu tratamento de erro da Fase 0.1. Falhas individuais e duração/resultados das reconciliações aparecem nos logs.

Principais componentes: `SqliteLibraryRepository`, `LibraryService`, `TrackMetadataReader`, `ArtworkCache`, `MainViewModel` e `Controls/ArtworkView`. O scanner antigo continua disponível para compatibilidade e seus testes; o aplicativo utiliza o serviço persistente.

Suíte atual: 161 casos aprovados (155 até a Fase 0.4 e 6 de apresentação da Fase 0.5). Na Fase 0.4 eram 155: os 98 anteriores e 57 novos casos de programação, SQLite e MVVM. Os testes usam cópias temporárias da fixture silenciosa, sem TagLib.Save, MP3 comerciais, FFmpeg ou internet. As coleções xUnit rodam sequencialmente porque um teste verifica acesso exclusivo à fixture compartilhada; execute processos de teste sequencialmente também. Os testes explícitos de concorrência usam tarefas simultâneas. Resultados em `tests/AHNSF.Music.Tests/TestResults/phase04.trx`, `phase04-sqlite.trx` (24 casos) e `phase04-regression.trx` (98 casos).

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

- **Navegação:** barra lateral com Início, Biblioteca, Playlists, Fila, Programação e Configurações (Ctrl+1 a Ctrl+6). Ctrl+F abre a pesquisa da Biblioteca. O cabeçalho mostra o modo Manual/Automático e se a automação está suspensa. O player fica sempre visível na parte inferior.
- **Painel (Início):** alertas operacionais no topo, música atual com playlist/origem e estado do player, regra vigente ou prevista, próxima transição, horário previsto de encerramento e totais da biblioteca/playlists/modo.
- **Biblioteca:** pesquisa com botão para limpar pesquisa e filtros, contagem de resultados e de músicas selecionadas, nome do arquivo sob o título, ações “Adicionar à playlist”, “Tocar a seguir” e “+ Fila” (também no botão direito). A lista continua virtualizada.
- **Playlists:** indicação da playlist padrão, ações visíveis apenas com uma playlist selecionada, editor e exclusão em cartões, estados vazios com atalho para a Biblioteca. Ao arrastar uma linha, o destino é destacado; a ordem continua salva em transação. Remover itens pede confirmação.
- **Fila:** resumo da origem e posição; remover ou limpar pede confirmação; itens ausentes ou inválidos aparecem destacados.
- **Programação:** avisos em destaque, visão semanal em grade com o dia de hoje, regra vencedora, conflitos e lacunas.
- **Telas menores:** abaixo de 1080 px de largura as colunas da biblioteca são reduzidas; abaixo de 800 px de altura (por exemplo 1366×768 com barra de tarefas) cabeçalho, margens e player ficam mais compactos.

Esta fase não alterou serviços, banco de dados, fila, player ou agendador.
## Validação e limites

Consulte [docs/ACCEPTANCE.md](docs/ACCEPTANCE.md) para critérios e evidências reais. Os testes usam um MP3 silencioso gerado por FFmpeg; o aplicativo não depende de FFmpeg.

Esta fase não valida operação prolongada, desconexão física da interface USB nem audição na mesa de som hospitalar. Esses ensaios continuam necessários antes de produção. Bandeja, inicialização com Windows, fade e seleção de dispositivos seguem o roadmap e não foram antecipados.

A Fase 0.6 e as demais etapas somente após autorização. O perfil de QA mantém a playlist padrão “QA — Fase 0.3”, com arquivos silenciosos, shuffle ativado e Repeat música; ajuste as preferências antes do uso real.
