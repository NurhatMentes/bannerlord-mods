using System.Globalization;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;

namespace LordOrders
{
    public enum OrderType
    {
        None = 0,
        Raid = 1,        // köy yağmala
        Besiege = 2,     // kale/şehir kuşat
        BringFood = 3,   // erzak getir
        Patrol = 4,      // bölgeyi koru
        JoinArmy = 5,    // orduma katıl
        Follow = 6,      // beni takip et
    }

    // Bir lordun yürüttüğü (ya da haberciyle yolda olan) emir.
    // Kayıt için Hero ayrı bir listede, geri kalanı tek satır metin olarak saklanır.
    public class Order
    {
        public Hero Hero;
        public OrderType Type;
        public Settlement Target;     // yağma/kuşatma/devriye hedefi ya da erzak teslim kalesi
        public Settlement Market;     // erzak alınacak şehir
        public bool ToPlayer;         // erzak oyuncuya getirilecek
        public int Amount;            // erzak miktarı (satın alındıktan sonra taşınan miktar)
        public int Stage;             // erzak: 0 pazara gidiyor, 1 teslim ediyor; kuşatma: 1 kuşatma başladı
        public double StartHours;
        public double DeadlineHours;
        public double ArriveHours;    // haberci: emrin lorda ulaşacağı an

        public string Serialize()
        {
            return string.Join("|",
                ((int)Type).ToString(CultureInfo.InvariantCulture),
                Target?.StringId ?? "",
                Market?.StringId ?? "",
                ToPlayer ? "1" : "0",
                Amount.ToString(CultureInfo.InvariantCulture),
                Stage.ToString(CultureInfo.InvariantCulture),
                StartHours.ToString("R", CultureInfo.InvariantCulture),
                DeadlineHours.ToString("R", CultureInfo.InvariantCulture),
                ArriveHours.ToString("R", CultureInfo.InvariantCulture));
        }

        public static Order Deserialize(Hero hero, string data)
        {
            string[] f = (data ?? "").Split('|');
            if (hero == null || f.Length < 9)
                return null;
            var o = new Order { Hero = hero };
            int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int type);
            o.Type = (OrderType)type;
            o.Target = f[1].Length > 0 ? Settlement.Find(f[1]) : null;
            o.Market = f[2].Length > 0 ? Settlement.Find(f[2]) : null;
            o.ToPlayer = f[3] == "1";
            int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out o.Amount);
            int.TryParse(f[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out o.Stage);
            double.TryParse(f[6], NumberStyles.Float, CultureInfo.InvariantCulture, out o.StartHours);
            double.TryParse(f[7], NumberStyles.Float, CultureInfo.InvariantCulture, out o.DeadlineHours);
            double.TryParse(f[8], NumberStyles.Float, CultureInfo.InvariantCulture, out o.ArriveHours);
            return o;   // None: haberciyle gönderilen "emri iptal et" mesajı
        }

        public static string Describe(OrderType type)
        {
            switch (type)
            {
                case OrderType.Raid: return "Yağma";
                case OrderType.Besiege: return "Kuşatma";
                case OrderType.BringFood: return "Erzak getir";
                case OrderType.Patrol: return "Bölgeyi koru";
                case OrderType.JoinArmy: return "Orduya katıl";
                case OrderType.Follow: return "Takip et";
                default: return "-";
            }
        }

        public string Describe()
        {
            string s = Describe(Type);
            if (Type == OrderType.BringFood)
                return s + " (" + Amount + ", " + (ToPlayer ? "sana" : Target?.Name?.ToString()) + ")";
            return Target != null ? s + " (" + Target.Name + ")" : s;
        }
    }
}
