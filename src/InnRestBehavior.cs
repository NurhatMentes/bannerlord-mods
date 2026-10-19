using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace InnRest
{
    // Şehir menüsüne "Hanında dinlen" seçeneği ekler: altın karşılığında
    // yaralı askerleri iyileştirir, kahramanı iyileştirir ve moral verir.
    public class InnRestBehavior : CampaignBehaviorBase
    {
        private const int GoldPerTroop = 5;
        private const int BaseCost = 100;
        private const float MoraleBonus = 15f;
        private const float CooldownDays = 7f;

        private CampaignTime _lastRest = CampaignTime.Zero;

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("innrest_lastRest", ref _lastRest);
        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            starter.AddGameMenuOption(
                "town", "innrest_rest", "{=innrest_opt}Rest at the inn ({INNREST_COST}{GOLD_ICON})",
                CanRest, OnRest, false, 4);
        }

        private int Cost()
        {
            return BaseCost + MobileParty.MainParty.MemberRoster.TotalManCount * GoldPerTroop;
        }

        private bool CanRest(MenuCallbackArgs args)
        {
            args.optionLeaveType = GameMenuOption.LeaveType.Wait;
            int cost = Cost();
            MBTextManager.SetTextVariable("INNREST_COST", cost);

            float daysLeft = CooldownDays - (float)(CampaignTime.Now - _lastRest).ToDays;
            if (_lastRest != CampaignTime.Zero && daysLeft > 0f)
            {
                args.IsEnabled = false;
                args.Tooltip = new TextObject("{=innrest_cd}You can rest again in {DAYS} days.")
                    .SetTextVariable("DAYS", MathF.Ceiling(daysLeft));
            }
            else if (Hero.MainHero.Gold < cost)
            {
                args.IsEnabled = false;
                args.Tooltip = new TextObject("{=innrest_gold}You don't have enough gold.");
            }
            return Settlement.CurrentSettlement != null && Settlement.CurrentSettlement.IsTown;
        }

        private void OnRest(MenuCallbackArgs args)
        {
            MobileParty party = MobileParty.MainParty;
            int cost = Cost();
            GiveGoldAction.ApplyBetweenCharacters(Hero.MainHero, null, cost);

            int healed = 0;
            var roster = party.MemberRoster;
            for (int i = 0; i < roster.Count; i++)
            {
                var element = roster.GetElementCopyAtIndex(i);
                if (element.Character.IsHero)
                {
                    element.Character.HeroObject.HitPoints = element.Character.HeroObject.MaxHitPoints;
                }
                else if (element.WoundedNumber > 0)
                {
                    healed += element.WoundedNumber;
                    roster.AddToCountsAtIndex(i, 0, -element.WoundedNumber);
                }
            }

            party.RecentEventsMorale += MoraleBonus;
            _lastRest = CampaignTime.Now;

            InformationManager.DisplayMessage(new InformationMessage(
                new TextObject("{=innrest_done}Your men rest at the inn. {HEALED} wounded troops recovered, morale +{MORALE}.")
                    .SetTextVariable("HEALED", healed)
                    .SetTextVariable("MORALE", (int)MoraleBonus).ToString(),
                Colors.Green));

            GameMenu.SwitchToMenu("town");
        }
    }
}
