# Notchira

Dynamic island ringan untuk Windows. Duduk di tepi atas layar sebagai notch kecil, membesar saat kursor diarahkan atau saat ada yang terjadi.

Native C# (.NET Framework 4.8, tanpa SDK), tanpa browser engine. Dibangun dengan `csc.exe` bawaan Windows.

## Status: v0.9

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
- **Teks lagu untuk lagu YouTube**: saat lagu diputar, Notchira mencari teks bertanda waktu dengan urutan: (1) LRCLIB, dengan judul YouTube dibersihkan dulu ("Artis - Lagu (Official Video)" menjadi artis dan lagu); (2) caption YouTube lewat yt-dlp, dahulu yang diunggah manusia dalam bahasa pilihan (`lyrics.langs`, bawaan `id,en,ja,ko`), lalu track otomatis bahasa asli video, bukan terjemahan mesin; (3) transkrip lewat Groq (Whisper) atau Deepgram, hanya bila kamu mengisi kunci API. Hasilnya disimpan di `music\<id>.lrc`, jadi pencarian hanya sekali per lagu. Hasil LRCLIB dengan kurang dari tiga baris dianggap sampah dan dilewati.
  - Panel terbuka menampilkan **tiga baris**: sebelumnya, sekarang (tebal), berikutnya. Mode ringkas menampilkan baris yang sedang dinyanyikan.
  - **Kunci API...** di jendela Musik YouTube: satu atau beberapa kunci per layanan (satu per baris). Kunci yang ditolak atau kena batas laju dilewati ke kunci berikutnya. Kunci disimpan terenkripsi DPAPI di akun Windows ini (`keys.dat`), tidak pernah di repo. **Audio lagu dikirim ke layanan itu hanya bila kuncinya ada**; matikan lewat opsi "Buat teks otomatis" atau hapus kuncinya. Groq dibatasi file 25 MB.
  - **Cari teks lagu** mengulang pencarian untuk lagu yang dipilih, termasuk transkrip bila ada kunci.
- **Asisten AI** (**Ctrl+Alt+A** atau menu tray): jendela tanya-jawab dengan percakapan beberapa giliran (hanya di memori, dihapus dengan "Percakapan baru"). Lewat API, jadi tidak ada model yang berjalan di komputermu. Penyedia: **Groq** dan **Gemini**. Model dan alamat API masing-masing bisa diubah di jendela Kunci. Mata island menunduk fokus selama menunggu jawaban.
  - **Banyak kunci per penyedia** (**Kunci...**): beri nama, jadikan aktif, hapus, pulihkan. Bila kunci aktif ditolak (401/403), kena batas per menit, atau kuotanya habis (429), kunci itu diistirahatkan (2 menit untuk batas per menit, 1 jam untuk kuota habis atau ditolak) dan kunci berikutnya mengambil alih, dengan toast pemberitahuan. Penggantian otomatis bisa dimatikan. Kunci disimpan terenkripsi DPAPI di `keys.dat`, hanya nama dan empat karakter terakhir yang tampil. Kunci Groq dan Deepgram yang sama juga dipakai untuk transkrip lagu.
  - **Pindah penyedia**: bila semua kunci penyedia terpilih sedang istirahat, penyedia lain yang punya kunci menjawab (bisa dimatikan).
  - **Rapikan dengan AI** di editor teleprompter: menulis ulang naskah agar enak dibacakan dan menyisipkan `[jeda]`; hasilnya bisa diurungkan sekali.
  - Hanya pertanyaan yang kamu kirim yang dikirim ke penyedia terpilih, saat kamu mengirimnya. Tidak ada telemetri.
- **Mode Mini**: island menyusut jadi sliver kecil di tepi atas yang **tembus klik**, jadi tidak pernah menutupi tab browser atau bilah judul. Matanya tetap berkedip, mengikuti kursor, dan berekspresi. Tiga pilihan di menu tray, **Mode island**:
  - **Normal**: island seperti biasa.
  - **Mini**: selalu sliver.
  - **Otomatis** (bawaan): sliver saat jendela aplikasi lain menutupi tepi atas (misalnya jendela yang dimaksimalkan), atau setelah semenit tanpa aktivitas.
  - Untuk memunculkannya dari Mini, diamkan kursor sebentar (sekitar setengah detik) tepat di atas sliver; island terbuka, lalu kembali ke Mini sekitar 1,5 detik setelah kursor pergi. Pintasan **Ctrl+Alt+K** bolak-balik Normal dan Mini; tanda **^** di pojok kiri atas panel mengecilkannya ke Mini.
  - Toast tetap muncul saat Mini (bisa dimatikan lewat **Notifikasi tetap muncul saat Mini**), lalu island kembali ke sliver.
- Tanpa gerak saat tidak ada yang berubah: bingkai digambar hanya selama animasi (10 ms saat island berubah ukuran, 33 ms saat hanya mata bergerak).

## Rencana

| Versi | Isi |
|---|---|
| 0.8 | Jawaban asisten bertahap (streaming), terjemahan teks lagu lewat asisten |
| 0.9 | Paket tema maskot dari PNG (opsional), teks lagu terjemahan |
| 1.0 | Pengaturan, bahasa ID/EN, navigasi keyboard, pembaca layar |

API key disimpan terenkripsi (DPAPI) di `%APPDATA%\Notchira`, tidak pernah di repo.

## Pasang

Tidak ada installer: Notchira satu berkas `Notchira.exe` (sekitar 190 KB). Syarat: Windows 10 versi 2004 atau lebih baru (atau Windows 11), 64-bit; .NET Framework 4.8 sudah bawaan Windows.

1. Bangun dengan `build.cmd` (lihat [Build](#build)), atau pakai exe hasil rilis.
2. Taruh di folder tetap, misalnya `D:\Apps\Notchira\`, lalu jalankan. Exe belum ditandatangani, jadi SmartScreen bisa memperingatkan: **More info**, lalu **Run anyway**.
3. Ikon muncul di system tray (di Windows 11 bisa di balik panah **^**). Klik kanan untuk semua pengaturan.

**Jalan otomatis.** Sejak pertama dijalankan, Notchira ikut menyala setiap Windows hidup, jadi sudah siaga setelah boot. Untuk menutupnya, klik kanan tray lalu **Keluar** (menyala lagi saat login berikutnya). Untuk mematikan jalan otomatis, hilangkan centang **Mulai bersama Windows**; pilihan itu dihormati seterusnya. Kalau exe dipindah, jalurnya diperbarui sendiri pada jalan berikutnya.

**Bisa dicari di Windows.** Tray, **Tambahkan ke menu Start (bisa dicari)** membuat pintasan `Notchira` di menu Start pengguna, sehingga muncul saat mencari "Notchira" di pencarian Windows. Hilangkan centangnya untuk menghapusnya.

Data ada di `%APPDATA%\Notchira` (tray, **Buka folder data**). Untuk membuang Notchira: matikan jalan otomatis, hapus pintasan Start bila ada, **Keluar**, lalu hapus exe dan folder data.

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
- `NOTCHIRA_DEMO=idle|compact|expanded|toast|lyrics|sleepy|prompter|editor|music|musicplay|musicwin|lyricstest|assistantwin|keyswin|mini|startmenu|autostart` tampilkan data palsu, tanpa membaca media.
- `NOTCHIRA_AI_BASE_GROQ` dan `_GEMINI` mengganti alamat API asisten (untuk uji dengan server tiruan).
- `NOTCHIRA_GROQ_URL` dan `NOTCHIRA_DEEPGRAM_URL` mengganti alamat layanan transkripsi (untuk uji dengan server tiruan).
- `NOTCHIRA_RUNKEY=<kunci registri>` dan `NOTCHIRA_STARTDIR=<folder>` mengalihkan kunci Run dan folder pintasan Start untuk uji. Salinan uji (`NOTCHIRA_DIR`) tidak menyentuh registri kecuali `NOTCHIRA_RUNKEY` diberikan.
- `NOTCHIRA_DUMP=<folder>` simpan frame yang dirender sebagai PNG (dipakai bersama DEMO, lalu keluar).
