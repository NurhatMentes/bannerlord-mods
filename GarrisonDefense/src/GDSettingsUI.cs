using TaleWorlds.CampaignSystem;

namespace GarrisonDefense
{
    // TASLAK — ayar ekranı ajanı dolduracak. Genel (public) imzalar değişmemeli.
    public static class GDSettingsUI
    {
        // GDSettings.Current alanlarını düzenleyen oyun içi pencereyi açar.
        public static void Open() { }

        // Şehir ve kale menülerine "Ayarlar" ve "Rapor" seçeneklerini ekler.
        // Çekirdek davranış OnSessionLaunched içinde çağırır.
        public static void RegisterMenus(CampaignGameStarter starter) { }

        // SubModule.OnApplicationTick'ten her karede çağrılır: kısayol tuşları.
        public static void Tick() { }
    }
}
