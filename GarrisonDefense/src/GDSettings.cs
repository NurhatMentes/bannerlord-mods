using TaleWorlds.CampaignSystem;

namespace GarrisonDefense
{
    // Oyun içinden değiştirilebilen ayarlar. Kayıt dosyasında saklanır,
    // her kampanyanın kendi ayarları olur. Ek mod (MCM vb.) gerektirmez.
    public class GDSettings
    {
        public static GDSettings Current { get; private set; } = new GDSettings();

        // --- Kapsam ---
        public bool ApplyToAiLords = false;      // yapay zekâ klanlarının şehir/kaleleri de korusun
        public bool RequireGovernor = false;     // sadece valisi olan yerleşimler müfreze çıkarır
        public bool TargetBandits = true;        // haydutlara müfreze çıkar
        public bool TargetLords = true;          // düşman lord ve devriyelerine müfreze çıkar

        // --- Müfreze ---
        public float MaxGarrisonShare = 0.70f;   // garnizonun en fazla bu kadarı dışarıda olabilir (0.1 - 0.9)
        public float CountMargin = 1.2f;         // asker sayısı düşmanın en az bu katı (her zaman > düşman)
        public float PowerMargin = 1.3f;         // güç düşmanın en az bu katı
        public float ThreatRadius = 6f;          // köy/kale çevresindeki tehdit yarıçapı
        public float LeashRadius = 12f;          // müfreze bu mesafeden uzağa kovalamaz
        public float MaxChaseHours = 36f;
        public bool GovernorTactics = true;      // valinin Taktik becerisi güç paylarını etkiler
        public bool PreferCavalry = true;        // hızlı düşmana önce atlı asker gönder

        // --- Kuşatmaya yardım ---
        public bool SiegeRelief = true;          // kuşatılan kaleye komşu garnizonlar yardım eder
        public float ReliefRadius = 35f;         // bu mesafedeki garnizonlar yardıma katılır

        // --- Bildirim ---
        public bool Notifications = true;        // müfreze çıktı/döndü mesajları (sadece oyuncunun yerleşimleri)
        public bool MonthlyReport = true;        // 30 günde bir özet mesajı

        public static void Reset()
        {
            Current = new GDSettings();
        }

        private const int Version = 1;

        public void Sync(IDataStore dataStore)
        {
            // Ayarların olmadığı eski (v1.0) kayıtlarda tüm ayarlar varsayılan kalır.
            int version = Version;
            dataStore.SyncData("gd_set_version", ref version);
            if (dataStore.IsLoading && version != Version)
                return;

            dataStore.SyncData("gd_set_applyAi", ref ApplyToAiLords);
            dataStore.SyncData("gd_set_reqGov", ref RequireGovernor);
            dataStore.SyncData("gd_set_bandits", ref TargetBandits);
            dataStore.SyncData("gd_set_lords", ref TargetLords);
            dataStore.SyncData("gd_set_share", ref MaxGarrisonShare);
            dataStore.SyncData("gd_set_countMargin", ref CountMargin);
            dataStore.SyncData("gd_set_powerMargin", ref PowerMargin);
            dataStore.SyncData("gd_set_threatRadius", ref ThreatRadius);
            dataStore.SyncData("gd_set_leashRadius", ref LeashRadius);
            dataStore.SyncData("gd_set_chaseHours", ref MaxChaseHours);
            dataStore.SyncData("gd_set_govTactics", ref GovernorTactics);
            dataStore.SyncData("gd_set_cavalry", ref PreferCavalry);
            dataStore.SyncData("gd_set_relief", ref SiegeRelief);
            dataStore.SyncData("gd_set_reliefRadius", ref ReliefRadius);
            dataStore.SyncData("gd_set_notify", ref Notifications);
            dataStore.SyncData("gd_set_monthly", ref MonthlyReport);

            // Eski kayıtlarda alan yoksa 0 gelir; anlamsız değerleri varsayılana çek.
            var d = new GDSettings();
            if (MaxGarrisonShare <= 0f || MaxGarrisonShare > 0.9f) MaxGarrisonShare = d.MaxGarrisonShare;
            if (CountMargin < 1f) CountMargin = d.CountMargin;
            if (PowerMargin < 1f) PowerMargin = d.PowerMargin;
            if (ThreatRadius <= 0f) ThreatRadius = d.ThreatRadius;
            if (LeashRadius < ThreatRadius) LeashRadius = ThreatRadius * 2f;
            if (MaxChaseHours <= 0f) MaxChaseHours = d.MaxChaseHours;
            if (ReliefRadius <= 0f) ReliefRadius = d.ReliefRadius;
        }
    }
}
