# InnRest — Bannerlord modu

Şehir menüsüne **"Rest at the inn"** seçeneği ekler:

- Ücret: 100 + parti başına asker × 5 altın
- Yaralı askerlerin hepsi iyileşir, partideki kahramanların canı dolar
- Parti moraline +15
- 7 günde bir kullanılabilir (kayıt dosyasına yazılır)

Ayarlar `src/InnRestBehavior.cs` dosyasının başındaki sabitlerde.

## Kurulum (oyuncu)
`dist/Modules/InnRest` klasörünü `...\Mount & Blade II Bannerlord\Modules\` içine kopyala,
launcher'da **Inn Rest** modunu işaretle.

## Derleme (geliştirici)
.NET SDK 8 gerekli. Oyun kurulu olmasa da derlenir (BUTR referans DLL'leri NuGet'ten gelir).

    cd src
    dotnet build -c Release
    # oyuna otomatik kopyalamak için:
    dotnet build -c Release -p:GameFolder="C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord"

Hedef oyun sürümü: v1.2.x / 1.4.8 referans paketi. Farklı bir oyun sürümündeysen
`InnRest.csproj` içindeki `Bannerlord.ReferenceAssemblies` sürümünü değiştir.

## REA ile API keşfi
Yeni özellik eklerken oyunun DLL'lerinde neyin olduğunu görmek için:

    rea inspect-managed-members "<oyun>\bin\Win64_Shipping_Client\TaleWorlds.CampaignSystem.dll" --type TaleWorlds.CampaignSystem.Party.MobileParty --format json
