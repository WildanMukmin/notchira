# Notchira

Dynamic island ringan untuk Windows. Duduk di tepi atas layar sebagai notch kecil, membesar saat kursor diarahkan atau saat ada yang terjadi.

Native C# (.NET Framework 4.8, tanpa SDK), tanpa browser engine. Dibangun dengan `csc.exe` bawaan Windows.

## Status: v0.3

Sudah ada:
- Notch di tepi atas dengan animasi pegas; tiga ukuran: idle, ringkas (sedang memutar musik), dan terbuka.
- Now Playing dari kontrol media Windows (Spotify, browser, VLC, dan lainnya): judul, artis, sampul, bilah progres yang bisa diklik, prev / play-jeda / next.
- Material **Liquid Glass** (latar blur dari layar di belakang island, rim cahaya) atau hitam pekat, dipilih dari menu tray.
- Tray icon, mulai bersama Windows, sembunyikan dengan **Ctrl+Alt+H**, otomatis menyingkir saat aplikasi layar penuh.
- Lirik tersinkron dari [LRCLIB](https://lrclib.net): baris aktif tampil di mode ringkas dan di panel. Hasil (termasuk "tidak ada") disimpan di `%APPDATA%\Notchira\lyrics`. Yang dikirim hanya judul, artis, album, dan durasi; bisa dimatikan dari menu tray.
- Toast dan bilah progres dari skrip (lihat di bawah).
- Scroll di atas island saat tertutup mengubah volume sistem.
- **Maskot mata**, digambar lewat kode (tanpa aset gambar). Mata mengikuti kursor, berkedip acak (kadang dua kali), dan berubah menurut keadaan:

  | Keadaan | Mata |
  |---|---|
  | Idle | Bulat, berkedip, mengikuti kursor |
  | Musik main | Tersenyum `^ ^`, sesekali mengangguk |
  | Musik dijeda | Setengah terpejam |
  | Kursor di island | Sedikit membesar |
  | Toast | Membesar; menunduk fokus saat ada bilah progres |
  | Tidak ada aktivitas 1 menit | Mengantuk, hampir terpejam |

- Tanpa gerak saat tidak ada yang berubah: bingkai digambar hanya selama animasi (10 ms saat island berubah ukuran, 33 ms saat hanya mata bergerak).

## Rencana

| Versi | Isi |
|---|---|
| 0.3b | Mode Mini (sliver tembus klik) dan paket tema maskot dari PNG |
| 0.4 | Teleprompter (naskah bergulir di tepi atas, tembus klik, pintasan global) |
| 0.5 | Modul YouTube: unduh audio dengan yt-dlp, ambil transkrip; bila tidak ada, transkrip lewat Groq atau Deepgram |
| 0.6 | Asisten AI: banyak API key per penyedia dengan rotasi dan fallback; panggilan lewat API |
| 1.0 | Pengaturan, bahasa ID/EN, navigasi keyboard, pembaca layar |

API key disimpan terenkripsi (DPAPI) di `%APPDATA%\Notchira`, tidak pernah di repo.

## Skrip

```
Notchira.exe --notify "Backup selesai" "12 berkas"
Notchira.exe --notify "Build" "Compiling" --progress 40 --id build
```

Yang pertama memunculkan toast 3,5 detik. Yang kedua menampilkan bilah progres; kirim angka baru dengan `--id` yang sama untuk memperbarui, dan `100` untuk menutup. Pesan lewat named pipe yang hanya bisa dibuka oleh akun Windows yang sama; karakter kontrol dan bidi dibuang, dan toast dibatasi satu per setengah detik. Kode keluar 1 bila Notchira tidak berjalan.

## Build

```
build.cmd
```

Hasilnya `Notchira.exe` di folder yang sama. Syarat: Windows 10 2004 atau lebih baru, 64-bit.

Variabel lingkungan untuk pengembangan:

- `NOTCHIRA_DIR=<folder>` pakai folder data lain dan izinkan instans kedua.
- `NOTCHIRA_DEMO=idle|compact|expanded|toast|lyrics|sleepy` tampilkan data palsu, tanpa membaca media.
- `NOTCHIRA_DUMP=<folder>` simpan frame yang dirender sebagai PNG (dipakai bersama DEMO, lalu keluar).
