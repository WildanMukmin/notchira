# Notchira

Dynamic island ringan untuk Windows. Duduk di tepi atas layar sebagai notch kecil, membesar saat kursor diarahkan atau saat ada yang terjadi.

Native C# (.NET Framework 4.8, tanpa SDK), tanpa browser engine. Dibangun dengan `csc.exe` bawaan Windows.

## Status: v0.9

Sudah ada:
- Notch di tepi atas dengan animasi pegas; tiga ukuran: idle, ringkas (sedang memutar musik), dan terbuka.
- **Semua dari island.** Saat terbuka, baris tab di atas panel berisi **Musik**, **Pustaka**, **Asisten**, **Prompter**, dan **Atur**. Island memanjang ke bawah sesuai isi tab. Tempel tautan, tanya asisten, dan semua pengaturan dilakukan di sini; kolom ketiknya kotak teks sungguhan (tempel, IME, Ctrl+A jalan seperti biasa). Island tetap terbuka selama kamu mengetik, **Esc** mengosongkan kolom lalu menutup island. Klik kanan island membuka tab Atur. Tab terakhir diingat selama 20 detik setelah island menutup, setelah itu kembali ke Musik.
- Editor naskah teleprompter dan pengelola Kunci API butuh ruang lebih, jadi keduanya **turun dari tepi atas** dengan bentuk dan warna island (tutup dengan **✕** atau **Esc**). Island menyingkir selama panel itu terbuka.
- Now Playing dari kontrol media Windows (Spotify, browser, VLC, dan lainnya): judul, artis, sampul, bilah progres yang bisa diklik, prev / play-jeda / next.
- Material **Liquid Glass** (latar blur dari layar di belakang island, rim cahaya) atau hitam pekat, dipilih di tab Atur.
- Tray icon hanya cadangan (Tampilkan/Sembunyikan island dan Keluar), mulai bersama Windows, sembunyikan dengan **Ctrl+Alt+H**, otomatis menyingkir saat aplikasi layar penuh.
- Lirik tersinkron dari [LRCLIB](https://lrclib.net): baris aktif tampil di mode ringkas dan di panel. Hasil (termasuk "tidak ada") disimpan di `%APPDATA%\Notchira\lyrics`. Yang dikirim hanya judul, artis, album, dan durasi; bisa dimatikan di tab Atur.
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

- **Efek suara** pendek untuk toast, progres selesai, dan klik tombol musik. Bunyinya disintesis di memori (tanpa berkas audio). Letakkan `toast.wav`, `done.wav`, atau `click.wav` di `%APPDATA%\Notchira\sounds\` untuk menggantinya. Pelan secara bawaan; diam saat layar penuh, Focus Assist, island disembunyikan, dan (kecuali klik) saat musik main. Bisa dimatikan di tab Atur.
- Memori dikembalikan ke Windows saat island diam (paling cepat sekali per 30 detik, tidak saat panel terbuka): sekitar 4–6 MB di Task Manager saat idle, naik lagi sesuai kebutuhan.
- **Teleprompter** (tab Prompter): naskah terakhir, jumlah kata, kecepatan, ukuran huruf, cermin, **Mulai**, dan **Editor naskah**. Naskah bergulir di tengah atas layar, dekat kamera, tidak ikut terekam, dan tembus klik selama berjalan. Kecepatan dalam kata per menit (60 sampai 400), hitung mundur 3 detik, penanda `[jeda]` berhenti otomatis, sisa waktu baca, mode cermin, naskah tersimpan, impor `.txt` dan `.md`. Saat dijeda, tepi kiri/kanan/bawah bisa diseret untuk mengubah ukuran. Island menyingkir selama teleprompter terbuka.

  | Pintasan | Fungsi |
  |---|---|
  | Ctrl+Alt+Spasi | Jeda dan lanjut |
  | Ctrl+Alt+R | Ulang dari awal |
  | Ctrl+Alt+Kiri, Kanan | Mundur atau maju 5 detik |
  | Ctrl+Alt+=, Ctrl+Alt+- | Lebih cepat atau lebih lambat |
  | Ctrl+Alt+P | Buka editor naskah |
  | Ctrl+Alt+X | Hentikan |

- **Pustaka musik** (tab Pustaka): tambah lagu dengan **seret berkas** ke island (tab Pustaka terbuka sendiri), tombol **Berkas...** (mp3, m4a, aac, wav, flac, wma, ogg, opus; folder diambil satu tingkat, maksimal 100 berkas), atau tempel tautan YouTube. Berkas disalin ke `%APPDATA%\Notchira\music`; judul, artis, durasi, dan sampul dibaca dari tag berkas, atau dari nama berkas bila berbentuk "Artis - Judul". Seret berkas tidak jalan saat Mode Mini karena island tembus klik. Untuk YouTube: tempel tautan lalu Enter. Tautan **playlist** (`youtube.com/playlist?list=...`) diunduh semuanya, maksimal 50 lagu, dan otomatis menjadi daftar putar bernama sama dengan playlist-nya; lagu yang gagal dilewati, dan **Batal** menghentikannya. Tautan video yang kebetulan membawa `list=` tetap satu lagu. Untuk satu video, Notchira mengunduh **audio saja** (m4a) dengan yt-dlp, lalu memutarnya dengan pemutar bawaan. Ada perpustakaan lokal (`%APPDATA%\Notchira\music`), daftar putar, acak, dan ulang (mati / semua / satu). Klik baris untuk memutar; arahkan kursor ke baris untuk **+ daftar**, **teks**, dan **hapus** (hapus minta klik kedua, "Yakin?"). Scroll di atas daftar untuk menggulir. Lagu yang diputar tampil di island dengan sampul, bilah progres, dan tombol yang sama seperti musik dari aplikasi lain. Pemutar dibuat saat lagu mulai dan dibuang saat antrean habis, jadi tidak memakan memori saat diam.
  - yt-dlp tidak ikut di repo. Saat pertama dipakai, klik **Pasang yt-dlp**: Notchira mengunduhnya dari rilis resmi GitHub (sekitar 25 MB) dan mencocokkan SHA-256-nya dengan daftar yang dirilis bersamanya. **Perbarui yt-dlp** memanggil `yt-dlp -U`, karena YouTube sering berubah.
  - Hanya tautan `youtube.com` dan `youtu.be` yang diterima. Pakai untuk lagu yang kamu berhak mengunduh; unduhan dari YouTube tunduk pada ketentuan layanannya.
- **Teks lagu untuk lagu YouTube**: saat lagu diputar, Notchira mencari teks bertanda waktu dengan urutan: (1) LRCLIB, dengan judul YouTube dibersihkan dulu ("Artis - Lagu (Official Video)" menjadi artis dan lagu); (2) caption YouTube lewat yt-dlp, dahulu yang diunggah manusia dalam bahasa pilihan (`lyrics.langs`, bawaan `id,en,ja,ko`), lalu track otomatis bahasa asli video, bukan terjemahan mesin; (3) transkrip lewat Groq (Whisper) atau Deepgram, hanya bila kamu mengisi kunci API. Hasilnya disimpan di `music\<id>.lrc`, jadi pencarian hanya sekali per lagu. Hasil LRCLIB dengan kurang dari tiga baris dianggap sampah dan dilewati.
  - Panel terbuka menampilkan **tiga baris**: sebelumnya, sekarang (tebal), berikutnya. Mode ringkas menampilkan baris yang sedang dinyanyikan.
  - **Kunci API** di tab Pustaka atau Atur: satu atau beberapa kunci per layanan (satu per baris). Kunci yang ditolak atau kena batas laju dilewati ke kunci berikutnya. Kunci disimpan terenkripsi DPAPI di akun Windows ini (`keys.dat`), tidak pernah di repo. **Audio lagu dikirim ke layanan itu hanya bila kuncinya ada**; matikan lewat opsi "Buat teks otomatis" atau hapus kuncinya. Groq dibatasi file 25 MB.
  - **Cari teks lagu** mengulang pencarian untuk lagu yang dipilih, termasuk transkrip bila ada kunci.
- **Asisten AI** (tab Asisten, atau **Ctrl+Alt+A** yang langsung membuka tab itu dan siap diketik): tanya-jawab beberapa giliran di dalam island (hanya di memori, dihapus dengan **Baru**). Scroll untuk membaca jawaban panjang, **Salin jawaban** untuk menyalinnya. Bila island tertutup saat jawaban datang, muncul toast. Lewat API, jadi tidak ada model yang berjalan di komputermu. Penyedia: **Groq** dan **Gemini**. **Pilih model** lewat tombol model di sebelah nama penyedia: ada model bawaan (Groq `openai/gpt-oss-120b`, Gemini `gemini-3.5-flash`), beberapa saran, dan model tambahanmu sendiri. **+ Tambah model** memeriksa namanya dulu ke daftar model penyedia, jadi salah ketik langsung ditolak beserta saran nama yang mirip; model tambahan bisa dihapus lagi dari daftar. Alamat API bisa diubah di panel Kunci API. Mata island menunduk fokus selama menunggu jawaban.
  - **Banyak kunci per penyedia** (**Kunci...**): beri nama, jadikan aktif, hapus, pulihkan. Bila kunci aktif ditolak (401/403), kena batas per menit, atau kuotanya habis (429), kunci itu diistirahatkan (2 menit untuk batas per menit, 1 jam untuk kuota habis atau ditolak) dan kunci berikutnya mengambil alih, dengan toast pemberitahuan. Penggantian otomatis bisa dimatikan. Kunci disimpan terenkripsi DPAPI di `keys.dat`, hanya nama dan empat karakter terakhir yang tampil. Kunci Groq dan Deepgram yang sama juga dipakai untuk transkrip lagu.
  - **Pindah penyedia**: bila semua kunci penyedia terpilih sedang istirahat, penyedia lain yang punya kunci menjawab (bisa dimatikan).
  - **Rapikan dengan AI** di editor teleprompter: menulis ulang naskah agar enak dibacakan dan menyisipkan `[jeda]`; hasilnya bisa diurungkan sekali.
  - Hanya pertanyaan yang kamu kirim yang dikirim ke penyedia terpilih, saat kamu mengirimnya. Tidak ada telemetri.
- **Asisten suara** (**Ctrl+Alt+V**, atau tombol mikrofon di tab Asisten): ngobrol langsung dengan suara. Tekan sekali untuk mulai, bicara biasa; penyedia mendeteksi sendiri kapan kamu selesai bicara lalu menjawab dengan suara yang diputar sambil datang. Teks ucapanmu dan jawabannya ikut tercatat di percakapan, dan island ringkas menampilkan kalimat terakhir. Selesai dengan **Selesai**, hotkey lagi, atau otomatis setelah 15 detik diam. Mata maskot melebar saat mendengar, menunduk saat berpikir, dan mengangguk saat bicara.
  - **Penyedia**, dipilih di pemilih model, bagian **Suara**: **Gemini Live** (`gemini-3.1-flash-live-preview` bawaan, termasuk tier gratis Gemini API, bisa bahasa Indonesia) atau **Deepgram Voice Agent** (nova-3 mendengar, GPT-4o mini berpikir, Aura-2 bicara; memakai kredit Deepgram; mengerti bahasa Indonesia, tetapi suara Aura-2 belum ada yang berbahasa Indonesia sehingga terdengar berlogat Inggris). Model Live lain atau suara Aura lain bisa ditambahkan dan diperiksa dulu ke penyedianya.
  - Tanpa peredam gema, suara dari speaker bisa terdengar lagi oleh mikrofon. Karena itu selama asisten bicara mikrofon mengirim hening. Dengan headset, nyalakan **Sela AI saat bicara** di tab Atur supaya bisa memotong jawaban.
  - Audio mikrofon hanya dikirim selama sesi berjalan, ke penyedia yang dipilih. Kunci memakai daftar kunci yang sama (Gemini atau Deepgram) dengan pergantian otomatis bila kena batas.
- **Tampilan saat musik main** (tab Atur, **Saat musik main**): **Judul / lirik** (bawaan) memperlebar island ringkas dengan sampul dan baris lirik atau judul; **Mata saja** membiarkan island tetap kecil, hanya maskot yang tersenyum `^ ^` dan mengangguk.
- **Mode Mini**: island menyusut jadi sliver kecil di tepi atas yang **tembus klik**, jadi tidak pernah menutupi tab browser atau bilah judul. Matanya tetap berkedip, mengikuti kursor, dan berekspresi. Tiga pilihan di tab Atur, **Mode island**:
  - **Normal**: island seperti biasa.
  - **Mini**: selalu sliver.
  - **Otomatis** (bawaan): sliver saat jendela aplikasi lain menutupi tepi atas (misalnya jendela yang dimaksimalkan), atau setelah semenit tanpa aktivitas.
  - Untuk memunculkannya dari Mini, diamkan kursor sebentar (sekitar setengah detik) tepat di atas sliver; island terbuka, lalu kembali ke Mini sekitar 1,5 detik setelah kursor pergi. Pintasan **Ctrl+Alt+K** bolak-balik Normal dan Mini; tanda **^** di pojok kiri atas panel mengecilkannya ke Mini.
  - Toast tetap muncul saat Mini (bisa dimatikan lewat **Notifikasi tetap muncul saat Mini**), lalu island kembali ke sliver.
- Tanpa gerak saat tidak ada yang berubah: bingkai digambar hanya selama animasi (10 ms saat island berubah ukuran, 33 ms saat hanya mata bergerak).

## Rencana

Belum dijadwalkan, urutannya bisa berubah:

- Bahasa ID/EN untuk antarmuka.
- Kontrol sistem dari island: kecerahan, baterai.
- Jawaban asisten bertahap (streaming).
- Notifikasi pesan (opsional).
- Workflow rilis: exe terunggah ke halaman rilis GitHub.
- Terjemahan teks lagu dan paket tema maskot dari PNG (opsional).
- Navigasi keyboard dan dukungan pembaca layar.

API key disimpan terenkripsi (DPAPI) di `%APPDATA%\Notchira`, tidak pernah di repo.

## Pasang

Tidak ada installer: Notchira satu berkas `Notchira.exe` (sekitar 250 KB). Syarat: Windows 10 versi 2004 atau lebih baru (atau Windows 11), 64-bit; .NET Framework 4.8 sudah bawaan Windows.

1. Bangun dengan `build.cmd` (lihat [Build](#build)), atau pakai exe hasil rilis.
2. Taruh di folder tetap, misalnya `D:\Apps\Notchira\`, lalu jalankan. Exe belum ditandatangani, jadi SmartScreen bisa memperingatkan: **More info**, lalu **Run anyway**.
3. Ikon muncul di system tray (di Windows 11 bisa di balik panah **^**). Semua pengaturan ada di tab **Atur** island (arahkan kursor ke island, atau klik kanan island).

**Jalan otomatis.** Sejak pertama dijalankan, Notchira ikut menyala setiap Windows hidup, jadi sudah siaga setelah boot. Untuk menutupnya, tab Atur lalu **Keluar** (klik dua kali), atau klik kanan tray lalu **Keluar** (menyala lagi saat login berikutnya). Untuk mematikan jalan otomatis, matikan **Mulai bersama Windows** di tab Atur; pilihan itu dihormati seterusnya. Kalau exe dipindah, jalurnya diperbarui sendiri pada jalan berikutnya.

**Bisa dicari di Windows.** Tab Atur, **Ada di menu Start** membuat pintasan `Notchira` di menu Start pengguna, sehingga muncul saat mencari "Notchira" di pencarian Windows. Matikan lagi untuk menghapusnya.

Data ada di `%APPDATA%\Notchira` (tab Atur, **Folder data**). Untuk membuang Notchira: matikan jalan otomatis, hapus pintasan Start bila ada, **Keluar**, lalu hapus exe dan folder data.

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
- `NOTCHIRA_DEMO=idle|compact|expanded|toast|lyrics|sleepy|prompter|editor|music|musicplay|pages|lyricstest|keyswin|mini|startmenu|autostart|models|voice|mictest` tampilkan data palsu, tanpa membaca media.
- `NOTCHIRA_AI_BASE_GROQ` dan `_GEMINI` mengganti alamat API asisten (untuk uji dengan server tiruan).
- `NOTCHIRA_GROQ_URL` dan `NOTCHIRA_DEEPGRAM_URL` mengganti alamat layanan transkripsi (untuk uji dengan server tiruan).
- `NOTCHIRA_RUNKEY=<kunci registri>` dan `NOTCHIRA_STARTDIR=<folder>` mengalihkan kunci Run dan folder pintasan Start untuk uji. Salinan uji (`NOTCHIRA_DIR`) tidak menyentuh registri kecuali `NOTCHIRA_RUNKEY` diberikan.
- `NOTCHIRA_DUMP=<folder>` simpan frame yang dirender sebagai PNG (dipakai bersama DEMO, lalu keluar).

## Lisensi

[MIT](LICENSE) © 2026 Wildan Mukmin. Lisensi ini hanya mencakup kode Notchira; yt-dlp dan layanan pihak ketiga (LRCLIB, Groq, Gemini, Deepgram) tunduk pada lisensi dan ketentuan masing-masing.
