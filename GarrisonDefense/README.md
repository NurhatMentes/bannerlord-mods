# Garrison Defense — Bannerlord modu

Şehir ve kale garnizonları, kendilerine bağlı köyleri ve yakın çevreyi düşman
lordlarına, düşman devriyelerine ve haydutlara karşı korur.

## Nasıl çalışır
Her oyun saatinde:
1. Senin şehir/kalelerin ve bağlı köylerin çevresinde (6 birim) savaş halinde olduğun bir parti var mı diye bakılır.
2. Varsa, düşman ve hemen yanındaki müttefikleri toplanır (asker sayısı + güç).
3. Garnizondan en güçlü askerlerden başlayarak bir müfreze seçilir. Müfreze düşmandan
   **hem daha kalabalık (en az %20 fazla) hem daha güçlü (en az %30 fazla)** olmalıdır.
4. Toplamda garnizonun **en fazla %70'i** dışarıda olabilir. Bu sınırla düşmanı geçemiyorsa kimse çıkmaz.
5. "X Muhafızları" adlı müfreze düşmana saldırır. Düşman kaçar, yok olur veya bölgeden uzaklaşırsa
   (12 birim) ya da kovalama 36 saati geçerse müfreze geri döner ve garnizona katılır.
   Esirler zindana, yaralılar garnizona geçer.
6. Müfreze her saat güç dengesini yeniden kontrol eder: yaralanıp zayıfladıysa veya düşmana
   takviye geldiyse geri çekilir.
7. Kuşatma yapan ve orduya bağlı partilere müfreze çıkmaz (garnizon onlarla baş edemez).
8. Kale düşmüş veya kuşatma altındaysa müfreze klanın (yoksa krallığın) en yakın güvenli
   kalesine/şehrine gider; hiçbiri yoksa yerinde bekler, askerler silinmez.

## Ayarlar
`src/GarrisonDefenseBehavior.cs` dosyasının başındaki sabitler:
`OnlyPlayerClan`, `RequireGovernor`, `MaxGarrisonShare`, `ThreatRadius`, `LeashRadius`,
`CountMargin`, `PowerMargin`, `MaxChaseHours`.

## Kurulum
`dist/Modules/GarrisonDefense` klasörünü oyunun `Modules` klasörüne kopyala, launcher'da işaretle.
Oyun sürümü: v1.3.x.

## Derleme
    cd src && dotnet build -c Release
