# Kontur Code

**Önce bunu okuyun:** Kontur Code, **dosyalarınıza yazabilen ve makinenizde programlar çalıştırabilen bir geliştirme aracıdır.** Bu bir hata değil, ürünün kendisidir. Aşağıdaki ajanın kapsama alanı — ve bilinen tüm açıkları — ile ilgili her şey
[SECURITY.md](SECURITY.md) dosyasındadır. Bunu, önemsediğiniz herhangi bir şeye yönlendirmeden önce okuyun.

[English](README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · [Español](README.es.md) ·
[Français](README.fr.md) · [Português (Brasil)](README.pt-BR.md) · [Italiano](README.it.md) ·
[中文（简体）](README.zh-CN.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Türkçe](README.tr.md)

---

Masaüstü bir LLM istemcisinden **uzamsal bir yapay zekâ geliştirme ortamına** dönüşen bir ürün. Tek pencere, kendi API anahtarlarınız, yerel bir SQLite dosyasındaki konuşmalarınız — ve baktığınız klasörü gerçekten görebileceğiniz bir grafiğe dönüştüren bir çalışma alanı.

Pencerenin altındaki her katmanı iki host paylaşır: bir **WPF uygulaması** ve aynı .NET çekirdeği üzerinde bir **Electron + React** kabuğu. Böylece uygulama iki araç setinden birinin tavanına bağlı kalmaz.

<p align="center">
  <img src="docs/screenshots/canvas.png" alt="Uzamsal tuval: çalışma alanı, kenarları etiketlenmiş bir bağımlılık grafiği olarak" width="100%">
</p>

---

## İçindekiler

- [Ekran görüntüleri](#ekran-görüntüleri)
- [Ne yapar](#ne-yapar)
- [Ajan](#ajan)
- [Gizlilik, kısaca](#gizlilik-kısaca)
- [Gereksinimler](#gereksinimler)
- [Kurulum](#kurulum)
- [İlk çalıştırma](#i&#x307;lk-çalıştırma)
- [Mimari](#mimari)
- [Geliştirme](#geliştirme)
- [Belgeler](#belgeler)
- [Katkıda bulunma](#katkıda-bulunma)
- [Lisans](#lisans)
- [Durum](#durum)

---

## Ekran görüntüleri

### Sohbet

<p align="center">
  <img src="docs/screenshots/chat.png" alt="Bir asistan yanıtı ve çalışma alanı bağlam paneli içeren bir sohbet oturumu" width="100%">
</p>

Jetonlar geldikçe görünür. Yanıtın ortasında durdurun ve **kısmi metin atılmaz, saklanır** — bir sonraki tur için bağlam olarak kullanılabilir kalır.

### Uzamsal tuval

<p align="center">
  <img src="docs/screenshots/canvas.png" alt="Sonsuz tuval üzerinde düğümler ve etiketli bağımlılık kenarları, bir de minimap" width="100%">
</p>

Projenizin bir grafik olarak hâli: dosyalar, klasörler, modüller, servisler, arayüzler, veri, testler ve planlar düğüm olur ve aralarında kapsama ile bağımlılık kenarları bulunur. Kaydırın, yakınlaştırın, dikdörtgen çizerek seçin ve grafiğin tamamının minimap'ini köşede izleyin. Kenarlar etiketlidir — `Login() → CreateTokenAsync` bir çağrı kenarıdır; “yalnızca derleme zamanında” ise hiç çalışmayan bir bağımlılıktır.

### Grafik ana hattı

<p align="center">
  <img src="docs/screenshots/graph-outline.png" alt="Grafiğin düğüm türüne göre gruplanmış, filtrelenebilir ana hat ağacı" width="100%">
</p>

Aynı grafik, okuyup adı ya da yoluna göre filtreleyebileceğiniz bir yapı olarak.

### Düzenleyici

<p align="center">
  <img src="docs/screenshots/editor.png" alt="Sözdizimi vurgulamalı ve bir değişiklik sayacıyla açılmış bir C# dosyası" width="100%">
</p>

On dil bilgisayar kurallarına sahip CodeMirror 6, bir seçim üzerinde satır içi yapay zekâ düzenlemeleri ve hayalet metin tamamlama.

### Ayarlar

<p align="center">
  <img src="docs/screenshots/settings.png" alt="Ayarlar: tema, dil, arayüz ve sohbet varsayılanları" width="100%">
</p>

Tema, arayüz dili, uygulama ölçeği, sistem istemi, örnekleme parametreleri — hepsi yerel, hepsi kendi veritabanınızda saklanır.

---

## Ne yapar

- **Akışlı sohbet.** Jetonlar üretildikçe gelir. Durdurmak kısmi yanıtı korur. Yeniden üretmek onu yerinde değiştirir, isterseniz farklı bir modelle.
- **Üç çalışma modu.** Konuşma için *Sohbet*, analiz için *Cowork* ve *Kod* — ajanın bir çalışma alanı ve bir araç döngüsü aldığı mod. Mod uygulamanın değil mesajın bir özelliğidir, yani “bunu planla, sonra yap” iki kez Ayarlar'a gitmek yerine iki mesajdır.
- **Uzamsal grafik.** Klasörünüz otomatik olarak düğümlere ve kenarlara dizinlenir. Fark tabanlı dizinleyici yeni dosyaları ekler, silinenleri çıkarır ve **sizin yerleştirdiğiniz düzeni korur**. Ajanın ürettiği planlar tuvale düğüm ve kenar kümeleri olarak düşer — geri alınabilir, kalıcı ve reddedebilmenize açıktır.
- **Birleşik çalışma alanı yüzeyleri.** Harita olarak tuval, yapı olarak grafik, bir dosya ağacı, düzenleyici, bir git paneli, bir çalışmanın izi ve görevler görünümü — hepsi bir `Ctrl+Shift+P` uzakta.
- **Git.** Durum, aşamalı ve aşamasız farklar, aşama, commit, branch, revert, push, pull, fetch. Tamamı `git` üzerinden, **kabuk olmadan** ve doğrulanmış argümanlarla.
- **Jeton sayımı.** Canlı kullanım, tahmini maliyet ve modelin bağlamda gerçekte tuttuğu şey — daha eski turları bir özete katlayan **Oturumu sıkıştır** düğmesiyle birlikte.
- **Markdown işleme.** Başlıklar, listeler, tablolar, alıntılar, görev listeleri ve çevrelenmiş kod bloğu — sözdizimi vurgusuyla. Yapılandırılmış içerik olarak işlenir, **hiçbir zaman enjekte edilmiş HTML olarak değil**.
- **Model kataloğu.** Her sağlayıcıdan çekilip SQLite'ta önbelleğe alınır, böylece seçici sonrasında çevrimdışı da çalışır. Bağlam penceresi, fiyat ve yetenekler sağlayıcıdan gelir, sabit bir listeden değil.
- **Hazır iki sağlayıcı** — OpenRouter ve NVIDIA NIM, ikisi de OpenAI uyumlu. NVIDIA'nın uç noktasını yerel bir Ollama, LM Studio veya kendi sunduğunuz bir NIM kapsayıcısına yönlendirin; makinenizden hiçbir şey çıkmaz.
- **Oturum paketleri.** Oturumun tamamını — sohbet, tuval, dosyalar, hedefler — bir `.zip` olarak dışa aktarın.
- **Üç dil.** İngilizce, Rusça ve Almanca; tüm arayüze canlı olarak uygulanır.
- **Açık ve koyu**, sistemi izler ya da sabitlenir.

---

## Ajan

Ajan bir araç döngüsü çalıştırır ve erişebildiği yer, onu kullanmadan önce anlaşılması gereken şeydir.

| | |
| --- | --- |
| **Çalıştığı yer** | Belirlediğiniz tek bir klasör ve dışına okumayı ya da yazmayı reddeder |
| **O klasörün içinde de reddedilenler** | `.git`, `.env`, `credentials.json`, `*.pem`, `*.key`, `*.pfx` — ada göre, her zaman |
| **Önce sorar** | Her yazma, her dış dosya, her ağ isteği, her program |
| **Asla** | Kabuk çalıştırmaz. `&&`, `\|`, `>` ve `$HOME` programın aldığı metindir |
| **Programlar** | Varsayılan olarak kapalı. Sonra yalnızca bir insanın düzenlediği izin listesi. Sonra her çağrı için onay |
| **Geri alma** | Sizin sürüm denetiminiz. Değişiklikler yapıldıktan sonra geri alınmaz, yapılmadan önce gösterilir |

Ret kuralın adını söyler ve modele onun yerine ne yapması gerektiğini bildirir; böylece aynı aracı üst üste üç kez istemeyi bırakır.

**O klasörün dışındaki her şey isteğe bağlıdır ve siz açana kadar kapalıdır.** Ağdan getirme ile proje dışı dosya erişimi Ayarlar'da birbirinden ayrı anahtarlardır ve her çağrı yine de onay isteminden geçer.

> Kapsama modelinin tamamı — ve proje dışı dosyalar için kimlik bilgisi dosya adı kurallarını bir Windows
> junction'ıyla geçilebilir kılan biri dâhil **bilinen sekiz açık** — [SECURITY.md](SECURITY.md) dosyasındadır.
> Bu bir alfa; ona güvenmeden önce okuyun.

---

## Gizlilik, kısaca

- **Telemetri yok. Analiz yok. Çökme raporlaması yok. Hesap yok.** Bu depoda, bu projenin sahibi olduğu herhangi bir adrese bağlantı açan kod yok.
- **Konuşmalarınız hiçbir zaman bir sunucuya uğramaz.** Bunlar kendi kullanıcı profilinizdeki bir SQLite dosyasıdır.
- **API anahtarları şifrelenir** Windows DPAPI ile, Windows hesabınıza bağlanır ve asla bir günlüğe yazılmaz.
- **Makinenizden ne çıkıyor:** bir model sağlayıcısına gönderdiğiniz şeyin tamamı ve yalnızca Gönder'e bastığınızda. Ağ hedeflerinin eksiksiz listesi [PRIVACY.md § 5](PRIVACY.md#5-what-leaves-your-machine-and-who-receives-it) dosyasındadır.
- **Ardışık transkriptler bu uygulama olmadan okunabilir.** Veritabanı diskte şifreli değildir — bu bilinçli bir ödünlemedir, üstü örtülmek yerine belgelenmiştir.
- **Model sağlayıcınız isteminizi görür**, ve işleyeceği şey bu projenin politikası değil *onun* politikasıdır. Başkasının modeli için bir istemcinin yaptığı sözleşme budur.

GDPR, Rus 152-FZ ve CCPA/CPRA'ya göre yazılmış tüm ayrıntılar [PRIVACY.md](PRIVACY.md) dosyasındadır. Orada her şeyi nasıl dışa aktaracağınız ve nasıl sileceğiniz de anlatılır.

---

## Gereksinimler

- Windows 10 sürüm 1809 veya sonrası ya da Windows 11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download) — yalnızca kurulum dosyası için; yayımlanmış bir derleme bunu ister, kaynaklar ise SDK'yı
- [OpenRouter](https://openrouter.ai) ya da [NVIDIA](https://integrate.api.nvidia.com) sitesinden bir API anahtarı
- Yaklaşık 500 MB disk ve bir ajanın okumasına izin vereceğiniz bir klasör

Çapraz platform bir derleme yoktur. DPAPI ve WPF yalnızca Windows'a özeldir ve hedef çatı bunu çalışma zamanında hata vererek değil, baştan söyleyerek bildirir.

---

## Kurulum

Kurulum dosyasını [sürümler sayfasından](https://github.com/rwarx/kontur-code/releases) indirin. Bu, kullanıcı başına bir NSIS kurulumudur — yönetici hakkı gerekmez.

İlk sürüm bir **alfa**. Şekli, üzerine bir şey kurmaya yetecek kadar oturmuş olduğu için yayımlanıyor; denetimsiz kullanıma hazır olduğu için değil.

<details>
<summary>Kendiniz derleyin</summary>

```bash
git clone https://github.com/rwarx/kontur-code.git
cd kontur-code

# Yan süreç, Electron'un onu aradığı yere yanında yayımlanmak zorunda
dotnet publish src/AIClient.Server -c Release -r win-x64 --self-contained false -o electron/sidecar

cd electron
npm install
npm run dist      # → electron/release/
```

.NET çözümünü tek başına derlemek WPF host'unu verir:

```bash
dotnet build AIClient.slnx
dotnet run --project src/AIClient.App
```

</details>

---

## İlk çalıştırma

1. **Ayarlar → Sağlayıcılar**'da bir API anahtarı yapıştırın ve **Yenile**'ye basın. Bir sağlayıcı başarılı olana kadar model seçici boş kalır — katalog sonrasında önbelleğe alınır, dolayısıyla o andan sonra çevrimdışı da çalışır.
2. **Bir klasör açın.** *Kod* modunda onu bir projeye yönlendirin. Grafiğe dizinlenir ve o andan sonra ajanın dünyası o klasördür.
3. **Çalışmaya başlamadan önce commit atın.** İsterseniz boş bir `git commit` yeter. Ajan; hiçbir şey aşamalı değilken ve hiçbir yedeği olmadan doğrudan çalışma ağacınıza yazar; geçmişiniz geri almanın yoludur ve tek yoldur.
4. Komut çalıştırmayı ya da proje dışı dosya erişimini açmayı düşünüyorsanız **[SECURITY.md](SECURITY.md)** dosyasını okuyun. İkisi de varsayılan olarak kapalı ve ikisi de kenarları keskin özellikler.

---

## Mimari

Beş proje, tek bir kural: **bağımlılıklar içeriye doğru bakar.** `Domain` ve `Application` düz `net10.0`'i hedefler, böylece WPF'ye ya da DPAPI'ye uzanmak bir gözden geçirme yorumu değil, derleme hatası olur.

```text
AIClient.Domain ◄──── AIClient.Application ◄──── AIClient.Infrastructure
                          ▲                          ▲            ▲
                          └──────── AIClient.App ────┘            │
                          └──────── AIClient.Server ─────────────┘
```

```text
provider bytes ──► AIStreamEvent ──► ChatTurnEvent ──► the UI
   (SSE frames)       (Domain)          (Application)    (WPF or React)
```

Her biri bir öncekinden daha dar olan üç olay sözlüğü, her sınırdan geçerken çevrilir. Sağlayıcı döndürdüğü tipe bir veritabanı kimliği koyamaz; çünkü döndürdüğü tip, arayüzün tükettiği tip değildir.

Yerel API her çalıştırmada bir bearer token ister ve loopback dışında hiçbir şeye bağlanmayı reddeder — `127.0.0.1` üzerinde olmak bir yetkilendirme sınırı değildir ve kod onu öyle bir sınır gibi de görmez.

Dosya sistemine açılan iki kapı ve iki tuval işleyicisi dâhil olmak üzere tüm gerekçe [ARCHITECTURE.md](ARCHITECTURE.md) dosyasındadır.

---

## Geliştirme

```bash
dotnet build AIClient.slnx     # uyarılar hatadır — bu bilinçlidir
dotnet test                    # 896 test, ne ağ ne de API anahtarı gerekir

cd electron
npm install
npm run typecheck
npm run dev                    # tohumlanmış bir demo çalışma alanına karşı işleyici, arka uca gerek yok
```

Windows ve .NET 10 SDK gerekir. Node 22 yalnızca işleyici için gerekir.

Anlamlı olan ve `.editorconfig`'in ifade edemediği kurallar [CONTRIBUTING.md](CONTRIBUTING.md) dosyasındadır.

---

## Belgeler

| Belge | İçindekiler |
| --- | --- |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Kodun neden bu biçimde olduğu. Yapıyı değiştirmeden önce okuyun. |
| [DEVELOPMENT.md](DEVELOPMENT.md) | Derleme, geçiş, test, genişletme. Bir şeyi değiştirmeden önce okuyun. |
| [SECURITY.md](SECURITY.md) | Tehdit modeli, neyin korunduğu, **ve bilinen açıklar**. |
| [PRIVACY.md](PRIVACY.md) | Hangi veriler var, nereye gidiyor ve haklarınız. GDPR / 152-ФЗ / CCPA. |
| [CHANGELOG.md](CHANGELOG.md) | Her değişiklik, güvenlik düzeltmeleri vurgulanmış olarak. |
| [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) | Paketlenmiş bileşenler ve lisansları. |
| [CONTRIBUTING.md](CONTRIBUTING.md) · [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) · [SUPPORT.md](SUPPORT.md) | Nasıl katılınır. |

---

## Katkıda bulunma

Katkılar memnuniyetle karşılanır ve ajanın güvenlik modeline yapılan değişiklikler için gözden geçirme çıtası bilinçli olarak yüksektir — çünkü o kod makinenizde dosya yazabilir ve programlar çalıştırabilir.

[CONTRIBUTING.md](CONTRIBUTING.md) ile başlayın. Kısacası: her çekme isteğinde tek bir mantıksal değişiklik, `dotnet test` yeşil ve ajanın eriştiği yere dokunuyorsanız açıklamada bunu hangi kapının arkasına koyduğunuzu yazın.

Lütfen **bir güvenlik açığı için herkese açık bir konu açmayın** — özel bildirim için [SECURITY.md](SECURITY.md) dosyasına bakın.

---

## Lisans

**MIT.** [LICENSE](LICENSE) dosyasına bakın.

Üçüncü taraf bileşenler kendi lisanslarını korur — yaklaşık 40 paketlenmiş paketin yanı sıra Electron ve Chromium — ve [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) dosyasında listelenmiştir.

---

## Durum

`0.1.1-alpha`. Bilinçli olarak bir ön sürüm olarak yayımlandı.

**Çalışıyor:** akışlı sohbet, her iki host, uzamsal grafik ve tuval, onay kapısıyla birlikte ajanın araç döngüsü, düzenleyici, git, oturumlar ve paketler, üç dil.

**`0.1.0-alpha`'dan bu yana düzeltilenler** — proje dışı dosyalar kapısındaki iki güvenlik açığı ve çalışma sırasında işin kaybolmasına yol açan iki yol:

- Proje dışı bir dosyayı okumaya verilen tek bir onay, çalışmanın geri kalanında diskin tamamını okuma yetkisi veriyordu. Artık proje dışındaki her işlem kendi sorusunu sorar.
- Proje dışı yollar metin olarak denetlendiği için bir Windows junction'ı kimlik bilgisi dosya adı kurallarını atlayabiliyordu. Artık bağlantılar herhangi bir şey denetlenmeden önce çözülüyor.
- Düzenleyici her tuş vuruşunda dosyanın tamamını yazıyordu. Artık yazmalar debounce'lu, bir **Kaydedilmedi** göstergesiyle ve oturum değiştirmeden, dışa aktarmadan ya da çıkmadan önce otomatik boşaltmayla birlikte.
- İşleyici her dosyanın içeriğini 5–10 MB kotaya karşı tarayıcı deposuna kopyalıyordu ve dolduğunda **sessizce** hiçbir şeyi kaydetmeyi bırakıyordu. Bu kopyalama gitti.

**Hâlâ açık olanlar**, dosya başvurularıyla [SECURITY.md](SECURITY.md#known-gaps) içinde listelendi: konuşmalar diskte şifrelenmiyor (bu bilinçli bir tercih ve zaten Windows hesabınız onları okuyabilir), sidecar'da Kestrel'in varsayılanı dışında istek boyutu ya da hız sınırı yok, Electron'un main ve preload betikleri tip denetiminden geçmiyor ve en yeni ajan araçlarının testi yok.

Bu, arkasında hiçbir finansman olmayan küçük bir projeden gelen `0.x` sürümüdür. Herkesin önünde geliştirilir, konular elden geldiğince yanıtlanır ve bir SLA yoktur. Bir SLA'ya ihtiyacınız varsa bu, bu depoyla değil bir satıcıyla yapılacak bir konuşmadır.

---

<p align="center"><sub>MIT lisanslı. Herkesin önünde geliştirildi. Ekran görüntüleri çalışan uygulamadan alındı.</sub></p>
