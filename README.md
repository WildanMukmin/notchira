# Notchira

Dynamic island ringan untuk Windows. Duduk di tepi atas layar sebagai notch kecil, membesar saat kursor diarahkan atau saat ada yang terjadi.

Native C# (.NET Framework 4.8, tanpa SDK), tanpa browser engine. Dibangun dengan `csc.exe` bawaan Windows.

## Status: v0.5

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

- **Efek suara** pendek untuk toast, progres selesai, dan klik tombol musik. Bunyinya disintesis di memori (tanpa berkas audio). Letakkan `toast.wav`, `done.wav`, atau `click.wav` di `%APPDATA%\Notchira\sounds\` untuk menggantinya. Pelan secara bawaan; diam saat layar penuh, Focus Assist, island disembunyikan, dan (kecuali klik) saat musik main. Bisa dimatikan dari menu tray.
- Memori dikembalikan ke Windows saat island diam (paling cepat sekali per 30 detik, tidak saat panel terbuka): sekitar 4–6 MB di Task Manager saat idle, naik lagi sesuai kebutuhan.
- **Teleprompter**: menu tray, Teleprompter. Naskah bergulir di tengah atas layar, dekat kamera, tidak ikut terekam, dan tembus klik selama berjalan. Kecepatan dalam kata per menit (60 sampai 400), hitung mundur 3 detik, penanda `[jeda]` berhenti otomatis, sisa waktu baca, mode cermin, naskah tersimpan, impor `.txt` dan `.md`. Saat dijeda, tepi kiri/kanan/bawah bisa diseret untuk mengubah ukuran. Island menyingkir selama teleprompter terbuka.

  | Pintasan | Fungsi |
  |---|---|
  | Ctrl+Alt+Spasi | Jeda dan lanjut |
  | Ctrl+Alt+R | Ulang dari awal |
  | Ctrl+Alt+Kiri, Kanan | Mundur atau maju 5 detik |
  | Ctrl+Alt+=, Ctrl+Alt+- | Lebih cepat atau lebih lambat |
  | Ctrl+Alt+P | Buka editor naskah |
  | Ctrl+Alt+X | Hentikan |

- **Musik YouTube** (menu tray, Musik YouTube): tempel tautan, Notchira mengunduh **audio saja** (m4a) dengan yt-dlp, lalu memutarnya dengan pemutar bawaan. Ada perpustakaan lokal (`%APPDATA%\Notchira\music`), daftar putar, acak, dan ulang (mati / semua / satu). Lagu yang diputar tampil di island dengan sampul, bilah progres, dan tombol yang sama seperti musik dari aplikasi lain. Pemutar dibuat saat lagu mulai dan dibuang saat antrean habis, jadi tidak memakan memori saat diam.
  - yt-dlp tidak ikut di repo. Saat pertama dipakai, klik **Pasang yt-dlp**: Notchira mengunduhnya dari rilis resmi GitHub (sekitar 25 MB) dan mencocokkan SHA-256-nya dengan daftar yang dirilis bersamanya. **Perbarui yt-dlp** memanggil `yt-dlp -U`, karena YouTube sering berubah.
  - Hanya tautan `youtube.com` dan `youtu.be` yang diterima. Pakai untuk lagu yang kamu berhak mengunduh; unduhan dari YouTube tunduk pada ketentuan layanannya.
- Tanpa gerak saat tidak ada yang berubah: bingkai digambar hanya selama animasi (10 ms saat island berubah ukuran, 33 ms saat hanya mata bergerak).

## Rencana

| Versi | Isi |
|---|---|
| 0.3b | Mode Mini (sliver tembus klik) dan paket tema maskot dari PNG |
| 0.6 | Teks lagu untuk lagu YouTube: LRCLIB, lalu caption YouTube, lalu Groq atau Deepgram; tampil 3 baris (sebelum, sekarang, berikutnya) |
| 0.7 | Asisten AI: banyak API key per penyedia dengan rotasi dan fallback; panggilan lewat API |
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
- `NOTCHIRA_DEMO=idle|compact|expanded|toast|lyrics|sleepy|prompter|editor|music|musicplay` tampilkan data palsu, tanpa membaca media.
- `NOTCHIRA_DUMP=<folder>` simpan frame yang dirender sebagai PNG (dipakai bersama DEMO, lalu keluar).
