using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;

namespace GarrisonDefense
{
    // TASLAK — rapor ajanı dolduracak. Genel (public) imzalar değişmemeli.
    // Ayrı bir CampaignBehavior: istatistikleri kayıtta saklar, savaş sonuçlarını kendisi izler.
    public class GDStatsBehavior : CampaignBehaviorBase
    {
        public static GDStatsBehavior Instance { get; private set; }

        public GDStatsBehavior() { Instance = this; }

        public override void RegisterEvents() { }
        public override void SyncData(IDataStore dataStore) { }

        // Çekirdek davranış bunları çağırır (sadece kayıt amaçlı; oyuncu + AI klanlar için çağrılır).
        public void RecordDispatch(Settlement home, int troops, bool isRelief) { }
        public void RecordReturn(Settlement destination, int troops, int prisoners) { }
        public void RecordReliefPlanned(Settlement besieged, int contributors, int troops) { }

        // Rapor penceresini açar (ayar/menü ajanı çağırır).
        public void ShowReport() { }
    }
}
