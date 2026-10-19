using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace GarrisonDefense
{
    public class SubModule : MBSubModuleBase
    {
        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);
            if (game.GameType is Campaign && gameStarterObject is CampaignGameStarter starter)
            {
                GDSettings.Reset();
                starter.AddBehavior(new GDStatsBehavior());
                starter.AddBehavior(new GarrisonDefenseBehavior());
            }
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);
            GDSettingsUI.Tick();
        }
    }
}
