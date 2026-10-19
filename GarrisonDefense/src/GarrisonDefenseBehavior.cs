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
    // düşman gidince müfreze garnizona geri döner. Kuşatılan bir kaleye
    // komşu garnizonlar ortak yardım kuvveti gönderir.
    // Ayarlar: GDSettings (oyun içinden değiştirilebilir).
    public class GarrisonDefenseBehavior : CampaignBehaviorBase
    {
        private const float GroupRadius = 3f;          // hedefin yanındaki düşmanlar da hesaba katılır
        private const int MinDetachmentSize = 5;
        private const float MaxReliefHours = 72f;      // yardım kuvveti en fazla bu kadar sahada kalır
        private const float FastEnemySpeed = 5f;       // bundan hızlı düşmana atlılar öncelikli

        private static GDSettings S => GDSettings.Current;

        public static GarrisonDefenseBehavior Instance { get; private set; }

        public GarrisonDefenseBehavior() { Instance = this; }

        // Bu parti modun çıkardığı bir garnizon müfrezesi mi? (rapor ve arayüz kullanır)
        public bool IsDetachment(MobileParty party) => party != null && _detachments.Contains(party);

        // Bu oturumda çıkmış (şimdi yok olmuş olabilir) bir müfreze mi? Savaşta yok olan müfrezenin
        // savaşı da rapora girsin diye kullanılır.
        public bool WasDetachment(MobileParty party) => party != null && (_everSpawned.Contains(party) || IsDetachment(party));

        private readonly HashSet<MobileParty> _everSpawned = new HashSet<MobileParty>();

        private List<MobileParty> _detachments = new List<MobileParty>();

        // Kaydedilmez; yüklemeden sonra her saat başı yeniden hesaplanır.
        // (Yükleme sırasında yoldaki yardım kuvvetleri evlerine döner.)
        private readonly Dictionary<MobileParty, MobileParty> _targets = new Dictionary<MobileParty, MobileParty>();
        private readonly Dictionary<MobileParty, CampaignTime> _chaseStart = new Dictionary<MobileParty, CampaignTime>();
        private readonly HashSet<MobileParty> _returning = new HashSet<MobileParty>();
        private readonly Dictionary<MobileParty, Settlement> _reliefTarget = new Dictionary<MobileParty, Settlement>();
        private readonly Dictionary<MobileParty, CampaignTime> _departAt = new Dictionary<MobileParty, CampaignTime>();
        private readonly Dictionary<Settlement, CampaignTime> _reliefStart = new Dictionary<Settlement, CampaignTime>();
        private readonly HashSet<Settlement> _reliefImpossibleNotified = new HashSet<Settlement>();

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, OnHourlyTick);
            CampaignEvents.MobilePartyDestroyed.AddNonSerializedListener(this, OnPartyDestroyed);
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("garrisonDefense_detachments", ref _detachments);
            if (_detachments == null)
                _detachments = new List<MobileParty>();
            GDSettings.Current.Sync(dataStore);
        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            GDSettingsUI.RegisterMenus(starter);
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
            _reliefTarget.Remove(party);
            _departAt.Remove(party);
        }

        private void OnHourlyTick()
        {
            _detachments.RemoveAll(p => p == null || !p.IsActive);

            List<MobileParty> hostiles = CollectPotentialHostiles();

            if (S.SiegeRelief)
                UpdateReliefOperations(hostiles);

            foreach (MobileParty detachment in _detachments.ToList())
                UpdateDetachment(detachment, hostiles);

            foreach (Settlement settlement in Settlement.All)
            {
                if (IsDefendedSettlement(settlement))
                    TryDispatch(settlement, hostiles);
            }
        }

        private static bool IsPlayers(Settlement s) => s != null && s.OwnerClan == Clan.PlayerClan;

        private static void Notify(Settlement s, TextObject text, Color color)
        {
            if (S.Notifications && IsPlayers(s))
                InformationManager.DisplayMessage(new InformationMessage(text.ToString(), color));
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
            if (s.OwnerClan != Clan.PlayerClan && (!S.ApplyToAiLords || s.OwnerClan.IsBanditFaction))
                return false;
            if (S.RequireGovernor && s.Town.Governor == null)
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

        // Kuşatma yapan veya bir orduya bağlı partilerle uğraşılmaz; garnizon bunlara tek başına çıkmaz.
        // Haydut/lord hedefleri ayarlardan kapatılabilir.
        private static bool IsValidThreat(MobileParty p, Settlement home)
        {
            if (!IsHostileTo(p, home) || p.BesiegedSettlement != null || p.Army != null)
                return false;
            return p.IsBandit ? S.TargetBandits : S.TargetLords;
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

        // ---------------- Güç payı ve vali ----------------

        // Valinin Taktik becerisi güç paylarını ölçekler: valisiz/beceriksiz 1.1 (temkinli),
        // 200+ Taktik 0.85 (cesur). Asker sayısı yine de her zaman düşmandan fazla olmalı.
        private static float MarginFactor(Settlement home)
        {
            if (!S.GovernorTactics || home?.Town == null)
                return 1f;
            Hero governor = home.Town.Governor;
            if (governor == null)
                return 1.1f;
            float tactics = MathF.Clamp(governor.GetSkillValue(DefaultSkills.Tactics) / 200f, 0f, 1f);
            return 1.1f - 0.25f * tactics;
        }

        private static int NeededCount(int enemyCount, float factor)
        {
            float margin = MathF.Max(1f, S.CountMargin * factor);
            return MathF.Max(MinDetachmentSize, MathF.Max(enemyCount + 1, MathF.Ceiling(enemyCount * margin)));
        }

        private static float NeededPower(float enemyPower, float factor)
        {
            return enemyPower * MathF.Max(1f, S.PowerMargin * factor);
        }

        private static bool CanBeat(int ownCount, float ownPower, int enemyCount, float enemyPower, float factor)
        {
            float margin = MathF.Max(1f, S.CountMargin * factor);
            return ownCount > enemyCount && ownCount >= enemyCount * margin
                   && ownPower >= NeededPower(enemyPower, factor);
        }

        // ---------------- Müfreze gönderme ----------------

        private int Budget(Settlement home)
        {
            MobileParty garrison = home.Town.GarrisonParty;
            if (garrison == null)
                return 0;
            int atHome = garrison.MemberRoster.TotalManCount;
            int alreadyOut = _detachments.Where(d => d.HomeSettlement == home).Sum(d => d.MemberRoster.TotalManCount);
            return MathF.Min(garrison.MemberRoster.TotalHealthyCount,
                (int)((atHome + alreadyOut) * S.MaxGarrisonShare) - alreadyOut);
        }

        private void TryDispatch(Settlement home, List<MobileParty> hostiles)
        {
            MobileParty garrison = home.Town.GarrisonParty;
            if (garrison == null || home.IsUnderSiege || garrison.MapEvent != null || home.Party.MapEvent != null)
                return;

            int budget = Budget(home);
            if (budget < MinDetachmentSize)
                return;

            // Ucuz ön eleme: kaleden çok uzaktaki partilere hiç bakma.
            float reach = S.ThreatRadius + 25f;
            MobileParty target = hostiles
                .Where(p => p.Position.Distance(home.Position) <= reach)
                .Where(p => IsValidThreat(p, home) && !IsTargeted(p))
                .Select(p => new { Party = p, Dist = DistanceToZone(home, p) })
                .Where(x => x.Dist <= S.ThreatRadius)
                .OrderBy(x => x.Dist)
                .Select(x => x.Party)
                .FirstOrDefault();
            if (target == null)
                return;

            float factor = MarginFactor(home);
            EnemyGroupStrength(target, home, hostiles, out int enemyCount, out float enemyPower);
            bool preferCavalry = S.PreferCavalry && (target.IsBandit || target.Speed > FastEnemySpeed);
            TroopRoster detachmentRoster = PickTroops(garrison.MemberRoster, budget,
                NeededCount(enemyCount, factor), NeededPower(enemyPower, factor), preferCavalry, false);
            if (detachmentRoster == null)
                return; // bütçeyle üstün gelemiyoruz, garnizon yerinde kalır

            MobileParty detachment = SpawnDetachment(home, detachmentRoster);
            Engage(detachment, target);
            GDStatsBehavior.Instance?.RecordDispatch(home, detachmentRoster.TotalManCount, false);

            Notify(home, new TextObject("{=gd_sent}{SETTLEMENT} garnizonu {COUNT} askerle {ENEMY} ({ENEMY_COUNT}) üzerine yürüyor.")
                    .SetTextVariable("SETTLEMENT", home.Name)
                    .SetTextVariable("COUNT", detachmentRoster.TotalManCount)
                    .SetTextVariable("ENEMY", target.Name)
                    .SetTextVariable("ENEMY_COUNT", enemyCount),
                Colors.Yellow);
        }

        private MobileParty SpawnDetachment(Settlement home, TroopRoster roster)
        {
            MobileParty garrison = home.Town.GarrisonParty;
            foreach (TroopRosterElement e in roster.GetTroopRoster())
                garrison.MemberRoster.AddToCounts(e.Character, -e.Number);

            TextObject name = new TextObject("{=gd_party_name}{SETTLEMENT} Muhafızları")
                .SetTextVariable("SETTLEMENT", home.Name);

            MobileParty detachment = CustomPartyComponent.CreateCustomPartyWithTroopRoster(
                home.GatePosition, 0.5f, home, name, home.OwnerClan,
                roster, TroopRoster.CreateDummyTroopRoster(), home.OwnerClan.Leader,
                "", "", 0f, false);

            int food = MathF.Max(5, roster.TotalManCount / 5);
            detachment.ItemRoster.AddToCounts(DefaultItems.Grain, food);
            detachment.Ai.SetDoNotMakeNewDecisions(true);
            _detachments.Add(detachment);
            _everSpawned.Add(detachment);
            return detachment;
        }

        // Garnizondan sağlıklı askerler seçer: hızlı düşmana karşı önce atlılar, sonra en güçlüler.
        // Hem sayı hem güç ihtiyacı karşılanınca durur. allowPartial=false iken bütçe yetmezse null;
        // allowPartial=true iken bütçe kadarını (boş olabilir) döndürür.
        private static TroopRoster PickTroops(TroopRoster garrison, int budget, int neededCount, float neededPower,
                                              bool preferCavalry, bool allowPartial)
        {
            if (!allowPartial && neededCount > budget)
                return null;

            var available = garrison.GetTroopRoster()
                .Where(e => e.Character != null && !e.Character.IsHero && e.Number - e.WoundedNumber > 0)
                .OrderByDescending(e => preferCavalry && e.Character.IsMounted ? 1 : 0)
                .ThenByDescending(e => e.Character.GetPower())
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
            return allowPartial ? picked : null;
        }

        // Hedef zaten başka bir müfrezenin hedefi ya da onun hemen yanındaysa (aynı grup) true.
        private bool IsTargeted(MobileParty enemy)
        {
            return _targets.Values.Any(t => t != null && t.IsActive
                && (t == enemy || t.Position.Distance(enemy.Position) <= GroupRadius));
        }

        private void Engage(MobileParty detachment, MobileParty target)
        {
            _returning.Remove(detachment);
            _targets[detachment] = target;
            if (!_chaseStart.ContainsKey(detachment))
                _chaseStart[detachment] = CampaignTime.Now;
            detachment.SetMoveEngageParty(target, MobileParty.NavigationType.Default);
        }

        // ---------------- Kuşatmaya yardım ----------------

        private static List<MobileParty> Besiegers(Settlement besieged, List<MobileParty> hostiles)
        {
            return hostiles.Where(p => IsHostileTo(p, besieged)
                                       && (p.BesiegedSettlement == besieged
                                           || p.Position.Distance(besieged.Position) <= GroupRadius * 2f))
                           .ToList();
        }

        private void UpdateReliefOperations(List<MobileParty> hostiles)
        {
            // Biten kuşatmaların kaydını temizle.
            foreach (Settlement s in _reliefStart.Keys.Where(s => !s.IsUnderSiege).ToList())
                _reliefStart.Remove(s);
            _reliefImpossibleNotified.RemoveWhere(s => !s.IsUnderSiege);

            // Süren operasyonlar: düşman büyüdüyse ya da süre dolduysa geri çekil.
            foreach (Settlement besieged in _reliefTarget.Values.Distinct().ToList())
            {
                List<MobileParty> force = _reliefTarget.Where(kv => kv.Value == besieged).Select(kv => kv.Key).ToList();
                bool expired = !_reliefStart.TryGetValue(besieged, out CampaignTime start)
                               || (CampaignTime.Now - start).ToHours > MaxReliefHours;
                List<MobileParty> enemies = Besiegers(besieged, hostiles);
                int enemyCount = enemies.Sum(p => p.MemberRoster.TotalHealthyCount);
                float enemyPower = enemies.Sum(p => RosterPower(p.MemberRoster));
                int ownCount = force.Sum(p => p.MemberRoster.TotalHealthyCount);
                float ownPower = force.Sum(p => RosterPower(p.MemberRoster));
                bool inBattle = force.Any(p => p.MapEvent != null);

                if (!besieged.IsUnderSiege || expired || (!inBattle && !CanBeat(ownCount, ownPower, enemyCount, enemyPower, 1f)))
                {
                    foreach (MobileParty d in force)
                    {
                        _reliefTarget.Remove(d);
                        _departAt.Remove(d);
                        if (d.MapEvent == null)
                            StartReturn(d);
                    }
                }
            }

            foreach (Settlement besieged in Settlement.All)
            {
                if (!besieged.IsFortification || !besieged.IsUnderSiege || !IsDefendedSettlement(besieged)
                    || _reliefStart.ContainsKey(besieged))
                    continue;
                PlanRelief(besieged, hostiles);
            }
        }

        private void PlanRelief(Settlement besieged, List<MobileParty> hostiles)
        {
            List<MobileParty> enemies = Besiegers(besieged, hostiles);
            int enemyCount = enemies.Sum(p => p.MemberRoster.TotalHealthyCount);
            float enemyPower = enemies.Sum(p => RosterPower(p.MemberRoster));
            if (enemyCount <= 0)
                return;

            // Aynı klanın ya da aynı krallığın (ayar açıksa AI lordları dahil) yakındaki garnizonları.
            var contributors = Settlement.All
                .Where(g => g != besieged && IsDefendedSettlement(g) && !g.IsUnderSiege
                            && g.Town.GarrisonParty != null && g.Town.GarrisonParty.MapEvent == null
                            && (g.OwnerClan == besieged.OwnerClan || g.MapFaction == besieged.MapFaction)
                            && g.Position.Distance(besieged.Position) <= S.ReliefRadius)
                .OrderBy(g => g.Position.Distance(besieged.Position))
                .ToList();

            int neededCount = NeededCount(enemyCount, 1f);
            float neededPower = NeededPower(enemyPower, 1f);
            var plan = new List<(Settlement home, TroopRoster roster)>();
            int count = 0;
            float power = 0f;
            foreach (Settlement g in contributors)
            {
                int budget = Budget(g);
                if (budget < MinDetachmentSize)
                    continue;
                TroopRoster roster = PickTroops(g.Town.GarrisonParty.MemberRoster, budget,
                    neededCount - count, neededPower - power, false, true);
                if (roster.TotalManCount < MinDetachmentSize)
                    continue;
                plan.Add((g, roster));
                count += roster.TotalManCount;
                power += RosterPower(roster);
                if (count >= neededCount && power >= neededPower)
                    break;
            }

            if (!CanBeat(count, power, enemyCount, enemyPower, 1f))
            {
                if (_reliefImpossibleNotified.Add(besieged))
                    Notify(besieged, new TextObject("{=gd_relief_no}{SETTLEMENT} kuşatılıyor ({ENEMY_COUNT} düşman), ama yakındaki garnizonlar yardım için yetersiz.")
                            .SetTextVariable("SETTLEMENT", besieged.Name)
                            .SetTextVariable("ENEMY_COUNT", enemyCount),
                        Colors.Red);
                return;
            }

            // Herkes aynı anda varsın diye yakındakiler daha geç yola çıkar.
            _reliefStart[besieged] = CampaignTime.Now;
            var spawned = new List<(MobileParty party, float eta)>();
            foreach (var (home, roster) in plan)
            {
                MobileParty d = SpawnDetachment(home, roster);
                float speed = d.Speed > 0.5f ? d.Speed : 4f;
                spawned.Add((d, home.Position.Distance(besieged.Position) / speed));
                GDStatsBehavior.Instance?.RecordDispatch(home, roster.TotalManCount, true);
            }
            float maxEta = spawned.Max(x => x.eta);
            foreach (var (party, eta) in spawned)
            {
                _reliefTarget[party] = besieged;
                _departAt[party] = CampaignTime.HoursFromNow(maxEta - eta);
                party.SetMoveModeHold();
            }
            GDStatsBehavior.Instance?.RecordReliefPlanned(besieged, plan.Count, count);

            Notify(besieged, new TextObject("{=gd_relief}{SETTLEMENT} kuşatmasını kırmak için {N} garnizondan {COUNT} asker toplanıyor.")
                    .SetTextVariable("SETTLEMENT", besieged.Name)
                    .SetTextVariable("N", plan.Count)
                    .SetTextVariable("COUNT", count),
                Colors.Yellow);
        }

        private void UpdateReliefDetachment(MobileParty detachment, Settlement besieged)
        {
            TopUpFood(detachment);
            if (_departAt.TryGetValue(detachment, out CampaignTime departAt) && CampaignTime.Now < departAt)
                detachment.SetMoveModeHold();
            else
                detachment.SetMoveDefendSettlement(besieged, false, MobileParty.NavigationType.Default);
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

            if (_reliefTarget.TryGetValue(detachment, out Settlement besieged))
            {
                UpdateReliefDetachment(detachment, besieged);
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
                                 && (CampaignTime.Now - start).ToHours > S.MaxChaseHours;
            float factor = MarginFactor(home);

            _targets.TryGetValue(detachment, out MobileParty target);
            bool targetValid = !homeLost && target != null && target.IsActive && target.CurrentSettlement == null
                               && !target.IsCurrentlyAtSea && IsValidThreat(target, home)
                               && DistanceToZone(home, target) <= S.LeashRadius;
            if (targetValid)
            {
                // Savaştan yaralı çıktıysak ya da düşmana takviye geldiyse artık saldırmayız.
                EnemyGroupStrength(target, home, hostiles, out int enemyCount, out float enemyPower);
                targetValid = CanBeat(detachment.MemberRoster.TotalHealthyCount, RosterPower(detachment.MemberRoster),
                                      enemyCount, enemyPower, factor);
            }

            if (!homeLost && !chasedTooLong && !targetValid)
            {
                _targets.Remove(detachment);
                _chaseStart.Remove(detachment);
                target = FindNewTarget(detachment, home, hostiles, factor);
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

        private MobileParty FindNewTarget(MobileParty detachment, Settlement home, List<MobileParty> hostiles, float factor)
        {
            int ownCount = detachment.MemberRoster.TotalHealthyCount;
            float ownPower = RosterPower(detachment.MemberRoster);
            foreach (MobileParty p in hostiles
                         .Where(p => IsValidThreat(p, home) && !IsTargeted(p))
                         .Where(p => DistanceToZone(home, p) <= S.ThreatRadius)
                         .OrderBy(p => p.Position.Distance(detachment.Position)))
            {
                EnemyGroupStrength(p, home, hostiles, out int enemyCount, out float enemyPower);
                if (CanBeat(ownCount, ownPower, enemyCount, enemyPower, factor))
                    return p;
            }
            return null;
        }

        private void StartReturn(MobileParty detachment)
        {
            _targets.Remove(detachment);
            _chaseStart.Remove(detachment);
            _reliefTarget.Remove(detachment);
            _departAt.Remove(detachment);
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
            int prisoners = detachment.PrisonRoster.TotalManCount;
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

            GDStatsBehavior.Instance?.RecordReturn(dest, returned, prisoners);
            Notify(dest, new TextObject("{=gd_back}{COUNT} muhafız {SETTLEMENT} garnizonuna geri döndü.")
                    .SetTextVariable("COUNT", returned)
                    .SetTextVariable("SETTLEMENT", dest.Name),
                Colors.Green);

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
