using System.Collections.Generic;

namespace WorldLineYggdrasil.Ui;

/// <summary>
/// Minimal localization seam for the UI. UI text lives here by key so it can be
/// translated without touching layout code; default language is zh (the mod is
/// Chinese-first). Migrate strings incrementally as labels are touched.
/// </summary>
public static class Loc
{
    public enum Lang { Zh, En }
    public static Lang Language { get; set; } = Lang.Zh;

    private static readonly Dictionary<string, string> Zh = new()
    {
        ["panel_title"] = "世界线·战斗状态图",
        ["tab_combat"] = "战斗图",
        ["tab_run"] = "运行图",
        ["hint_ops"] = "左键点结点=还原 · 右键拖动=平移 · 滚轮缩放 · 「涂画」开关=自由圈点",
        ["empty_graph"] = "当前战斗还没有记录到结点（进入战斗、打出第一张牌后自动生成）",
        ["name_placeholder"] = "给结点起名",
        ["btn_name"] = "命名",
        ["btn_draw"] = "涂画",
        ["btn_select"] = "选择",
        ["btn_clear_ink"] = "清除批注",
        ["lbl_unspecified"] = "未选择",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["panel_title"] = "World Line · Combat Graph",
        ["tab_combat"] = "Combat",
        ["tab_run"] = "Run",
        ["hint_ops"] = "Click node = restore · Right-drag = pan · Wheel = zoom · Draw = annotate",
        ["empty_graph"] = "No nodes recorded yet (enter a combat and play a card).",
        ["name_placeholder"] = "name this node",
        ["btn_name"] = "Name",
        ["btn_draw"] = "Draw",
        ["btn_select"] = "Select",
        ["btn_clear_ink"] = "Clear ink",
        ["lbl_unspecified"] = "none",
    };

    public static string T(string key) =>
        (Language == Lang.En ? En : Zh).TryGetValue(key, out var s) ? s : key;
}