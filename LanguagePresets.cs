namespace HdrPlayer;

/// <summary>
/// 首选音轨 / 字幕语言的预设。每种语言列出多个代码：片源里的语言标记很不统一
/// （ISO 639-2 的 chi / zho、639-1 的 zh、BCP 47 的 zh-Hans / zh-TW、民间常用的 chs / cht……）。
/// mpv 按列表顺序依次匹配，用第一个找得到的；所以回退规则直接体现在顺序里：
///   简体中文 → 不区分简繁的中文 → 繁体中文 → 英语
///   繁体中文 → 不区分简繁的中文 → 简体中文 → 英语
///   其它语言 → 英语
/// </summary>
internal static class LanguagePresets
{
    private const string Simplified = "chs,sc,zh-Hans,zh-CN,zh-SG";
    private const string Traditional = "cht,tc,zh-Hant,zh-TW,zh-HK,zh-MO";
    private const string ChineseGeneric = "chi,zho,zh";
    private const string English = "eng,en";

    public const string SubSimplified = Simplified + "," + ChineseGeneric + "," + Traditional + "," + English;
    public const string SubTraditional = Traditional + "," + ChineseGeneric + "," + Simplified + "," + English;

    /// <summary>音轨的中文：普通话 / 粤语一起算（音轨很少区分简繁）。</summary>
    public const string AudioChinese = ChineseGeneric + ",cmn,yue," + Simplified + "," + Traditional + "," + English;

    public static readonly (string Value, string Name)[] Subtitle =
    {
        (SubSimplified, "简体中文"),
        (SubTraditional, "繁体中文"),
        (English, "英语"),
        ("jpn,ja," + English, "日语"),
        ("kor,ko," + English, "韩语"),
    };

    public static readonly (string Value, string Name)[] Audio =
    {
        (English, "英语"),
        (AudioChinese, "中文（普通话 / 粤语）"),
        ("jpn,ja," + English, "日语"),
        ("kor,ko," + English, "韩语"),
    };

    public const string DefaultSubtitle = SubSimplified;
    public const string DefaultAudio = English;

    /// <summary>旧版本的预设值 → 新预设（旧值没有“找不到就退到英语 / 另一种中文”的规则）。</summary>
    public static readonly Dictionary<string, string> Upgrades = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chi,zho,chs,zh,zh-Hans,eng,en"] = SubSimplified, // 旧的默认字幕语言
        ["chs,sc,zh-Hans,zh-CN,chi,zho,zh"] = SubSimplified,
        ["cht,tc,zh-Hant,zh-TW,zh-HK,chi,zho,zh"] = SubTraditional,
        ["chi,zho,chs,cht,zh,zh-Hans,zh-Hant,cmn,yue"] = AudioChinese,
        ["jpn,ja"] = "jpn,ja," + English,
        ["kor,ko"] = "kor,ko," + English,
    };
}
