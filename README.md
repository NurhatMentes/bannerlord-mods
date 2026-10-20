# Bannerlord Mods

Mount & Blade II: Bannerlord (v1.4.8, Steam ve Xbox Game Pass) için modlar.
Ek mod gerektirmez (MCM/Harmony yok).

| Mod | Klasör | Ne yapar |
|---|---|---|
| **Garrison Defense** (+ Lord Orders) | `GarrisonDefense/` | Garnizonlar bağlı köyleri ve çevreyi korur, kuşatılan kalelere ortak yardım gönderir; ayar ve rapor ekranı. Lordlarına diyalog ya da haberciyle emir verirsin (yağma, kuşatma, erzak, devriye, katıl, takip). |
| **Inn Rest** (örnek) | `src/`, `_Module/` | Şehir menüsünde "hanında dinlen": altınla yaralıları iyileştirir, moral verir. |

Ayrıntılar: [`GarrisonDefense/README.md`](GarrisonDefense/README.md), [`GarrisonDefense/README_LordOrders.md`](GarrisonDefense/README_LordOrders.md).

## Derleme
.NET SDK 8 yeterli; oyun kurulu olmasa da derlenir (referanslar NuGet'teki `Bannerlord.ReferenceAssemblies` paketinden gelir).

    cd GarrisonDefense/src
    dotnet build -c Release
    # çıktı: GarrisonDefense/dist/Modules/GarrisonDefense

`dist/Modules/<Mod>` klasörünü oyunun `Modules` klasörüne kopyala. DLL hem `Win64_Shipping_Client` (Steam)
hem `Gaming.Desktop.x64_Shipping_Client` (Xbox) klasörüne konur.
