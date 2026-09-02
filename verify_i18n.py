#!/usr/bin/env python3
"""Static consistency checks for the BunnyGarden2FixMod zh-CN / zh-TW localization.

Run from the repo root:  python verify_i18n.py
Checks:
  1. en/zhCN/zhtw JSON files parse and have identical key sets.
  2. Translated values contain no leftover kana.
  3. zhCN values use no traditional-only characters, zhtw no simplified-only ones.
  4. Every Loc.Tr("...") literal in the C# sources exists in all dictionaries.
"""

import json
import re
import sys
import pathlib

ROOT = pathlib.Path(__file__).parent
MOD = ROOT / "BunnyGarden2FixMod"
LANG = MOD / "Resources" / "lang"

# Character pairs that genuinely differ between simplified and traditional.
TRAD_ONLY = "設體說漢圖選關開內絲線視質機動畫顯層應還頭點風與華輪試樣強護擴後裡時個對發達電順題長陽車門問間閃閉進運過遠遊適邊際雲霧頁們優偉價傳傷條傑兒兩冊凍決況準減創劃務勝單員團圓壓壞複夠婦孫學實專將尋導尷屍屬歲岡峽島幣帶幫幹廣廳張彈徑復憶懷戶房掃擇換擁據擊攝攢敵數斷舊曠暈暫曬書會東標樓檔櫃權款歡歸殘毀氣沒況沖油潑灑潛燈燒營爾豬獵獨獲環產當療癒皺盤監眾裡補裝覺覽觀計記訪訴詞診詢譯讀變讓豐貝財責貨資賓贈躍軌軍軟農違遭遷遲郵鄉鄰醫針錯鍾閒閱隊隨隱雖雜離靜頂預頓頗領頻顆題飛養館馬驗驚鬥魚鳥麥龍龜無"
SIMP_ONLY = "设体说汉图选关开内丝线视质机动画显层应还头点风与华轮试样强护扩后里时个对发达电顺题长阳车门问间闪闭进运过远适边际云雾页们优伟价传伤条杰儿两册冻决况准减创划务胜单员团圆压坏复够妇孙学实专将寻导尴尸属岁冈峡岛币带帮广场张弹径忆怀户房扫择换拥据击摄攒敌数断旧旷晕暂晒书会东标楼档柜权款欢归残毁气没冲泼洒潜灯烧营尔猪猎独获环产当疗愈皱盘监众补装觉览观计记访诉词诊询译读变让丰贝财责货资宾赠跃轨军软农违遭迁迟邮乡邻医针错钟闲阅随隐虽杂离静顶预顿颇领频颗飞养馆马验惊斗鱼鸟麦龙龟无"

# Kana ranges, excluding U+30FB (katakana middle dot, also used as a separator in Chinese).
KANA = re.compile(r"[぀-ヺー-ヿㇰ-ㇿ]")

errors = []


def load(name):
    with open(LANG / name, encoding="utf-8") as f:
        return json.load(f)


try:
    en = load("en.json")
    zhCN = load("zhCN.json")
    zhtw = load("zhtw.json")
except Exception as e:
    print(f"JSON 解析失败: {e}")
    sys.exit(1)

print(f"en: {len(en)} 键, zhCN: {len(zhCN)} 键, zhtw: {len(zhtw)} 键")

# 1. Key-set consistency
en_keys = set(en)
for name, d in (("zhCN", zhCN), ("zhtw", zhtw)):
    missing = sorted(en_keys - set(d))
    extra = sorted(set(d) - en_keys)
    if missing:
        errors.append(f"{name} 缺少键: {missing}")
    if extra:
        errors.append(f"{name} 多余键: {extra}")

# 2. No leftover kana in translated values
for name, d in (("zhCN", zhCN), ("zhtw", zhtw)):
    for k, v in d.items():
        if KANA.search(v):
            errors.append(f"{name} 值含假名残留, 键 {k!r}: {v!r}")

# 3. Script variants
for name, d, banned, label in (
    ("zhCN", zhCN, TRAD_ONLY, "繁体字"),
    ("zhtw", zhtw, SIMP_ONLY, "简体字"),
):
    for k, v in d.items():
        hit = sorted(set(v) & set(banned))
        if hit:
            errors.append(f"{name} 值含{label} {' '.join(hit)}, 键 {k!r}")

# 4. Loc.Tr(...) literals must exist in all dictionaries
def csharp_unescape(s):
    return s.replace("\\n", "\n").replace("\\t", "\t").replace("\\r", "\r").replace('\\"', '"').replace("\\\\", "\\")


literals = set()
tr_re = re.compile(r'Loc\.Tr\(\s*("(?:[^"\\]|\\.)*"(?:\s*\+\s*"(?:[^"\\]|\\.)*")*)')
piece_re = re.compile(r'"((?:[^"\\]|\\.)*)"')
for cs in MOD.rglob("*.cs"):
    if "Generated" in cs.parts:
        continue
    # Strip line comments so XML-doc examples like /// <c>Loc.Tr("...")</c> are skipped.
    text = "\n".join(line for line in cs.read_text(encoding="utf-8").splitlines()
                     if not line.strip().startswith("//"))
    for m in tr_re.finditer(text):
        s = "".join(csharp_unescape(p) for p in piece_re.findall(m.group(1)))
        literals.add(s)

print(f"Loc.Tr 字面量: {len(literals)} 个")
for s in sorted(literals):
    if s not in zhCN:
        errors.append(f"zhCN 缺少 Loc.Tr 字面量 {s!r}")
    if s not in zhtw:
        errors.append(f"zhtw 缺少 Loc.Tr 字面量 {s!r}")

# 5. English section keys (F9 sidebar Category) must be translated.
# The sidebar renders Loc.Tr(category) where category is the English section key,
# so a missing entry would fall back to English.
yaml = (MOD / "Configs.yaml").read_text(encoding="utf-8")
sections = set(re.findall(r"^- section:\s*(\S+)", yaml, re.M))
sections.add("Migration")  # exists in released builds (v1.0.11), absent from master yaml
for k in sorted(sections):
    if k not in zhCN:
        errors.append(f"zhCN 缺少英文 section 键 {k!r}")
    if k not in zhtw:
        errors.append(f"zhtw 缺少英文 section 键 {k!r}")

if errors:
    print("\n".join(errors))
    print(f"\n验证失败: {len(errors)} 个问题")
    sys.exit(1)
print("全部检查通过。")
