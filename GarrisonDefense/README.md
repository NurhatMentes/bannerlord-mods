# Garrison Defense v2.2 — Bannerlord modu (v1.4.8 / Xbox ve Steam)

Lord Orders (lordlara emir verme) bu modun içindedir; ayrıntılar: README_LordOrders.md.

Şehir ve kale garnizonları bağlı köyleri ve çevreyi düşman lordlarına, devriyelere ve haydutlara karşı korur.
Ek mod gerektirmez.

## Özellikler
- **Müfreze:** Tehdit görülünce garnizondan düşmandan hem kalabalık hem güçlü bir müfreze çıkar; en fazla garnizonun %70'i (ayarlanabilir).
  Her saat güç dengesini kontrol eder; zayıflarsa geri çekilir. Dönünce garnizona katılır, esirler zindana geçer.
- **Valinin etkisi:** Valinin Taktik becerisi yüksekse daha küçük güç farkıyla da çıkar; vali yoksa daha temkinli olur.
- **Süvari önceliği:** Haydutlara ve hızlı düşmanlara önce atlılar gönderilir.
- **Kuşatmaya yardım:** Kalen kuşatılınca yakındaki garnizonlar ortak bir yardım kuvveti toplar. Herkes aynı anda varsın diye
  yakındakiler biraz bekler. Güç yetmiyorsa yardım çıkmaz ve sana haber verilir.
- **Yapay zekâ lordları:** Ayarlardan açılırsa AI klanlarının kaleleri de aynı şekilde korunur (varsayılan: kapalı).
  AI garnizonları senin ana partini hedef almaz; müfrezeler birbirini hedef almaz ama güç hesabında sayılır.
- **Rapor:** 30 günlük dönem, önceki dönem ve toplam: müfrezeler, zaferler, dağıtılan haydutlar, esirler, kayıplar.

## Kullanım
- **Ayarlar:** Haritada `Ctrl+Shift+G` ya da herhangi bir şehir/kale menüsünde "Garnizon Savunması: Ayarlar".
- **Rapor:** Haritada `Ctrl+Shift+R` ya da menüde "Garnizon Savunması: Rapor".
- Ayarlar kayıt dosyasına kaydedilir, her kampanyanın kendi ayarları olur.

## Kurulum
`dist/Modules/GarrisonDefense` klasörünü oyunun `Modules` klasörüne kopyala (eskisini sil), launcher'da işaretle.
Ayrı `LordOrders` klasörü varsa **sil** (Xbox launcher'ı iki modu yan yana yüklerken çöküyordu).

## Derleme
    cd src && dotnet build -c Release
