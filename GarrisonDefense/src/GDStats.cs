using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace GarrisonDefense
{
    // Oyuncunun garnizon müfrezelerinin istatistikleri: 30 günlük dönem, önceki dönem ve toplam.
    // Kayıtta saklanır; savaş sonuçlarını kendisi izler.
    public class GDStatsBehavior : CampaignBehaviorBase
    {
        public static GDStatsBehavior Instance { get; private set; }

        public GDStatsBehavior() { Instance = this; }

        private const float PeriodDays = 30f;

        private enum Stat
        {
            Dispatches, ReliefDispatches, ReliefOperations, Battles, Victories, Defeats,
            EnemyKilled, OwnKilled, OwnWounded, PrisonersDelivered, TroopsReturned,
            EnemyPartiesDestroyed, BanditPartiesDestroyed, Count
        }

        private int[] _period = new int[(int)Stat.Count];
        private int[] _previous = new int[(int)Stat.Count];
        private int[] _total = new int[(int)Stat.Count];
        private CampaignTime _periodStart = CampaignTime.Zero;
        private bool _hasPrevious;

        // Dönem içinde en çok müfreze çıkaran yerleşimler (StringId -> sayı).
        private List<string> _busyIds = new List<string>();
        private List<int> _busyCounts = new List<int>();

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, _ =>
            {
                if (_periodStart == CampaignTime.Zero)
                    _periodStart = CampaignTime.Now;
            });
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTick);
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
            CampaignEvents.MobilePartyDestroyed.AddNonSerializedListener(this, OnPartyDestroyed);
        }

        public override void SyncData(IDataStore dataStore)
        {
            for (int i = 0; i < (int)Stat.Count; i++)
            {
                dataStore.SyncData("gd_stat_p" + i, ref _period[i]);
                dataStore.SyncData("gd_stat_v" + i, ref _previous[i]);
                dataStore.SyncData("gd_stat_t" + i, ref _total[i]);
            }
            dataStore.SyncData("gd_stat_start", ref _periodStart);
            dataStore.SyncData("gd_stat_hasPrev", ref _hasPrevious);
            // Liste yerine tek bir metin olarak saklanır ("id:sayı;id:sayı"), kayıt sistemi için en güvenlisi.
            string busy = string.Join(";", _busyIds.Zip(_busyCounts, (id, n) => id + ":" + n));
            dataStore.SyncData("gd_stat_busy", ref busy);
            if (dataStore.IsLoading)
            {
                _busyIds = new List<string>();
                _busyCounts = new List<int>();
                foreach (string pair in (busy ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] kv = pair.Split(':');
                    if (kv.Length == 2 && int.TryParse(kv[1], out int n))
                    {
                        _busyIds.Add(kv[0]);
                        _busyCounts.Add(n);
                    }
                }
            }
        }

        private void Add(Stat stat, int amount = 1)
        {
            if (amount <= 0)
                return;
            _period[(int)stat] += amount;
            _total[(int)stat] += amount;
        }

        private static bool IsPlayers(Settlement s) => s != null && s.OwnerClan == Clan.PlayerClan;

        private static bool IsPlayerDetachment(MobileParty p)
        {
            return p != null && GarrisonDefenseBehavior.Instance != null
                   && GarrisonDefenseBehavior.Instance.IsDetachment(p) && p.ActualClan == Clan.PlayerClan;
        }

        // ---------------- Çekirdek davranıştan gelen kayıtlar ----------------

        public void RecordDispatch(Settlement home, int troops, bool isRelief)
        {
            if (!IsPlayers(home))
                return;
            Add(isRelief ? Stat.ReliefDispatches : Stat.Dispatches);
            int i = _busyIds.IndexOf(home.StringId);
            if (i < 0)
            {
                _busyIds.Add(home.StringId);
                _busyCounts.Add(1);
            }
            else
            {
                _busyCounts[i]++;
            }
        }

        public void RecordReturn(Settlement destination, int troops, int prisoners)
        {
            if (!IsPlayers(destination))
                return;
            Add(Stat.TroopsReturned, troops);
            Add(Stat.PrisonersDelivered, prisoners);
        }

        public void RecordReliefPlanned(Settlement besieged, int contributors, int troops)
        {
            if (IsPlayers(besieged))
                Add(Stat.ReliefOperations);
        }

        // ---------------- Savaş takibi ----------------

        private void OnMapEventEnded(MapEvent mapEvent)
        {
            try
            {
                foreach (BattleSideEnum sideEnum in new[] { BattleSideEnum.Attacker, BattleSideEnum.Defender })
                {
                    MapEventSide side = mapEvent.GetMapEventSide(sideEnum);
                    MapEventSide other = mapEvent.GetMapEventSide(sideEnum == BattleSideEnum.Attacker ? BattleSideEnum.Defender : BattleSideEnum.Attacker);
                    var ours = side.Parties.Where(p => IsPlayerDetachment(p.Party?.MobileParty)).ToList();
                    if (ours.Count == 0)
                        continue;

                    Add(Stat.Battles);
                    if (mapEvent.HasWinner)
                        Add(mapEvent.WinningSide == sideEnum ? Stat.Victories : Stat.Defeats);
                    Add(Stat.OwnKilled, ours.Sum(p => p.DiedInBattle?.TotalManCount ?? 0));
                    Add(Stat.OwnWounded, ours.Sum(p => p.WoundedInBattle?.TotalManCount ?? 0));
                    Add(Stat.EnemyKilled, other.Parties.Sum(p => p.DiedInBattle?.TotalManCount ?? 0));
                }
            }
            catch (Exception)
            {
                // İstatistik hatası oyunu bozmamalı.
            }
        }

        private void OnPartyDestroyed(MobileParty party, PartyBase destroyer)
        {
            if (party == null || destroyer == null || !IsPlayerDetachment(destroyer.MobileParty))
                return;
            Add(party.IsBandit ? Stat.BanditPartiesDestroyed : Stat.EnemyPartiesDestroyed);
        }

        // ---------------- Dönem ve özet ----------------

        private void OnDailyTick()
        {
            if (_periodStart == CampaignTime.Zero)
                _periodStart = CampaignTime.Now;
            if ((CampaignTime.Now - _periodStart).ToDays < PeriodDays)
                return;

            if (GDSettings.Current.MonthlyReport && _period.Any(v => v > 0))
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"Garnizon Savunması (son 30 gün): {_period[(int)Stat.Dispatches] + _period[(int)Stat.ReliefDispatches]} müfreze, " +
                    $"{_period[(int)Stat.Victories]}/{_period[(int)Stat.Battles]} zafer, " +
                    $"{_period[(int)Stat.BanditPartiesDestroyed]} haydut grubu dağıtıldı, " +
                    $"{_period[(int)Stat.PrisonersDelivered]} esir. Ayrıntı: Ctrl+Shift+R",
                    Colors.Cyan));
            }

            Array.Copy(_period, _previous, _period.Length);
            Array.Clear(_period, 0, _period.Length);
            _hasPrevious = true;
            _busyIds.Clear();
            _busyCounts.Clear();
            _periodStart = CampaignTime.Now;
        }

        private static void AppendBlock(StringBuilder sb, string title, int[] v)
        {
            sb.AppendLine(title);
            sb.AppendLine($"  Çıkan müfreze: {v[(int)Stat.Dispatches]}   Kuşatma yardımı: {v[(int)Stat.ReliefOperations]} operasyon ({v[(int)Stat.ReliefDispatches]} müfreze)");
            sb.AppendLine($"  Savaş: {v[(int)Stat.Battles]}   Zafer: {v[(int)Stat.Victories]}   Yenilgi: {v[(int)Stat.Defeats]}");
            sb.AppendLine($"  Dağıtılan haydut grubu: {v[(int)Stat.BanditPartiesDestroyed]}   Yok edilen düşman partisi: {v[(int)Stat.EnemyPartiesDestroyed]}");
            sb.AppendLine($"  Öldürülen düşman: {v[(int)Stat.EnemyKilled]}   Zindana getirilen esir: {v[(int)Stat.PrisonersDelivered]}");
            sb.AppendLine($"  Kayıplarımız: {v[(int)Stat.OwnKilled]} ölü, {v[(int)Stat.OwnWounded]} yaralı");
            sb.AppendLine();
        }

        public string BuildReport()
        {
            var sb = new StringBuilder();
            int days = _periodStart == CampaignTime.Zero ? 0 : (int)(CampaignTime.Now - _periodStart).ToDays;
            AppendBlock(sb, $"BU DÖNEM (son {days} gün)", _period);

            if (_busyIds.Count > 0)
            {
                var busiest = _busyIds.Zip(_busyCounts, (id, n) => new { id, n })
                    .OrderByDescending(x => x.n).Take(3)
                    .Select(x => $"{Settlement.Find(x.id)?.Name?.ToString() ?? x.id} ({x.n})");
                sb.AppendLine("  En hareketli garnizonlar: " + string.Join(", ", busiest));
                sb.AppendLine();
            }

            if (_hasPrevious)
                AppendBlock(sb, "ÖNCEKİ 30 GÜN", _previous);
            AppendBlock(sb, "TOPLAM", _total);

            List<MobileParty> active = MobileParty.All.Where(IsPlayerDetachment).ToList();
            sb.AppendLine($"ŞU AN SAHADA: {active.Count} müfreze, {active.Sum(p => p.MemberRoster.TotalManCount)} asker");
            foreach (MobileParty p in active.Take(8))
                sb.AppendLine($"  {p.Name}: {p.MemberRoster.TotalHealthyCount} sağlam / {p.MemberRoster.TotalManCount}");

            return sb.ToString();
        }

        // Rapor penceresini açar.
        public void ShowReport()
        {
            if (Campaign.Current == null)
                return;
            InformationManager.ShowInquiry(new InquiryData(
                "Garnizon Savunması — Rapor", BuildReport(),
                true, false, "Tamam", "", null, null), true, false);
        }
    }
}
