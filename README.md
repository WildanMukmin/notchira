# Notchira

Dynamic island ringan untuk Windows. Duduk di tepi atas layar sebagai notch kecil, membesar saat kursor diarahkan atau saat ada yang terjadi.

Native C# (.NET Framework 4.8, tanpa SDK), tanpa browser engine. Dibangun dengan `csc.exe` bawaan Windows.

## Status: v0.1 (kerangka)

Sudah ada:
- Notch di tepi atas dengan animasi pegas; tiga ukuran: idle, ringkas (sedang memutar musik), dan terbuka.
- Now Playing dari kontrol media Windows (Spotify, browser, VLC, dan lainnya): judul, artis, sampul, bilah progres yang bisa diklik, prev / play-jeda / next.
- Material **Liquid Glass** (latar blur dari layar di belakang island, rim cahaya) atau hitam pekat, dipilih dari menu tray.
- Tray icon, mulai bersama Windows, sembunyikan dengan **Ctrl+Alt+H**, otomatis menyingkir saat aplikasi layar penuh.
- Tanpa timer saat diam: menggambar hanya selama animasi atau saat panel terbuka.

## Rencana

| Versi | Isi |
|---|---|
| 0.2 | Lirik tersinkron (LRCLIB), CLI `Notchira.exe --notify`, volume lewat scroll |
| 0.3 | Maskot beranimasi (sprite sheet) dan mata di mode Mini |
| 0.4 | Teleprompter (naskah bergulir di tepi atas, tembus klik, pintasan global) |
| 0.5 | Modul YouTube: unduh audio dengan yt-dlp, ambil transkrip; bila tidak ada, transkrip lewat Groq atau Deepgram |
| 0.6 | Asisten AI: banyak API key per penyedia dengan rotasi dan fallback; panggilan lewat API |
| 1.0 | Pengaturan, bahasa ID/EN, navigasi keyboard, pembaca layar |

API key disimpan terenkripsi (DPAPI) di `%APPDATA%\Notchira`, tidak pernah di repo.

## Build

```
build.cmd
```

Hasilnya `Notchira.exe` di folder yang sama. Syarat: Windows 10 2004 atau lebih baru, 64-bit.

Variabel lingkungan untuk pengembangan:

- `NOTCHIRA_DIR=<folder>` pakai folder data lain dan izinkan instans kedua.
- `NOTCHIRA_DEMO=idle|compact|expanded` tampilkan data palsu, tanpa membaca media.
- `NOTCHIRA_DUMP=<folder>` simpan frame yang dirender sebagai PNG (dipakai bersama DEMO, lalu keluar).
