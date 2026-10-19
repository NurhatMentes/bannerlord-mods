using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace GarrisonDefense
{
    // Şehir/kale garnizonları, kendilerine bağlı köyleri ve yakın çevreyi
    // düşman lordlarına ve haydutlara karşı korur. Tehdit görüldüğünde
    // garnizondan düşmandan daha kalabalık ve güçlü bir müfreze çıkar,
    // düşman gidince müfreze garnizona geri döner.
    public class GarrisonDefenseBehavior : CampaignBehaviorBase
    {
        // --- Ayarlar ---
        private const bool OnlyPlayerClan = true;      // false: tüm klanların şehir/kaleleri
        private const bool RequireGovernor = false;    // true: sadece valisi olan yerleşimler
        private const float MaxGarrisonShare = 0.70f;  // garnizonun en fazla %70'i dışarıda olabilir
        private const float ThreatRadius = 6f;         // köy/kale çevresinde tehdit yarıçapı (harita birimi)
        private const float LeashRadius = 12f;         // müfreze bu mesafeden uzağa kovalamaz
        private const float GroupRadius = 3f;          // hedefin yanındaki düşmanlar da hesaba katılır
        private const float CountMargin = 1.2f;        // asker sayısı düşmanın en az 1.2 katı (ve fazlası)
        private const float PowerMargin = 1.3f;        // güç düşmanın en az 1.3 katı
        private const float MaxChaseHours = 36f;       // bu süreden uzun kovalamaz
        private const int MinDetachmentSize = 5;

        public static GarrisonDefenseBehavior Instance { get; private set; }

        public GarrisonDefenseBehavior() { Instance = this; }

        // Bu parti modun çıkardığı bir garnizon müfrezesi mi? (rapor ve arayüz kullanır)
        public bool IsDetachment(MobileParty party) => party != null && _detachments.Contains(party);

        private List<MobileParty> _detachments = new List<MobileParty>();

        // Kaydedilmez; yüklemeden sonra her saat başı yeniden hesaplanır.
        private readonly Dictionary<MobileParty, MobileParty> _targets = new Dictionary<MobileParty, MobileParty>();
        private readonly Dictionary<MobileParty, CampaignTime> _chaseStart = new Dictionary<MobileParty, CampaignTime>();
        private readonly HashSet<MobileParty> _returning = new HashSet<MobileParty>();

        public override void RegisterEvents()
        {
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, OnHourlyTick);
            CampaignEvents.MobilePartyDestroyed.AddNonSerializedListener(this, OnPartyDestroyed);
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("garrisonDefense_detachments", ref _detachments);
            if (_detachments == null)
                _detachments = new List<MobileParty>();
        }

        private void OnPartyDestroyed(MobileParty party, PartyBase destroyer)
        {
            Forget(party);
            foreach (var key in _targets.Where(kv => kv.Value == party).Select(kv => kv.Key).ToList())
                _targets.Remove(key);
        }

        private void Forget(MobileParty party)
        {
            _detachments.Remove(party);
            _targets.Remove(party);
            _chaseStart.Remove(party);
            _returning.Remove(party);
        }

        private void OnHourlyTick()
        {
            _detachments.RemoveAll(p => p == null || !p.IsActive);

            List<MobileParty> hostiles = CollectPotentialHostiles();

            foreach (MobileParty detachment in _detachments.ToList())
                UpdateDetachment(detachment, hostiles);

            foreach (Settlement settlement in Settlement.All)
            {
                if (IsDefendedSettlement(settlement))
                    TryDispatch(settlement, hostiles);
            }
        }

        // ---------------- Tehdit tespiti ----------------

        private static List<MobileParty> CollectPotentialHostiles()
        {
            var result = new List<MobileParty>();
            foreach (MobileParty p in MobileParty.All)
            {
                if (!p.IsActive || p.CurrentSettlement != null || p.IsCurrentlyAtSea || p.MapFaction == null)
                    continue;
                if (!(p.IsLordParty || p.IsBandit || p.IsPatrolParty))
                    continue;
                if (p.MemberRoster.TotalHealthyCount <= 0)
                    continue;
                result.Add(p);
            }
            return result;
        }

        private static bool IsDefendedSettlement(Settlement s)
        {
            if (!s.IsFortification || s.Town == null || s.OwnerClan == null)
                return false;
            if (OnlyPlayerClan && s.OwnerClan != Clan.PlayerClan)
                return false;
            if (RequireGovernor && s.Town.Governor == null)
                return false;
            return true;
        }

        private static IEnumerable<CampaignVec2> ProtectedPoints(Settlement s)
        {
            yield return s.Position;
            foreach (Village v in s.BoundVillages)
                yield return v.Settlement.Position;
        }

        private static float DistanceToZone(Settlement s, MobileParty p)
        {
            float best = float.MaxValue;
            foreach (CampaignVec2 point in ProtectedPoints(s))
                best = MathF.Min(best, point.Distance(p.Position));
            return best;
        }

        private static bool IsHostileTo(MobileParty p, Settlement s)
        {
            return p.MapFaction != null && s.MapFaction != null && p.MapFaction.IsAtWarWith(s.MapFaction);
        }

        // Hedef ve yanındaki düşmanların toplam sayısı ve gücü.
        private static void EnemyGroupStrength(MobileParty target, Settlement home, List<MobileParty> hostiles, out int count, out float power)
        {
            count = 0;
            power = 0f;
            foreach (MobileParty p in hostiles)
            {
                if (p != target && (!IsHostileTo(p, home) || p.Position.Distance(target.Position) > GroupRadius))
                    continue;
                count += p.MemberRoster.TotalHealthyCount;
                power += RosterPower(p.MemberRoster);
            }
        }

        private static float RosterPower(TroopRoster roster)
        {
            float power = 0f;
            foreach (TroopRosterElement e in roster.GetTroopRoster())
            {
                int healthy = e.Number - e.WoundedNumber;
                if (healthy > 0 && e.Character != null)
                    power += e.Character.GetPower() * healthy;
            }
            return power;
        }

        // ---------------- Müfreze gönderme ----------------

        private void TryDispatch(Settlement home, List<MobileParty> hostiles)
        {
            MobileParty garrison = home.Town.GarrisonParty;
            if (garrison == null || home.IsUnderSiege || garrison.MapEvent != null || home.Party.MapEvent != null)
                return;

            int atHome = garrison.MemberRoster.TotalManCount;
            int alreadyOut = _detachments.Where(d => d.HomeSettlement == home).Sum(d => d.MemberRoster.TotalManCount);
            int budget = MathF.Min(garrison.MemberRoster.TotalHealthyCount,
                (int)((atHome + alreadyOut) * MaxGarrisonShare) - alreadyOut);
            if (budget < MinDetachmentSize)
                return;

            MobileParty target = hostiles
                .Where(p => IsValidThreat(p, home) && !IsTargeted(p))
                .Select(p => new { Party = p, Dist = DistanceToZone(home, p) })
                .Where(x => x.Dist <= ThreatRadius)
                .OrderBy(x => x.Dist)
                .Select(x => x.Party)
                .FirstOrDefault();
            if (target == null)
                return;

            EnemyGroupStrength(target, home, hostiles, out int enemyCount, out float enemyPower);
            TroopRoster detachmentRoster = PickTroops(garrison.MemberRoster, budget, enemyCount, enemyPower);
            if (detachmentRoster == null)
                return; // %70 ile bile üstün gelemiyoruz, garnizon yerinde kalır

            foreach (TroopRosterElement e in detachmentRoster.GetTroopRoster())
                garrison.MemberRoster.AddToCounts(e.Character, -e.Number);

            TextObject name = new TextObject("{=gd_party_name}{SETTLEMENT} Muhafızları")
                .SetTextVariable("SETTLEMENT", home.Name);

            MobileParty detachment = CustomPartyComponent.CreateCustomPartyWithTroopRoster(
                home.GatePosition, 0.5f, home, name, home.OwnerClan,
                detachmentRoster, TroopRoster.CreateDummyTroopRoster(), home.OwnerClan.Leader,
                "", "", 0f, false);

            int food = MathF.Max(5, detachmentRoster.TotalManCount / 5);
            detachment.ItemRoster.AddToCounts(DefaultItems.Grain, food);
            detachment.Ai.SetDoNotMakeNewDecisions(true);

            _detachments.Add(detachment);
            Engage(detachment, target);

            if (home.OwnerClan == Clan.PlayerClan)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    new TextObject("{=gd_sent}{SETTLEMENT} garnizonu {COUNT} askerle {ENEMY} ({ENEMY_COUNT}) üzerine yürüyor.")
                        .SetTextVariable("SETTLEMENT", home.Name)
                        .SetTextVariable("COUNT", detachmentRoster.TotalManCount)
                        .SetTextVariable("ENEMY", target.Name)
                        .SetTextVariable("ENEMY_COUNT", enemyCount).ToString(),
                    Colors.Yellow));
            }
        }

        // Garnizondan en güçlü askerlerden başlayarak, düşmandan hem daha
        // kalabalık hem daha güçlü bir kadro seçer. Bütçeyle mümkün değilse null.
        private static TroopRoster PickTroops(TroopRoster garrison, int budget, int enemyCount, float enemyPower)
        {
            int neededCount = MathF.Max(MinDetachmentSize, MathF.Max(enemyCount + 1, MathF.Ceiling(enemyCount * CountMargin)));
            float neededPower = enemyPower * PowerMargin;
            if (neededCount > budget)
                return null;

            var available = garrison.GetTroopRoster()
                .Where(e => e.Character != null && !e.Character.IsHero && e.Number - e.WoundedNumber > 0)
                .OrderByDescending(e => e.Character.GetPower())
                .ToList();

            TroopRoster picked = TroopRoster.CreateDummyTroopRoster();
            int count = 0;
            float power = 0f;
            foreach (TroopRosterElement e in available)
            {
                int healthy = e.Number - e.WoundedNumber;
                float unitPower = e.Character.GetPower();
                int take = 0;
                while (take < healthy && count < budget && (count < neededCount || power < neededPower))
                {
                    take++;
                    count++;
                    power += unitPower;
                }
                if (take > 0)
                    picked.AddToCounts(e.Character, take);
                if (count >= neededCount && power >= neededPower)
                    return picked;
                if (count >= budget)
                    break;
            }
            return null;
        }

        // Hedef zaten başka bir müfrezenin hedefi ya da onun hemen yanındaysa (aynı grup) true.
        private bool IsTargeted(MobileParty enemy)
        {
            return _targets.Values.Any(t => t != null && t.IsActive
                && (t == enemy || t.Position.Distance(enemy.Position) <= GroupRadius));
        }

        // Kuşatma yapan veya bir orduya bağlı partilerle uğraşılmaz; garnizon bunlara tek başına çıkmaz.
        private static bool IsValidThreat(MobileParty p, Settlement home)
        {
            return IsHostileTo(p, home) && p.BesiegedSettlement == null && p.Army == null;
        }

        private static bool CanBeat(int ownCount, float ownPower, int enemyCount, float enemyPower)
        {
            return ownCount > enemyCount && ownCount >= enemyCount * CountMargin && ownPower >= enemyPower * PowerMargin;
        }

        private void Engage(MobileParty detachment, MobileParty target)
        {
            _returning.Remove(detachment);
            _targets[detachment] = target;
            if (!_chaseStart.ContainsKey(detachment))
                _chaseStart[detachment] = CampaignTime.Now;
            detachment.SetMoveEngageParty(target, MobileParty.NavigationType.Default);
        }

        // ---------------- Müfreze yaşam döngüsü ----------------

        private void UpdateDetachment(MobileParty detachment, List<MobileParty> hostiles)
        {
            if (detachment.MapEvent != null)
                return;

            if (detachment.MemberRoster.TotalManCount <= 0)
            {
                DestroyPartyAction.Apply(null, detachment);
                Forget(detachment);
                return;
            }

            Settlement home = detachment.HomeSettlement;

            if (_returning.Contains(detachment))
            {
                Settlement dest = ReturnDestination(detachment);
                if (dest == null)
                {
                    // Gidilecek güvenli kale yok (ör. tek kalemiz kuşatmada): yerinde bekle, her saat tekrar dene.
                    detachment.SetMoveModeHold();
                    TopUpFood(detachment);
                }
                else if (detachment.CurrentSettlement == dest)
                {
                    MergeIntoGarrison(detachment, dest);
                }
                else
                {
                    detachment.SetMoveGoToSettlement(dest, MobileParty.NavigationType.Default, false);
                }
                return;
            }

            bool homeLost = home == null || !IsDefendedSettlement(home);
            bool chasedTooLong = _chaseStart.TryGetValue(detachment, out CampaignTime start)
                                 && (CampaignTime.Now - start).ToHours > MaxChaseHours;

            _targets.TryGetValue(detachment, out MobileParty target);
            bool targetValid = !homeLost && target != null && target.IsActive && target.CurrentSettlement == null
                               && !target.IsCurrentlyAtSea && IsValidThreat(target, home)
                               && DistanceToZone(home, target) <= LeashRadius;
            if (targetValid)
            {
                // Savaştan yaralı çıktıysak ya da düşmana takviye geldiyse artık saldırmayız.
                EnemyGroupStrength(target, home, hostiles, out int enemyCount, out float enemyPower);
                targetValid = CanBeat(detachment.MemberRoster.TotalHealthyCount, RosterPower(detachment.MemberRoster),
                                      enemyCount, enemyPower);
            }

            if (!homeLost && !chasedTooLong && !targetValid)
            {
                _targets.Remove(detachment);
                _chaseStart.Remove(detachment);
                target = FindNewTarget(detachment, home, hostiles);
                targetValid = target != null;
            }

            if (targetValid && !chasedTooLong)
            {
                Engage(detachment, target);
                TopUpFood(detachment);
            }
            else
            {
                StartReturn(detachment);
            }
        }

        private MobileParty FindNewTarget(MobileParty detachment, Settlement home, List<MobileParty> hostiles)
        {
            int ownCount = detachment.MemberRoster.TotalHealthyCount;
            float ownPower = RosterPower(detachment.MemberRoster);
            foreach (MobileParty p in hostiles
                         .Where(p => IsValidThreat(p, home) && !IsTargeted(p))
                         .Where(p => DistanceToZone(home, p) <= ThreatRadius)
                         .OrderBy(p => p.Position.Distance(detachment.Position)))
            {
                EnemyGroupStrength(p, home, hostiles, out int enemyCount, out float enemyPower);
                if (CanBeat(ownCount, ownPower, enemyCount, enemyPower))
                    return p;
            }
            return null;
        }

        private void StartReturn(MobileParty detachment)
        {
            _targets.Remove(detachment);
            _chaseStart.Remove(detachment);
            _returning.Add(detachment);
            Settlement dest = ReturnDestination(detachment);
            if (dest != null)
                detachment.SetMoveGoToSettlement(dest, MobileParty.NavigationType.Default, false);
        }

        // Normalde kendi kalesi; kale düşmüşse veya kuşatmadaysa klanın en yakın uygun kalesi.
        private static Settlement ReturnDestination(MobileParty detachment)
        {
            Settlement home = detachment.HomeSettlement;
            Clan clan = detachment.ActualClan;
            if (home != null && home.OwnerClan == clan && !home.IsUnderSiege)
                return home;
            if (clan == null)
                return null;
            Settlement best = clan.Settlements
                .Where(s => s.IsFortification && s.Town != null && !s.IsUnderSiege)
                .OrderBy(s => s.Position.Distance(detachment.Position))
                .FirstOrDefault();
            if (best != null || clan.Kingdom == null)
                return best;
            // Klanın başka kalesi yoksa krallıktaki en yakın güvenli kale/şehir.
            return clan.Kingdom.Settlements
                .Where(s => s.IsFortification && s.Town != null && !s.IsUnderSiege)
                .OrderBy(s => s.Position.Distance(detachment.Position))
                .FirstOrDefault();
        }

        private void MergeIntoGarrison(MobileParty detachment, Settlement dest)
        {
            if (dest.Town.GarrisonParty == null)
                dest.AddGarrisonParty();
            MobileParty garrison = dest.Town.GarrisonParty;

            int returned = detachment.MemberRoster.TotalManCount;
            garrison.MemberRoster.Add(detachment.MemberRoster);
            detachment.MemberRoster.Clear();

            // Esir lordların tutsaklık durumu doğru güncellensin diye oyunun kendi aksiyonu kullanılır.
            foreach (TroopRosterElement e in detachment.PrisonRoster.GetTroopRoster().ToList())
            {
                if (e.Character.IsHero)
                    TransferPrisonerAction.Apply(e.Character, detachment.Party, dest.Party);
            }
            dest.Party.PrisonRoster.Add(detachment.PrisonRoster);
            detachment.PrisonRoster.Clear();

            if (dest.OwnerClan == Clan.PlayerClan)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    new TextObject("{=gd_back}{COUNT} muhafız {SETTLEMENT} garnizonuna geri döndü.")
                        .SetTextVariable("COUNT", returned)
                        .SetTextVariable("SETTLEMENT", dest.Name).ToString(),
                    Colors.Green));
            }

            DestroyPartyAction.ApplyForDisbanding(detachment, dest);
            Forget(detachment);
        }

        private static void TopUpFood(MobileParty detachment)
        {
            int wanted = MathF.Max(3, detachment.MemberRoster.TotalManCount / 10);
            int have = detachment.ItemRoster.GetItemNumber(DefaultItems.Grain);
            if (have < wanted)
                detachment.ItemRoster.AddToCounts(DefaultItems.Grain, wanted - have);
        }
    }
}
