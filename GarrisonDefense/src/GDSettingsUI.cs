using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;

namespace GarrisonDefense
{
    // Oyun içi ayar penceresi, şehir/kale menü seçenekleri ve harita kısayolları.
    // Ek mod gerektirmez: oyunun kendi seçim pencereleri kullanılır.
    public static class GDSettingsUI
    {
        private static GDSettings S => GDSettings.Current;

        // Ayar satırı: başlık, mevcut değer metni ve seçilince yapılacak değişiklik.
        private class Entry
        {
            public string Label;
            public Func<string> Value;
            public Action Change;
            public string Hint;
        }

        private static readonly float[] Shares = { 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f };
        private static readonly float[] CountMargins = { 1.1f, 1.2f, 1.5f, 2.0f };
        private static readonly float[] PowerMargins = { 1.1f, 1.3f, 1.5f, 2.0f };
        private static readonly float[] ThreatRadii = { 4f, 6f, 8f, 10f };
        private static readonly float[] LeashRadii = { 8f, 12f, 16f, 20f };
        private static readonly float[] ChaseHours = { 12f, 24f, 36f, 48f };
        private static readonly float[] ReliefRadii = { 20f, 35f, 50f };

        private const string ResetId = "__reset";

        // Pencere kapanırken yenisini hemen açmak onu da kapatabilir; bir sonraki karede açılır.
        private static bool _reopenPending;

        private static string OnOff(bool b) => b ? "AÇIK" : "KAPALI";
        private static string Pct(float f) => "%" + MathF.Round(f * 100f);
        private static string Num(float f) => f.ToString("0.#");

        // Dizideki bir sonraki değeri döndürür (sonda başa sarar); mevcut değer dizide yoksa en yakından devam eder.
        private static float Next(float[] values, float current)
        {
            int idx = 0;
            float best = float.MaxValue;
            for (int i = 0; i < values.Length; i++)
            {
                float d = Math.Abs(values[i] - current);
                if (d < best) { best = d; idx = i; }
            }
            return values[(idx + 1) % values.Length];
        }

        private static List<Entry> BuildEntries()
        {
            return new List<Entry>
            {
                // --- Kapsam ---
                new Entry { Label = "[Kapsam] Yapay zekâ lordlarının kaleleri de korusun", Value = () => OnOff(S.ApplyToAiLords),
                    Change = () => S.ApplyToAiLords = !S.ApplyToAiLords,
                    Hint = "Açıkken tüm klanların şehir ve kaleleri garnizon müfrezesi çıkarır. Dengeyi değiştirir." },
                new Entry { Label = "[Kapsam] Sadece valisi olan yerleşimler", Value = () => OnOff(S.RequireGovernor),
                    Change = () => S.RequireGovernor = !S.RequireGovernor },
                new Entry { Label = "[Kapsam] Haydutlara müfreze çıkar", Value = () => OnOff(S.TargetBandits),
                    Change = () => S.TargetBandits = !S.TargetBandits },
                new Entry { Label = "[Kapsam] Düşman lord ve devriyelerine müfreze çıkar", Value = () => OnOff(S.TargetLords),
                    Change = () => S.TargetLords = !S.TargetLords },

                // --- Müfreze ---
                new Entry { Label = "[Müfreze] Garnizonun en fazla dışarı çıkabilecek kısmı", Value = () => Pct(S.MaxGarrisonShare),
                    Change = () => S.MaxGarrisonShare = Next(Shares, S.MaxGarrisonShare) },
                new Entry { Label = "[Müfreze] Asker sayısı düşmanın en az kaç katı", Value = () => Num(S.CountMargin) + "x",
                    Change = () => S.CountMargin = Next(CountMargins, S.CountMargin) },
                new Entry { Label = "[Müfreze] Güç düşmanın en az kaç katı", Value = () => Num(S.PowerMargin) + "x",
                    Change = () => S.PowerMargin = Next(PowerMargins, S.PowerMargin) },
                new Entry { Label = "[Müfreze] Tehdit yarıçapı (köy/kale çevresi)", Value = () => Num(S.ThreatRadius),
                    Change = () =>
                    {
                        S.ThreatRadius = Next(ThreatRadii, S.ThreatRadius);
                        if (S.LeashRadius < S.ThreatRadius) S.LeashRadius = S.ThreatRadius;
                    } },
                new Entry { Label = "[Müfreze] En uzak kovalama mesafesi", Value = () => Num(S.LeashRadius),
                    Change = () =>
                    {
                        S.LeashRadius = Next(LeashRadii, S.LeashRadius);
                        if (S.LeashRadius < S.ThreatRadius) S.LeashRadius = LeashRadii.First(r => r >= S.ThreatRadius);
                    } },
                new Entry { Label = "[Müfreze] En uzun kovalama süresi (saat)", Value = () => Num(S.MaxChaseHours),
                    Change = () => S.MaxChaseHours = Next(ChaseHours, S.MaxChaseHours) },
                new Entry { Label = "[Müfreze] Valinin Taktik becerisi etkilesin", Value = () => OnOff(S.GovernorTactics),
                    Change = () => S.GovernorTactics = !S.GovernorTactics,
                    Hint = "Yüksek Taktik: daha küçük güç farkıyla da çıkar. Valisiz: daha temkinli." },
                new Entry { Label = "[Müfreze] Hızlı düşmana önce atlılar", Value = () => OnOff(S.PreferCavalry),
                    Change = () => S.PreferCavalry = !S.PreferCavalry },

                // --- Kuşatma yardımı ---
                new Entry { Label = "[Kuşatma] Komşu garnizonlar kuşatılan kaleye yardım etsin", Value = () => OnOff(S.SiegeRelief),
                    Change = () => S.SiegeRelief = !S.SiegeRelief },
                new Entry { Label = "[Kuşatma] Yardıma katılacak garnizonların uzaklığı", Value = () => Num(S.ReliefRadius),
                    Change = () => S.ReliefRadius = Next(ReliefRadii, S.ReliefRadius) },

                // --- Bildirim ---
                new Entry { Label = "[Bildirim] Müfreze çıktı/döndü mesajları", Value = () => OnOff(S.Notifications),
                    Change = () => S.Notifications = !S.Notifications },
                new Entry { Label = "[Bildirim] 30 günlük özet mesajı", Value = () => OnOff(S.MonthlyReport),
                    Change = () => S.MonthlyReport = !S.MonthlyReport },
            };
        }

        // GDSettings.Current alanlarını düzenleyen oyun içi pencereyi açar.
        // Bir satır seçilip "Değiştir" denince değer bir sonrakine geçer ve pencere yeniden açılır.
        public static void Open()
        {
            if (Campaign.Current == null)
                return;

            List<Entry> entries = BuildEntries();
            var elements = new List<InquiryElement>();
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                elements.Add(new InquiryElement(i, e.Label + ":  " + e.Value(), null, true, e.Hint ?? ""));
            }
            elements.Add(new InquiryElement(ResetId, "Varsayılanlara dön", null, true, "Tüm ayarları başlangıç değerlerine döndürür."));

            MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(
                "Garnizon Savunması — Ayarlar",
                "Değiştirmek istediğin ayarı seç ve \"Değiştir\"e bas. Ayarlar bu kayıt dosyasına kaydedilir.",
                elements, true, 1, 1, "Değiştir", "Kapat",
                selected =>
                {
                    object id = selected.FirstOrDefault()?.Identifier;
                    if (id is int index && index >= 0 && index < entries.Count)
                        entries[index].Change();
                    else if (id as string == ResetId)
                        CopyDefaults();
                    _reopenPending = true;
                },
                null), true, false);
        }

        // Current örneğini değiştirmeden alanları varsayılana çeker (davranış Current'a bağlı).
        private static void CopyDefaults()
        {
            var d = new GDSettings();
            foreach (var field in typeof(GDSettings).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                field.SetValue(S, field.GetValue(d));
        }

        // Şehir ve kale menülerine "Ayarlar" ve "Rapor" seçeneklerini ekler.
        public static void RegisterMenus(CampaignGameStarter starter)
        {
            foreach (string menu in new[] { "town", "castle" })
            {
                starter.AddGameMenuOption(menu, "gd_settings_" + menu, "{=gd_menu_settings}Garnizon Savunması: Ayarlar",
                    args => { args.optionLeaveType = GameMenuOption.LeaveType.Manage; return true; },
                    args => Open(), false, -1);
                starter.AddGameMenuOption(menu, "gd_report_" + menu, "{=gd_menu_report}Garnizon Savunması: Rapor",
                    args => { args.optionLeaveType = GameMenuOption.LeaveType.Manage; return true; },
                    args => GDStatsBehavior.Instance?.ShowReport(), false, -1);
            }
        }

        // Harita ekranında kısayollar: Ctrl+Shift+G ayarlar, Ctrl+Shift+R rapor.
        public static void Tick()
        {
            try
            {
                if (Campaign.Current == null || !(Game.Current?.GameStateManager?.ActiveState is MapState))
                    return;
                if (InformationManager.IsAnyInquiryActive())
                    return;
                if (_reopenPending)
                {
                    _reopenPending = false;
                    Open();
                    return;
                }
                bool ctrl = Input.IsKeyDown(InputKey.LeftControl) || Input.IsKeyDown(InputKey.RightControl);
                bool shift = Input.IsKeyDown(InputKey.LeftShift) || Input.IsKeyDown(InputKey.RightShift);
                if (!ctrl || !shift)
                    return;
                if (Input.IsKeyPressed(InputKey.G))
                    Open();
                else if (Input.IsKeyPressed(InputKey.R))
                    GDStatsBehavior.Instance?.ShowReport();
            }
            catch (Exception)
            {
                // Her karede çalışır; hiçbir hata oyunu bozmamalı.
            }
        }
    }
}
