#!/usr/bin/env python3
"""
Извлечь контент нужной версии из лаунчерной content.db в ~/void-client/Resources/.

Через официальный лаунчер клиент целевого сервера запускается из content.db без нашего кода.
Чтобы вставить агента, реконструируем клиент вручную: этот скрипт вытаскивает все файлы
контента конкретной версии (VersionId) на диск, дальше рядом кладётся движок + DLL агента.

Использование:
    python3 extract_content.py <VersionId>

VersionId узнать так:
    sqlite3 -readonly "$HOME/.local/share/Space Station 14/launcher/content.db" \
      "SELECT Id,ForkId,ForkVersion,LastUsed FROM ContentVersion ORDER BY Id DESC LIMIT 5"

Нужен пакет: pip install zstandard
"""
import sqlite3, os, io, sys, time
import zstandard as zstd

if len(sys.argv) < 2 or not sys.argv[1].isdigit():
    print("укажи VersionId: python3 extract_content.py <VID>", file=sys.stderr)
    sys.exit(1)

VID = int(sys.argv[1])
DB = os.path.expanduser("~/.local/share/Space Station 14/launcher/content.db")
OUT = os.path.expanduser("~/void-client/Resources")

con = sqlite3.connect(f"file:{DB}?mode=ro", uri=True)
rows = con.execute(
    "SELECT m.Path, c.Compression, c.Data FROM ContentManifest m "
    "JOIN Content c ON c.Id = m.ContentId WHERE m.VersionId = ?", (VID,)
).fetchall()
if not rows:
    print(f"версия {VID} пуста/не найдена — проверь VersionId", file=sys.stderr)
    sys.exit(1)

dctx = zstd.ZstdDecompressor()
n, t0 = 0, time.time()
for path, comp, data in rows:
    dst = os.path.join(OUT, path)
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    if comp == 2:      # zstd
        blob = dctx.stream_reader(io.BytesIO(data)).read()
    elif comp == 0:    # stored
        blob = data
    else:
        print(f"UNKNOWN compression {comp} for {path}", file=sys.stderr)
        continue
    with open(dst, "wb") as f:
        f.write(blob)
    n += 1
    if n % 3000 == 0:
        print(f"  {n}/{len(rows)} ({time.time()-t0:.0f}s)")

print(f"DONE: {n}/{len(rows)} файлов за {time.time()-t0:.0f}s -> {OUT}")
