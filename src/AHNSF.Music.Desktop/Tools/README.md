# Ferramentas auxiliares para o pacote Windows

Layout final: `Tools/spotdl/spotdl.exe`, `Tools/ffmpeg/ffmpeg.exe` e `Tools/manifest.json`, ao lado de `AHNSF.Music.Desktop.exe`.

O repositório contém apenas o manifesto de versões previstas, com SHA-256 vazio. Executáveis não foram fornecidos nem validados nesta fase. Hash vazio bloqueia a execução. O empacotador precisa adquirir builds auditados, fixar hashes, preservar licenças/atribuições e validar ambos os `--version` antes de publicar. Nunca copiar executável encontrado no PATH ou baixar durante uso do aplicativo.

Deno 2.9.7 é opcional em `Tools/deno/deno.exe` (runtime JavaScript usado pelo yt-dlp). Se presente, `DenoVersion`/`DenoSha256` do manifesto são obrigatórios; um Deno não conferido bloqueia o importador.
