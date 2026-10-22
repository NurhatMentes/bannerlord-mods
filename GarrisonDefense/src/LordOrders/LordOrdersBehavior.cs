using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace LordOrders
{
    // Lordlara emir verme: klan üyeleri her zaman uyar, krallığın vasalları (oyuncu hükümdarsa)
    // ilişkiye göre kabul eder ve nüfuz harcatır. Emir yüz yüze diyalogla ya da haberciyle verilir.
    public class LordOrdersBehavior : CampaignBehaviorBase
    {
        private const float VassalInfluenceCost = 10f;
        private const int MessengerGoldCost = 50;
        private const float MessengerSpeed = 12f;      // harita birimi / saat
        private const float RaidDays = 5f, BesiegeDays = 15f, FoodDays = 10f, PatrolDays = 10f, FollowDays = 10f, JoinDays = 5f;
        private const float PatrolEngageRadius = 8f;
        private const int MaxListItems = 20;
        private const float RefuseCooldownHours = 24f;  // reddeden vasala bu süre tekrar sorulamaz

        public static LordOrdersBehavior Instance { get; private set; }

        public LordOrdersBehavior() { Instance = this; }

        private List<Order> _orders = new List<Order>();
        private List<Order> _messages = new List<Order>();   // haberciyle yolda olan emirler
        private readonly Dictionary<Hero, CampaignTime> _refusedUntil = new Dictionary<Hero, CampaignTime>();

        // Diyalog bitince açılacak hedef seçimi (bir sonraki karede).
        private Hero _pendingHero;
        private OrderType _pendingType;
        private bool _pendingViaMessenger;
        private Action _pendingUi;

        // ---------------- Kayıt ----------------

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, OnHourlyTick);
            CampaignEvents.HeroKilledEvent.AddNonSerializedListener(this, (victim, killer, detail, notify) =>
            {
                foreach (Order o in _orders.Where(o => o.Hero == victim).ToList())
                    Release(o);
                _messages.RemoveAll(m => m.Hero == victim);
            });
            CampaignEvents.MobilePartyDestroyed.AddNonSerializedListener(this, (party, destroyer) =>
                _orders.RemoveAll(o => o.Party == party));
            CampaignEvents.OnPartyDisbandStartedEvent.AddNonSerializedListener(this, party =>
            {
                foreach (Order o in _orders.Where(o => o.Party == party).ToList())
                    Finish(o, $"{o.Hero?.Name} partisini dağıtıyor, emir iptal.", false);
            });
        }

        public override void SyncData(IDataStore dataStore)
        {
            SyncList(dataStore, "lo_orders", ref _orders);
            SyncList(dataStore, "lo_messages", ref _messages);
        }

        private static void SyncList(IDataStore dataStore, string key, ref List<Order> list)
        {
            List<Hero> heroes = list.Select(o => o.Hero).ToList();
            List<MobileParty> parties = list.Select(o => o.Party).ToList();
            string data = string.Join("\n", list.Select(o => o.Serialize()));
            dataStore.SyncData(key + "_heroes", ref heroes);
            dataStore.SyncData(key + "_parties", ref parties);
            dataStore.SyncData(key + "_data", ref data);
            if (!dataStore.IsLoading)
                return;
            list = new List<Order>();
            string[] lines = (data ?? "").Split('\n');
            if (heroes == null)
                return;
            for (int i = 0; i < heroes.Count && i < lines.Length; i++)
            {
                Order o = Order.Deserialize(heroes[i], lines[i]);
                if (o == null)
                    continue;
                if (parties != null && parties.Count == heroes.Count)
                    o.Party = parties[i];
                list.Add(o);
            }
        }

        // ---------------- Kimlere emir verilebilir ----------------

        private static bool IsClanMember(Hero h) => h != null && h.Clan == Clan.PlayerClan && h != Hero.MainHero;

        private static bool IsVassal(Hero h)
        {
            Kingdom k = Clan.PlayerClan.Kingdom;
            return h != null && k != null && k.RulingClan == Clan.PlayerClan
                   && h.Clan != null && h.Clan != Clan.PlayerClan && h.Clan.Kingdom == k;
        }

        private static MobileParty LeadParty(Hero h)
        {
            MobileParty p = h?.PartyBelongedTo;
            if (p == null || p.LeaderHero != h || p.IsMainParty || !p.IsActive)
                return null;
            return p;
        }

        // Emir verilebilir mi? Değilse neden (oyuncuya gösterilir).
        private static bool CanCommand(Hero h, out string reason)
        {
            reason = null;
            if (h == null || !h.IsAlive || h.IsPrisoner)
                reason = "Bu kişi emir alamaz.";
            else if (!IsClanMember(h) && !IsVassal(h))
                reason = "Sadece klanının üyelerine ve (hükümdarsan) vasallarına emir verebilirsin.";
            else if (LeadParty(h) == null)
                reason = h.Name + " kendi partisine komuta etmiyor (şu an senin partinde olabilir). "
                         + "Klan ekranında (L) Partiler sekmesinden ona yeni bir parti kur.";
            else if (LeadParty(h).Army != null && LeadParty(h).Army.LeaderParty != LeadParty(h))
                reason = h.Name + " başka birinin ordusunda.";
            return reason == null;
        }

        // Lordun ağzından, neden emir alamadığı.
        private static string CannotReasonFirstPerson(Hero h)
        {
            MobileParty p = LeadParty(h);
            if (h == null || h.IsPrisoner)
                return "Şu an emir alacak durumda değilim.";
            if (p == null)
                return "Emrine hazırım, ama komuta ettiğim bir parti yok; şu an seninle birlikteyim. "
                       + "Klan ekranından bana bir parti kurarsan istediğin her yere giderim.";
            if (p.Army != null && p.Army.LeaderParty != p)
                return "Şu an başka birinin ordusundayım, oradan ayrılamam.";
            return "Şu an buna uyamam.";
        }

        // Liste neden boş: oyuncuya ne yapması gerektiğini söyler.
        private static string NoLordReason()
        {
            var parts = new List<string>();
            int withYou = Clan.PlayerClan.Heroes.Count(h => h != Hero.MainHero && h.IsAlive && !h.IsChild
                                                          && h.PartyBelongedTo == MobileParty.MainParty);
            if (withYou > 0)
                parts.Add($"{withYou} klan üyen senin partinde; klan ekranında (L) Partiler sekmesinden onlara kendi partilerini kurabilirsin.");
            else
                parts.Add("Klanında kendi partisine komuta eden kimse yok; klan ekranından (L) bir yoldaşına parti kurabilirsin.");
            Kingdom k = Clan.PlayerClan.Kingdom;
            if (k == null)
                parts.Add("Bir krallığa bağlı değilsin, bu yüzden başka lordlara emir veremezsin.");
            else if (k.RulingClan != Clan.PlayerClan)
                parts.Add($"{k.Name} krallığının hükümdarı sen değilsin ({k.Leader?.Name}); diğer lordlara sadece hükümdarken emir verebilirsin.");
            return string.Join(" ", parts);
        }

        private Order OrderOf(Hero h) => _orders.FirstOrDefault(o => o.Hero == h);

        private static IEnumerable<Hero> CommandableHeroes()
        {
            // Klanın "savaş partileri" listesine güvenmek yerine haritadaki tüm partilere bakılır:
            // bu sürümde bazı klan partileri o listede görünmeyebiliyor.
            return MobileParty.All
                .Where(p => p.IsActive && !p.IsMainParty && p.LeaderHero != null)
                .Select(p => p.LeaderHero)
                .Where(h => CanCommand(h, out _))
                .Distinct();
        }

        // ---------------- Diyalog ----------------

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            // Seçenek klan üyelerine ve vasallara her zaman görünür; emir alamıyorsa lord nedenini söyler.
            starter.AddPlayerLine("lo_start", "hero_main_options", "lo_ask",
                "{=lo_start}Sana bir emrim var.",
                () => IsClanMember(Hero.OneToOneConversationHero) || IsVassal(Hero.OneToOneConversationHero),
                null, 105, null, null);
            starter.AddDialogLine("lo_ask_resp", "lo_ask", "lo_menu",
                "{=lo_listen}Dinliyorum.",
                () => CanCommand(Hero.OneToOneConversationHero, out _), null, 100, null);
            starter.AddDialogLine("lo_ask_cannot", "lo_ask", "lord_pretalk",
                "{=lo_cannot}{LO_CANNOT_REASON}",
                () =>
                {
                    MBTextManager.SetTextVariable("LO_CANNOT_REASON", CannotReasonFirstPerson(Hero.OneToOneConversationHero));
                    return !CanCommand(Hero.OneToOneConversationHero, out _);
                }, null, 100, null);

            AddOrderLine(starter, "lo_o_raid", "{=lo_o_raid}Düşmanın bir köyünü yağmalamanı istiyorum.", OrderType.Raid,
                () => RaidTargets(Hero.OneToOneConversationHero).Any());
            AddOrderLine(starter, "lo_o_siege", "{=lo_o_siege}Bir düşman kalesini ya da şehrini kuşatmanı istiyorum.", OrderType.Besiege,
                () => SiegeTargets(Hero.OneToOneConversationHero).Any());
            AddOrderLine(starter, "lo_o_food", "{=lo_o_food}Erzak getirmeni istiyorum.", OrderType.BringFood, () => true);
            AddOrderLine(starter, "lo_o_patrol", "{=lo_o_patrol}Bir bölgeyi korumanı istiyorum.", OrderType.Patrol, () => true);
            AddOrderLine(starter, "lo_o_join", "{=lo_o_join}Orduma katıl.", OrderType.JoinArmy,
                () => Clan.PlayerClan.Kingdom != null);
            AddOrderLine(starter, "lo_o_follow", "{=lo_o_follow}Beni takip et.", OrderType.Follow, () => true);

            starter.AddPlayerLine("lo_o_cancel", "lo_menu", "lo_cancelled",
                "{=lo_o_cancel}Şu anki emrini unut.",
                () => OrderOf(Hero.OneToOneConversationHero) != null,
                () => CancelOrder(OrderOf(Hero.OneToOneConversationHero), null), 100, null, null);
            starter.AddPlayerLine("lo_o_never", "lo_menu", "lord_pretalk",
                "{=lo_o_never}Boş ver.", null, null, 100, null, null);

            starter.AddDialogLine("lo_ack", "lo_ack", "close_window",
                "{=lo_ack}Hedefi göster, ona göre davranırım.", null,
                () =>
                {
                    // Haritada karşılaşmayla konuşulduysa karşılaşma menüsü kapansın.
                    if (PlayerEncounter.Current != null)
                        PlayerEncounter.LeaveEncounter = true;
                }, 100, null);
            starter.AddDialogLine("lo_cancelled", "lo_cancelled", "lord_pretalk",
                "{=lo_cancelled}Peki. Kendi bildiğim gibi davranacağım.", null, null, 100, null);

            foreach (string menu in new[] { "town", "castle" })
            {
                starter.AddGameMenuOption(menu, "lo_messenger_" + menu, "{=lo_messenger}Haberci gönder (lordlara emir)",
                    args =>
                    {
                        args.optionLeaveType = GameMenuOption.LeaveType.Manage;
                        if (Hero.MainHero.Gold < MessengerGoldCost)
                        {
                            args.IsEnabled = false;
                            args.Tooltip = new TextObject("{=lo_no_gold}Haberci için " + MessengerGoldCost + " dinar gerekir.");
                        }
                        return true;
                    },
                    args => OpenMessengerLordPicker(), false, -1);
                starter.AddGameMenuOption(menu, "lo_cancel_all_" + menu, "{=lo_cancel_all}Tüm lord emirlerini iptal et",
                    args =>
                    {
                        args.optionLeaveType = GameMenuOption.LeaveType.Manage;
                        args.Tooltip = new TextObject("{=lo_cancel_all_tip}Bütün emirler ve yoldaki haberciler hemen iptal edilir. Modu kaldırmadan önce kullan.");
                        return _orders.Count > 0 || _messages.Count > 0;
                    },
                    args => CancelAll(), false, -1);
            }
        }

        private void CancelAll()
        {
            int n = _orders.Count;
            foreach (Order o in _orders.ToList())
                Release(o);
            _messages.Clear();
            Say($"{n} emir iptal edildi; lordlar kendi bildikleri gibi davranacak.", Colors.Yellow);
        }

        private void AddOrderLine(CampaignGameStarter starter, string id, string text, OrderType type, Func<bool> available)
        {
            starter.AddPlayerLine(id, "lo_menu", "lo_ack", text,
                () => available(),
                () =>
                {
                    _pendingHero = Hero.OneToOneConversationHero;
                    _pendingType = type;
                    _pendingViaMessenger = false;
                    _pendingUi = () => OpenTargetPicker(_pendingHero, _pendingType, _pendingViaMessenger);
                }, 100, null, null);
        }

        // SubModule her karede çağırır: diyalog kapanınca bekleyen pencereyi açar.
        public void Tick()
        {
            try
            {
                if (_pendingUi == null || Campaign.Current == null)
                    return;
                if (!(Game.Current?.GameStateManager?.ActiveState is MapState mapState) || mapState.MapConversationActive)
                    return;
                if (Campaign.Current.ConversationManager != null && Campaign.Current.ConversationManager.IsConversationInProgress)
                    return;
                if (InformationManager.IsAnyInquiryActive())
                    return;
                Action ui = _pendingUi;
                _pendingUi = null;
                ui();
            }
            catch (Exception)
            {
                _pendingUi = null;
                Say("Lord Orders: işlem tamamlanamadı.", Colors.Red);
            }
        }

        // ---------------- Hedef listeleri ----------------

        private static float Dist(Hero h, Settlement s)
        {
            MobileParty p = LeadParty(h);
            return p == null ? 0f : p.Position.Distance(s.Position);
        }

        private static IEnumerable<Settlement> RaidTargets(Hero h)
        {
            if (h?.MapFaction == null)
                return Enumerable.Empty<Settlement>();
            return Settlement.All
                .Where(s => s.IsVillage && s.Village != null && s.MapFaction != null && s.MapFaction.IsAtWarWith(h.MapFaction)
                            && s.Village.VillageState == Village.VillageStates.Normal)
                .OrderBy(s => Dist(h, s)).Take(MaxListItems);
        }

        private static IEnumerable<Settlement> SiegeTargets(Hero h)
        {
            if (h?.MapFaction == null)
                return Enumerable.Empty<Settlement>();
            return Settlement.All
                .Where(s => s.IsFortification && s.MapFaction != null && s.MapFaction.IsAtWarWith(h.MapFaction) && !s.IsUnderSiege)
                .OrderBy(s => Dist(h, s)).Take(MaxListItems);
        }

        private static IEnumerable<Settlement> PatrolTargets(Hero h)
        {
            IFaction faction = Clan.PlayerClan.MapFaction;
            return Settlement.All
                .Where(s => (s.IsVillage || s.IsFortification) && s.MapFaction == faction)
                .OrderBy(s => Dist(h, s)).Take(MaxListItems + 5);
        }

        private static IEnumerable<Settlement> FoodDestinations()
        {
            return Settlement.All.Where(s => s.IsFortification && s.Town != null && s.OwnerClan == Clan.PlayerClan);
        }

        private static InquiryElement Element(object id, string title, string hint = "")
        {
            return new InquiryElement(id, title, null, true, hint);
        }

        private static void Pick(string title, string text, List<InquiryElement> elements, Action<object> onPick)
        {
            if (elements.Count == 0)
            {
                InformationManager.DisplayMessage(new InformationMessage("Uygun hedef yok.", Colors.Red));
                return;
            }
            MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(
                title, text, elements, true, 1, 1, "Seç", "Vazgeç",
                selected =>
                {
                    object id = selected.FirstOrDefault()?.Identifier;
                    if (id != null)
                        Instance._pendingUi = () => onPick(id);   // bir sonraki pencere bir sonraki karede açılır
                },
                null), true, false);
        }

        private void OpenTargetPicker(Hero hero, OrderType type, bool viaMessenger)
        {
            if (!CanCommand(hero, out string reason))
            {
                InformationManager.DisplayMessage(new InformationMessage(reason, Colors.Red));
                return;
            }

            switch (type)
            {
                case OrderType.Raid:
                    Pick("Yağmalanacak köy", hero.Name + " hangi köyü yağmalasın?",
                        RaidTargets(hero).Select(s => Element(s, $"{s.Name} ({s.MapFaction.Name}) — uzaklık {Dist(hero, s):0}")).ToList(),
                        id => Issue(hero, new Order { Type = type, Target = (Settlement)id }, viaMessenger));
                    break;
                case OrderType.Besiege:
                    Pick("Kuşatılacak yer", hero.Name + " neresi kuşatsın? Tek başına güçlü bir kaleyi alamayabilir.",
                        SiegeTargets(hero).Select(s => Element(s, $"{s.Name} ({s.MapFaction.Name}) — uzaklık {Dist(hero, s):0}")).ToList(),
                        id => Issue(hero, new Order { Type = type, Target = (Settlement)id }, viaMessenger));
                    break;
                case OrderType.Patrol:
                    Pick("Korunacak bölge", hero.Name + " hangi yerleşimin çevresini korusun? (" + PatrolDays + " gün)",
                        PatrolTargets(hero).Select(s => Element(s, $"{s.Name} — uzaklık {Dist(hero, s):0}")).ToList(),
                        id => Issue(hero, new Order { Type = type, Target = (Settlement)id }, viaMessenger));
                    break;
                case OrderType.BringFood:
                    Pick("Erzak miktarı", "Ne kadar erzak alınsın? Parasını sen ödersin (pazar fiyatı).",
                        new List<InquiryElement> { Element(30, "30 birim"), Element(60, "60 birim"), Element(100, "100 birim") },
                        amount =>
                        {
                            var dests = new List<InquiryElement> { Element("player", "Bana getir") };
                            dests.AddRange(FoodDestinations().Select(s => Element(s, $"{s.Name} kilerine götür")));
                            Pick("Erzak nereye", "Erzak nereye teslim edilsin?", dests,
                                dest => Issue(hero, new Order
                                {
                                    Type = type,
                                    Amount = (int)amount,
                                    ToPlayer = dest as string == "player",
                                    Target = dest as Settlement
                                }, viaMessenger));
                        });
                    break;
                default:
                    Issue(hero, new Order { Type = type }, viaMessenger);
                    break;
            }
        }

        // ---------------- Haberci ----------------

        private void OpenMessengerLordPicker()
        {
            var lords = CommandableHeroes()
                .OrderBy(h => LeadParty(h).Position.Distance(MobileParty.MainParty.Position))
                .Select(h =>
                {
                    Order cur = OrderOf(h);
                    string tag = IsClanMember(h) ? "klan" : "vasal";
                    return Element(h, $"{h.Name} ({tag}, {LeadParty(h).MemberRoster.TotalManCount} asker) — emir: {cur?.Describe() ?? "yok"}");
                })
                .ToList();
            if (lords.Count == 0)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "Emir verebileceğin, kendi partisine komuta eden bir lord yok. " + NoLordReason(), Colors.Red));
                return;
            }
            Pick("Haberci gönder", "Haberci kime gitsin? (" + MessengerGoldCost + " dinar, mesafeye göre gecikmeli ulaşır)", lords,
                id =>
                {
                    Hero hero = (Hero)id;
                    var types = new List<InquiryElement>();
                    foreach (OrderType t in new[] { OrderType.Raid, OrderType.Besiege, OrderType.BringFood, OrderType.Patrol, OrderType.JoinArmy, OrderType.Follow })
                    {
                        if (t == OrderType.JoinArmy && Clan.PlayerClan.Kingdom == null) continue;
                        types.Add(Element((int)t, Order.Describe(t)));
                    }
                    if (OrderOf(hero) != null)
                        types.Add(Element(0, "Mevcut emri iptal et"));
                    Pick("Emir", hero.Name + " ne yapsın?", types, t =>
                    {
                        if ((int)t == 0)
                            Issue(hero, new Order { Type = OrderType.None }, true);
                        else
                            OpenTargetPicker(hero, (OrderType)(int)t, true);
                    });
                });
        }

        // ---------------- Emir verme ----------------

        private void Issue(Hero hero, Order order, bool viaMessenger)
        {
            order.Hero = hero;
            if (viaMessenger)
            {
                if (Hero.MainHero.Gold < MessengerGoldCost)
                {
                    InformationManager.DisplayMessage(new InformationMessage("Haberci için yeterli altının yok.", Colors.Red));
                    return;
                }
                GiveGoldAction.ApplyBetweenCharacters(Hero.MainHero, null, MessengerGoldCost, true);
                MobileParty p = LeadParty(hero);
                float hours = 1f + (p == null ? 0f : p.Position.Distance(MobileParty.MainParty.Position) / MessengerSpeed);
                order.ArriveHours = CampaignTime.HoursFromNow(hours).ToHours;
                _messages.Add(order);
                InformationManager.DisplayMessage(new InformationMessage(
                    $"Haberci yola çıktı: {hero.Name} emri yaklaşık {MathF.Ceiling(hours)} saat içinde alacak.", Colors.Yellow));
                return;
            }
            Deliver(order);
        }

        // Emir lorda ulaştı: kabul/ret ve başlatma.
        private void Deliver(Order order)
        {
            Hero hero = order.Hero;
            if (!CanCommand(hero, out string reason))
            {
                Say(reason, Colors.Red);
                return;
            }

            Order current = OrderOf(hero);
            if (order.Type == OrderType.None)
            {
                if (current != null)
                    CancelOrder(current, hero.Name + " emrini bıraktı, kendi bildiği gibi davranacak.");
                return;
            }

            if (IsVassal(hero))
            {
                if (_refusedUntil.TryGetValue(hero, out CampaignTime until) && CampaignTime.Now < until)
                {
                    Say($"{hero.Name} kısa süre önce emrini reddetti; tekrar sormak için {MathF.Ceiling(until.RemainingHoursFromNow)} saat bekle.", Colors.Red);
                    return;
                }
                if (Clan.PlayerClan.Influence < VassalInfluenceCost)
                {
                    Say($"{hero.Name} emrine uymadı: bir vasala emir vermek {VassalInfluenceCost:0} nüfuz gerektirir.", Colors.Red);
                    return;
                }
                float chance = MathF.Clamp(0.55f + hero.GetRelationWithPlayer() * 0.006f, 0.15f, 0.95f);
                if (MBRandom.RandomFloat > chance)
                {
                    _refusedUntil[hero] = CampaignTime.HoursFromNow(RefuseCooldownHours);
                    Say($"{hero.Name} emrini reddetti. (Kabul şansı ~%{MathF.Round(chance * 100)}; ilişkiniz arttıkça yükselir.)", Colors.Red);
                    return;
                }
                ChangeClanInfluenceAction.Apply(Clan.PlayerClan, -VassalInfluenceCost);
            }

            if (current != null)
                Release(current);

            order.Party = LeadParty(hero);
            order.StartHours = CampaignTime.Now.ToHours;
            order.DeadlineHours = CampaignTime.HoursFromNow(24f * DurationDays(order.Type)).ToHours;
            order.Stage = 0;
            if (order.Type == OrderType.BringFood)
                order.Market = FindMarket(hero, order.Amount);
            _orders.Add(order);
            Say($"{hero.Name}: \"Emredersin!\" — {order.Describe()}", Colors.Green);
            Execute(order);
        }

        private static float DurationDays(OrderType type)
        {
            switch (type)
            {
                case OrderType.Raid: return RaidDays;
                case OrderType.Besiege: return BesiegeDays;
                case OrderType.BringFood: return FoodDays;
                case OrderType.Patrol: return PatrolDays;
                case OrderType.JoinArmy: return JoinDays;
                default: return FollowDays;
            }
        }

        private static void Say(string text, Color color)
        {
            InformationManager.DisplayMessage(new InformationMessage(text, color));
        }

        private void CancelOrder(Order order, string message)
        {
            if (order == null)
                return;
            Release(order);
            if (message != null)
                Say(message, Colors.Yellow);
        }

        // Lordun kontrolünü yapay zekâya geri verir.
        private void Release(Order order)
        {
            _orders.Remove(order);
            // Lider ölmüş ya da değişmiş olsa bile emri alan partinin kilidi açılır.
            MobileParty p = order.Party ?? order.Hero?.PartyBelongedTo;
            if (p != null && p.IsActive)
                p.Ai.SetDoNotMakeNewDecisions(false);
        }

        private void Finish(Order order, string message, bool success)
        {
            Release(order);
            Say(message, success ? Colors.Green : Colors.Yellow);
        }

        // ---------------- Saatlik yürütme ----------------

        private void OnHourlyTick()
        {
            double now = CampaignTime.Now.ToHours;
            foreach (Order m in _messages.Where(m => m.ArriveHours <= now).ToList())
            {
                _messages.Remove(m);
                Deliver(m);
            }

            foreach (Order order in _orders.ToList())
            {
                try
                {
                    Execute(order);
                }
                catch (Exception)
                {
                    Finish(order, $"{order.Hero?.Name} emrini yerine getiremedi.", false);
                }
            }
        }

        private void Execute(Order order)
        {
            Hero hero = order.Hero;
            MobileParty party = LeadParty(hero);
            if (order.Party == null)
                order.Party = party;
            if (party == null || party != order.Party || party.IsDisbanding || hero.IsPrisoner || !hero.IsAlive
                || (party.Army != null && party.Army.LeaderParty != party && order.Type != OrderType.JoinArmy))
            {
                Finish(order, $"{hero?.Name} emrini yerine getiremiyor (partisi yok, esir ya da başka bir orduda).", false);
                return;
            }
            if (!IsClanMember(hero) && !IsVassal(hero))
            {
                Finish(order, $"{hero.Name} artık emrinde değil.", false);
                return;
            }
            if (CampaignTime.Now.ToHours > order.DeadlineHours)
            {
                Finish(order, $"{hero.Name} emrinin süresi doldu ({order.Describe()}); kendi işine dönüyor.", false);
                return;
            }
            if (party.MapEvent != null)
                return;

            // Bana gelen emirlerde biri denizde diğeri karadaysa bekle.
            bool escortsPlayer = order.Type == OrderType.Follow || order.Type == OrderType.JoinArmy
                                 || (order.Type == OrderType.BringFood && order.ToPlayer && order.Stage == 1);
            if (escortsPlayer && party.IsCurrentlyAtSea != MobileParty.MainParty.IsCurrentlyAtSea)
            {
                party.Ai.SetDoNotMakeNewDecisions(true);
                party.SetMoveModeHold();
                return;
            }

            // Kuşatma başladıktan sonra kuşatmayı (erzak bitince çekilme vb.) oyunun yapay zekâsı yönetir.
            bool siegeRunning = order.Type == OrderType.Besiege && party.BesiegedSettlement == order.Target;
            party.Ai.SetDoNotMakeNewDecisions(!siegeRunning);
            switch (order.Type)
            {
                case OrderType.Raid: ExecuteRaid(order, party); break;
                case OrderType.Besiege: ExecuteBesiege(order, party); break;
                case OrderType.BringFood: ExecuteFood(order, party); break;
                case OrderType.Patrol: ExecutePatrol(order, party); break;
                case OrderType.JoinArmy: ExecuteJoin(order, party); break;
                case OrderType.Follow: party.SetMoveEscortParty(MobileParty.MainParty, Nav(party), false); break;
            }
        }

        private static MobileParty.NavigationType Nav(MobileParty party)
        {
            MobileParty.NavigationType nav = party.NavigationCapability;
            return nav == MobileParty.NavigationType.None ? MobileParty.NavigationType.Default : nav;
        }

        private void ExecuteRaid(Order order, MobileParty party)
        {
            Settlement v = order.Target;
            if (v?.Village == null)
            {
                Finish(order, $"{order.Hero.Name}: yağma hedefi geçersiz.", false);
                return;
            }
            if (v.Village.VillageState == Village.VillageStates.Looted)
            {
                Finish(order, $"{order.Hero.Name}, {v.Name} köyünü yağmaladı.", true);
                return;
            }
            if (!v.MapFaction.IsAtWarWith(party.MapFaction))
            {
                Finish(order, $"{order.Hero.Name}: {v.Name} artık düşman değil, yağma iptal.", false);
                return;
            }
            party.SetMoveRaidSettlement(v, Nav(party), false);
        }

        private void ExecuteBesiege(Order order, MobileParty party)
        {
            Settlement s = order.Target;
            if (s == null)
            {
                Finish(order, $"{order.Hero.Name}: kuşatma hedefi geçersiz.", false);
                return;
            }
            if (s.MapFaction == party.MapFaction)
            {
                Finish(order, $"{s.Name} ele geçirildi! ({order.Hero.Name})", true);
                return;
            }
            if (!s.MapFaction.IsAtWarWith(party.MapFaction))
            {
                Finish(order, $"{order.Hero.Name}: {s.Name} artık düşman değil, kuşatma iptal.", false);
                return;
            }
            if (party.BesiegedSettlement == s)
            {
                order.Stage = 1;
                return;   // kuşatma sürüyor; saldırı ve çekilme kararı oyunun kuşatma yapay zekâsında
            }
            if (order.Stage == 1)
            {
                Finish(order, $"{order.Hero.Name} {s.Name} kuşatmasını sürdüremedi.", false);
                return;
            }
            party.SetMoveBesiegeSettlement(s, Nav(party));
        }

        // En yakın, savaşta olunmayan ve pazarında yiyecek olan şehir.
        private static Settlement FindMarket(Hero hero, int amount)
        {
            MobileParty p = LeadParty(hero);
            return Settlement.All
                .Where(s => s.IsTown && s.Town != null && s.MapFaction != null && !s.MapFaction.IsAtWarWith(hero.MapFaction)
                            && !s.IsUnderSiege && MarketFood(s) >= MathF.Min(amount, 10))
                .OrderBy(s => p == null ? 0f : p.Position.Distance(s.Position))
                .FirstOrDefault();
        }

        private static int MarketFood(Settlement s)
        {
            int total = 0;
            ItemRoster roster = s.ItemRoster;
            for (int i = 0; i < roster.Count; i++)
            {
                ItemRosterElement e = roster.GetElementCopyAtIndex(i);
                if (e.EquipmentElement.Item != null && e.EquipmentElement.Item.IsFood)
                    total += e.Amount;
            }
            return total;
        }

        private void ExecuteFood(Order order, MobileParty party)
        {
            if (order.Stage == 0)
            {
                Settlement market = order.Market;
                if (market == null || market.MapFaction.IsAtWarWith(party.MapFaction) || market.IsUnderSiege)
                    market = order.Market = FindMarket(order.Hero, order.Amount);
                if (market == null)
                {
                    Finish(order, $"{order.Hero.Name} erzak alabileceği bir pazar bulamadı.", false);
                    return;
                }
                if (party.CurrentSettlement == market)
                {
                    int bought = BuyFood(market, party, order);
                    if (bought <= 0)
                    {
                        Finish(order, $"{order.Hero.Name} {market.Name} pazarından erzak alamadı (altın ya da stok yetersiz).", false);
                        return;
                    }
                    order.Amount = bought;
                    order.Stage = 1;
                    Say($"{order.Hero.Name}, {market.Name} pazarından {bought} birim erzak aldı, yola çıkıyor.", Colors.Yellow);
                }
                else
                {
                    party.SetMoveGoToSettlement(market, Nav(party), false);
                    return;
                }
            }

            if (order.ToPlayer)
            {
                if (party.Position.Distance(MobileParty.MainParty.Position) <= 2f)
                {
                    int moved = MoveBoughtFood(order, party.ItemRoster, MobileParty.MainParty.ItemRoster, order.Amount);
                    Finish(order, $"{order.Hero.Name} sana {moved} birim erzak teslim etti.", true);
                }
                else
                {
                    party.SetMoveEscortParty(MobileParty.MainParty, Nav(party), false);
                }
                return;
            }

            Settlement dest = order.Target;
            if (dest?.Town == null || dest.OwnerClan != Clan.PlayerClan)
            {
                Finish(order, $"{order.Hero.Name}: teslim edilecek kale artık senin değil, erzak iptal.", false);
                return;
            }
            if (party.CurrentSettlement == dest)
            {
                // Kiler kadar teslim edilir; sığmayan erzak lordda kalır (boşa gitmez).
                int room = MathF.Max(0, MathF.Floor(dest.Town.FoodStocksUpperLimit() - dest.Town.FoodStocks));
                int moved = MoveBoughtFood(order, party.ItemRoster, null, room);
                dest.Town.FoodStocks += moved;
                string extra = moved < order.Amount ? $" Kiler dolu olduğu için kalanı lordda kaldı." : "";
                Finish(order, $"{order.Hero.Name}, {dest.Name} kilerine {moved} birim erzak teslim etti.{extra}", true);
            }
            else
            {
                party.SetMoveGoToSettlement(dest, Nav(party), false);
            }
        }

        // Pazardan en ucuz yiyecekleri oyuncunun parasıyla alır, alınanları emre kaydeder.
        private static int BuyFood(Settlement market, MobileParty party, Order order)
        {
            ItemRoster roster = market.ItemRoster;
            var food = new List<(ItemObject item, int count, int price)>();
            for (int i = 0; i < roster.Count; i++)
            {
                ItemRosterElement e = roster.GetElementCopyAtIndex(i);
                ItemObject item = e.EquipmentElement.Item;
                if (item != null && item.IsFood && e.Amount > 0 && e.EquipmentElement.ItemModifier == null)
                    food.Add((item, e.Amount, market.Town.GetItemPrice(item, party, false)));
            }

            int bought = 0;
            int spent = 0;
            var record = new List<string>();
            foreach (var (item, count, price) in food.OrderBy(f => f.price))
            {
                int take = 0;
                while (take < count && bought < order.Amount && spent + price <= Hero.MainHero.Gold)
                {
                    take++;
                    bought++;
                    spent += price;
                }
                if (take > 0)
                {
                    roster.AddToCounts(item, -take);
                    party.ItemRoster.AddToCounts(item, take);
                    record.Add(item.StringId + ":" + take);
                }
                if (bought >= order.Amount)
                    break;
            }
            if (spent > 0)
                GiveGoldAction.ApplyForCharacterToSettlement(Hero.MainHero, market, spent, false);
            order.Items = string.Join(",", record);
            return bought;
        }

        // Sadece satın alınan yiyecekleri taşır (lordun kendi erzakına dokunmaz). to=null ise kilere gider.
        // Yolda yenmiş olanlar teslim edilemez.
        private static int MoveBoughtFood(Order order, ItemRoster from, ItemRoster to, int max)
        {
            int moved = 0;
            foreach (string pair in (order.Items ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = pair.Split(':');
                if (kv.Length != 2 || !int.TryParse(kv[1], out int n))
                    continue;
                ItemObject item = MBObjectManager.Instance.GetObject<ItemObject>(kv[0]);
                if (item == null)
                    continue;
                int take = MathF.Min(MathF.Min(n, from.GetItemNumber(item)), max - moved);
                if (take <= 0)
                    continue;
                from.AddToCounts(item, -take);
                to?.AddToCounts(item, take);
                moved += take;
                if (moved >= max)
                    break;
            }
            return moved;
        }

        private void ExecutePatrol(Order order, MobileParty party)
        {
            Settlement s = order.Target;
            if (s == null)
            {
                Finish(order, $"{order.Hero.Name}: korunacak bölge geçersiz.", false);
                return;
            }
            // Bölgedeki, bizden zayıf en yakın düşmana saldır; yoksa devriye gez.
            float ownStrength = party.Party.EstimatedStrength;
            MobileParty enemy = MobileParty.All
                .Where(p => p.IsActive && p.CurrentSettlement == null && !p.IsCurrentlyAtSea && p.MapFaction != null
                            && (p.IsBandit || p.IsLordParty || p.IsPatrolParty) && p.Army == null
                            && p.MapFaction.IsAtWarWith(party.MapFaction)
                            && p.Position.Distance(s.Position) <= PatrolEngageRadius
                            && p.Party.EstimatedStrength * 1.2f < ownStrength)
                .OrderBy(p => p.Position.Distance(party.Position))
                .FirstOrDefault();
            if (enemy != null)
                party.SetMoveEngageParty(enemy, Nav(party));
            else
                party.SetMovePatrolAroundSettlement(s, Nav(party), false);
        }

        private void ExecuteJoin(Order order, MobileParty party)
        {
            MobileParty main = MobileParty.MainParty;
            if (party.Army != null && party.Army == main.Army)
            {
                Finish(order, $"{order.Hero.Name} ordana katıldı.", true);
                return;
            }
            if (party.Position.Distance(main.Position) > 3f || main.CurrentSettlement != null || party.CurrentSettlement != null
                || party.IsCurrentlyAtSea != main.IsCurrentlyAtSea)
            {
                party.SetMoveEscortParty(main, Nav(party), false);
                return;
            }

            Kingdom kingdom = Clan.PlayerClan.Kingdom;
            if (main.Army == null)
            {
                if (kingdom == null)
                {
                    order.Type = OrderType.Follow;
                    Say($"{order.Hero.Name}: bir krallığın olmadan ordu kurulamaz, seni takip edeceğim.", Colors.Yellow);
                    return;
                }
                Settlement gather = Settlement.All.Where(st => st.IsFortification && st.MapFaction == Clan.PlayerClan.MapFaction)
                    .OrderBy(st => st.Position.Distance(main.Position)).FirstOrDefault() ?? Hero.MainHero.HomeSettlement;
                kingdom.CreateArmy(Hero.MainHero, gather, Army.ArmyTypes.Patrolling, new MBList<MobileParty>());
            }
            if (main.Army == null || main.Army.LeaderParty != main)
            {
                order.Type = OrderType.Follow;
                Say($"{order.Hero.Name}: ordu kurulamadı, seni takip edeceğim.", Colors.Yellow);
                return;
            }

            if (party.Army != null && party.Army.LeaderParty == party)
            {
                Finish(order, $"{order.Hero.Name} kendi ordusuna komuta ediyor, katılamaz.", false);
                return;
            }
            party.Army = main.Army;
            main.Army.AddPartyToMergedParties(party);
            Finish(order, $"{order.Hero.Name} ordana katıldı.", true);
        }
    }
}
