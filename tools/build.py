#!/usr/bin/env python3
"""Generate the AMP template files for Ultima Memento.

Reads the upstream World/Info/Scripts/Settings.cs and the C# sources in ../src, and writes
(into the repository root):

    ultima-memento.kvp            AMP generic module definition
    ultima-mementoconfig.json     settings shown in the AMP web panel (one per MySettings field)
    ultima-mementoports.json      the game port
    ultima-mementometaconfig.json (empty: AMP does not edit any config file directly)
    ultima-mementoupdates.json    "Update": download, deploy, compile
    ultima-mementostart.json      before every start: write Settings.override.cs, install bridge
    manifest.json                 tells AMP this repository is a template repository

Usage:
    python tools/build.py --repo you/amp-ultima-memento   # first run: the GitHub repo you push to
    python tools/build.py                       # later runs keep the repo from manifest.json
    python tools/build.py --ref 2.5.0           # fetch Settings.cs from a tag / branch / commit
    python tools/build.py --settings path/to/Settings.cs

Re-run it whenever upstream adds or removes settings, then commit the regenerated files.
Only the Python standard library is used.
"""

import argparse
import json
import os
import re
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src")
NAME = "ultima-memento"
APP_CONFIG_ID = "d88f2737-58de-4e24-a6b4-2e176e3c7e6b"
CONFIG_VERSION = 2
MIN_AMP_VERSION = "2.7.2.0"
UPSTREAM_REPO = "Jascen/ultima-memento"

# manifest.json: AMP only loads a configuration repository that has one, and its id and prefix
# must differ from every other repository AMP knows (the official one uses an empty prefix).
REPO_ID = "f6fb8d84-58d0-4dc4-a277-d1fce92656d2"
REPO_PREFIX = "Memento"

R = "{{$FullRootDir}}"  # instance/ultima-memento/ (AMP includes the trailing separator)
CSC = r"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

# Settings that AMP manages elsewhere (value written to settings.kvp, but no web-panel field).
MANAGED = {"S_Port": "{{$GamePort}}"}

SECTION_UI = {
    "SYSTEM OPTIONS": ("Memento Server:dns", "Server Options:tune:3"),
    "GAME OPTIONS": ("Memento Gameplay:castle", "Game Options:casino:1"),
    "PLAYER OPTIONS": ("Memento Gameplay:castle", "Player Options:person:2"),
    "QUESTS & TREASURE": ("Memento Gameplay:castle", "Quests & Treasure:redeem:3"),
    "SKILLS": ("Memento Gameplay:castle", "Skills:school:4"),
    "CRAFTING": ("Memento Gameplay:castle", "Crafting:construction:5"),
    "MONSTERS & CREATURES": ("Memento Gameplay:castle", "Monsters & Creatures:pest_control:6"),
    "MERCHANTS": ("Memento Gameplay:castle", "Merchants:storefront:7"),
    "HOMES & SHIPS": ("Memento Gameplay:castle", "Homes & Ships:home:8"),
    "PETS, MOUNTS, & FOLLOWERS": ("Memento Gameplay:castle", "Pets, Mounts & Followers:pets:9"),
    "TOWNS & CITIES": ("Memento Gameplay:castle", "Towns & Cities:location_city:10"),
    "ACKNOWLEDGEMENT": ("Memento Gameplay:castle", "Acknowledgement:verified:11"),
}
FALLBACK_UI = ("Memento Gameplay:castle", "Other:more_horiz:12")

DISPLAY_NAMES = {
    "S_EnableConsole": "Built-in Console Commands",
    "ConsoleLog": "Verbose Console Log",
    "S_WebsiteLink": "Website Link (Message of the Day)",
    "S_WebsiteName": "Website Name (Message of the Day)",
    "S_ServerSaveMinutes": "Autosave Interval (minutes)",
    "S_SaveOnCharacterLogout": "Save When a Character Logs Out",
    "S_RunRoutinesAtStartup": "Run Maintenance Routines at Startup",
    "S_DeleteDays": "Days Before a Character Can Be Deleted",
    "S_AutoAccounts": "Automatic Account Creation",
    "S_Address": "Public Address (IP or host name)",
    "S_ServerName": "Server Name",
    "S_AutoDetect": "Auto-detect Public IP",
    "S_PersistentBlackjack": "Persistent Blackjack Tables",
    "S_FloorTrapTrigger": "Floor Trap Trigger Chance (%)",
    "S_AnnounceTrapSaves": "Announce Avoided Traps",
    "S_EnableDungeonSoundEffects": "Dungeon Ambient Sounds",
    "S_PortalExits": "Exit Portals in Deep Dungeons",
    "S_Bribery": "Murder Count Bribe (gold)",
    "S_ChangeArtyLook": "Allow Changing Artifact Appearance",
    "S_CorpseDecay": "Player Corpse Decay (minutes)",
    "S_BoneDecay": "Player Bones Decay (minutes)",
    "S_AllowTravelToSpecialPlaces": "Magic Travel to Special Outdoor Places",
    "S_NoMacroing": "Block Skill Macroing",
    "S_StatGain": "Stat Gain Rate (50 slow - 10 fast)",
    "S_StatGainDelay": "Minutes Between Stat Gains",
    "S_AllowAlienChoice": "Allow Alien Origin",
    "S_MonsterCharacters": "Monster Characters (0 off, 1-3 size)",
    "S_Belly": "No Hunger or Thirst Indoors",
    "S_FoodCheck": "Hunger Check Interval (minutes)",
    "S_GuildJoinFee": "Guild Join Fee (gold)",
    "S_GuildIncrease": "Guild Fee Grows per Guild Joined",
    "S_MaxResistance": "Max Player Resistance",
    "S_DeathPayLevel": "Resurrection Fee Starts at Level",
    "S_DeathPayAmount": "Resurrection Fee per Level (gold)",
    "S_SpellDamageIncreaseVsMonsters": "Spell Damage vs Monsters (%)",
    "S_SpellDamageIncreaseVsPlayers": "Spell Damage vs Players (%)",
    "S_LowerReg": "Max Lower Reagent Cost (%)",
    "S_LowerRegEnchantment": "Max Lower Reagent Cost from Enchanting (%)",
    "S_LowerMana": "Max Lower Mana Cost (%)",
    "S_LowerManaEnchantment": "Max Lower Mana Cost from Enchanting (%)",
    "S_PlayerLevelMod": "Hit Points / Mana / Stamina per Stat Point",
    "S_AllowCustomTitles": "Allow Custom Titles",
    "S_MinGold": "Starting Gold (minimum)",
    "S_MaxGold": "Starting Gold (maximum)",
    "S_RequirePreviousPowerscroll": "Power Scrolls Must Be Used in Order",
    "S_GetUnidentifiedChance": "Unidentified Item Chance (%)",
    "S_IdentifyItemsOnlyInPack": "Identify Only Items in the Backpack",
    "S_UnidentifiedItem_GuaranteedItemChecks": "Guaranteed Identify Skill Levels",
    "S_LootChance": "Loot Chance",
    "S_GetTimeBetweenQuests": "Minutes Between Bulletin Board Quests",
    "S_GetTimeBetweenArtifactQuests": "Minutes Between Sage Artifact Quests",
    "S_GetGoldCutRate": "Gold Found Rate (%)",
    "S_QuestRewardModifier": "Quest Gold Reward (%)",
    "S_DecoArtySteal": "Steal Decoration Artifacts Repeatedly",
    "S_PedStealThrottle": "Throttle Pedestal Loot",
    "S_WarnSkaraBrae": "Warn Before Entering Skara Brae",
    "S_WarnBottleCity": "Warn Before Entering the Bottle City",
    "S_SkillBoost": "Extra Skill Cap (x100 points)",
    "S_SpecialWeaponAbilSkill": "Weapon Special Ability Skill Required",
    "S_SkillGain": "Faster Skill Gain (0-10)",
    "S_TrainDummies": "Training Dummy Skill Cap",
    "S_PickDips": "Pickpocket Dip Skill Cap",
    "S_TrainMulti": "Training Skill-Check Multiplier",
    "S_CraftMany": "Craft Many Items at Once",
    "S_CraftButtons": "Show 1/10/100 Craft Buttons",
    "S_Resources": "Harvest Amount Multiplier",
    "S_GuildEnhanceMod": "Guild Enhancement Price (%)",
    "S_AllowMacroResources": "Allow Unattended Gathering (no CAPTCHA)",
    "S_RecipesRequireMinSkill": "Recipes Require Minimum Skill",
    "S_RecipesForAnyone": "Recipes Usable by Anyone",
    "S_AutoRepeatHarvesting": "Auto-repeat Harvesting",
    "S_UseLegacyInscription": "Legacy Inscription Crafting",
    "S_CreaturesSearching": "Creatures Detect Hidden Players",
    "S_WyrmBody": "Wyrm Body Graphic",
    "S_DispelFailure": "Chance Enemies Fail to Dispel Summons (%)",
    "S_HPModifier": "Extra Creature Hit Points (%)",
    "S_Normal": "Dungeon Difficulty: Normal (%)",
    "S_Difficult": "Dungeon Difficulty: Difficult (%)",
    "S_Challenging": "Dungeon Difficulty: Challenging (%)",
    "S_Hard": "Dungeon Difficulty: Hard (%)",
    "S_Deadly": "Dungeon Difficulty: Deadly (%)",
    "S_Scary": "Powerful Monsters Roam the Land",
    "S_LineOfSight": "Hide Creatures Out of Line of Sight",
    "S_Purple": "Purple Adventurers Attack Monsters",
    "S_SpawnMin": "Respawn Time Minimum (minutes)",
    "S_SpawnMax": "Respawn Time Maximum (minutes)",
    "S_paralyzeDuration": "Max Monster Paralyze Duration (seconds)",
    "S_SellAll": "Vendors Sell Everything They Stock",
    "S_BuyAll": "Vendors Buy Everything They Stock",
    "S_BuyCloth": "Vendors Buy Tailoring Materials",
    "S_VendorsBuyStuff": "Vendors Buy from Players",
    "S_RichMerchants": "Merchants Have Unlimited Gold",
    "S_UseRemainingGold": "Merchants Pay with Remaining Gold",
    "S_MinMerchant": "Merchant Gold (minimum)",
    "S_MaxMerchant": "Merchant Gold (maximum)",
    "S_ScaleMerchantCoins": "Scale Merchant Gold by Land",
    "S_SellGoldCutRate": "Sell-to-Vendor Price Cut (%)",
    "S_QualityPrices": "Prices Depend on Item Quality",
    "S_MerchantCrates": "Merchant Crates",
    "S_PriceMore": "Vendor Price Increase (%)",
    "S_ResourcePrice": "Resource Price Increase (%)",
    "S_SoldResource": "Vendors Sell Bulk Resources",
    "S_NoBuyResources": "Vendors Refuse to Buy Resources",
    "S_BlackMarket": "Black Market",
    "S_CustomMerchant": "Custom Merchant (Merchant.cs)",
    "S_HouseOwners": "Co-owners Have Owner Permissions",
    "S_LawnsAllowed": "Lawn Tools",
    "S_ShantysAllowed": "Remodeling Tools",
    "S_BoatDecay": "Boat Decay (days)",
    "S_HomeDecay": "House Decay (days)",
    "S_HousesDecay": "Houses Decay",
    "S_HousesPerAccount": "Houses per Account (-1 unlimited)",
    "S_AllowHouseDyes": "Dyeable House Contracts",
    "S_AllowCustomHomes": "Custom House Building",
    "S_Basements": "Public Basements",
    "S_HouseStorage": "Unlimited House Storage",
    "S_HouseStoragePercent": "House Storage Multiplier",
    "S_PetStatGainDelay": "Minutes Between Pet Stat Gains",
    "S_DamageToPets": "Damage Multiplier vs Pets",
    "S_CriticalToPets": "Critical Hit Chance vs Pets (%)",
    "S_PetsMatchMasterNotoriety": "Pets Match Owner Notoriety",
    "S_PetsCanHarmOwner": "Pets Can Harm Their Owner",
    "S_PetsCanHarmSiblingPets": "Pets Can Harm Sibling Pets",
    "S_NoMountsInCertainRegions": "No Mounts in Dungeons and Caves",
    "S_NoMountBuilding": "Dismount in Buildings",
    "S_NoMountsInHouses": "Dismount in Player Houses",
    "S_FastFriends": "Followers Keep Up When Running",
    "S_FriendsAvoidHeels": "Followers Spread Out",
    "S_FriendsGuardFriends": "Followers Guard Each Other",
    "S_Stables": "Extra Stable Slots",
    "S_BondDays": "Days Before a Pet Can Bond",
    "S_GuardsSentenceDeath": "Guards Kill Criminals Instantly",
    "S_GuardsPatrolOutside": "Guards Patrol Outside Town",
    "S_GuardsSprint": "Guards Sprint",
    "S_JailOnlyDeletesMoney": "Jail Only Takes Money",
    "S_Humanoids": "Adventurer Companions in Towns",
    "S_Reviewed": "Settings Reviewed",
}

EXTRA_NOTES = {
    "S_EnableConsole": "Not needed for AMP: the AMP console already accepts save, shutdown, broadcast and online. "
                       "Turning this on switches to the game's own console instead, which the authors warn may crash the server.",
    "S_Address": "Leave empty to let the server auto-detect it.",
    "S_UnidentifiedItem_GuaranteedItemChecks": "Enter a comma separated list of skill levels, for example 0,30,60. Leave empty to turn the check off.",
}


# ----------------------------------------------------------------------------- Settings.cs parsing

DECL = re.compile(r"^\s*public\s+static\s+(bool|int|double|string|int\s*\[\s*\])\s+([A-Za-z_]\w*)\s*=\s*(.*)$")
SECTION = re.compile(r"^\s*//\s*\d{3}\s*-\s*(.+?)\s*/{3,}\s*$")


def strip_line_comment(code):
    """Remove a trailing // comment that is not inside a string literal; return (code, comment)."""
    in_str = False
    i = 0
    while i < len(code):
        c = code[i]
        if in_str:
            if c == "\\":
                i += 2
                continue
            if c == '"':
                in_str = False
        else:
            if c == '"':
                in_str = True
            elif code.startswith("//", i):
                return code[:i], code[i + 2:].strip()
        i += 1
    return code, ""


def parse_settings(text):
    fields = []
    section = None
    comment = []
    desc = ""
    last_was_decl = False
    lines = text.replace("\r\n", "\n").split("\n")
    i = 0
    in_index = True
    while i < len(lines):
        line = lines[i]
        stripped = line.strip()
        m = SECTION.match(line)
        if m and not in_index:
            section = m.group(1).strip()
            comment, desc, last_was_decl = [], "", False
            i += 1
            continue
        if stripped.startswith("// 012 - ACKNOWLEDGEMENT") and in_index:
            in_index = False  # end of the INDEX block at the top of the file
            i += 1
            continue
        if stripped.startswith("//"):
            body = stripped[2:].strip()
            if re.fullmatch(r"/*", body) or in_index:
                i += 1
                continue
            if last_was_decl:
                comment, last_was_decl = [], False
            comment.append(body)
            i += 1
            continue
        m = DECL.match(line)
        if m and section:
            ftype = re.sub(r"\s+", "", m.group(1))
            name = m.group(2)
            rest = m.group(3)
            inline = ""
            if ftype == "int[]":
                buf = [rest]
                while ";" not in strip_line_comment(buf[-1])[0] and i + 1 < len(lines):
                    i += 1
                    buf.append(lines[i])
                value = " ".join(strip_line_comment(b)[0].strip() for b in buf)
                value = value[: value.rfind(";")].strip()
            else:
                code, inline = strip_line_comment(rest)
                value = code.strip()
                if not value.endswith(";"):
                    raise SystemExit("Unsupported declaration for %s: %s" % (name, line.strip()))
                value = value[:-1].strip()
            if comment:
                desc = clean_description(comment)
                comment = []
            fields.append({
                "name": name,
                "type": ftype,
                "raw": value,
                "default": amp_default(ftype, value),
                "section": section,
                "description": desc,
                "inline": inline,
            })
            last_was_decl = True
            i += 1
            continue
        i += 1
    return fields


def clean_description(lines):
    out = []
    for l in lines:
        if not l:
            continue
        if re.match(r"^(EXAMPLES?\b:?)$", l) or "public static" in l:
            continue
        out.append(l)
    text = " ".join(out)
    text = re.sub(r"\s+", " ", text).strip()
    return text


def amp_default(ftype, raw):
    if ftype == "bool":
        return raw
    if ftype in ("int", "double"):
        return raw.rstrip("dDfFmM")
    if ftype == "string":
        if raw == "null":
            return ""
        if raw.startswith('@"'):
            return raw[2:-1].replace('""', '"')
        return json.loads(raw)
    if ftype == "int[]":
        m = re.search(r"\{(.*)\}", raw)
        items = [p.strip() for p in (m.group(1) if m else "").split(",") if p.strip()]
        return ",".join(items)
    raise SystemExit("Unsupported type " + ftype)


def humanize(name):
    n = name[2:] if name.startswith("S_") else name
    n = n.replace("_", " ")
    n = re.sub(r"(?<=[a-z0-9])(?=[A-Z])", " ", n)
    n = re.sub(r"(?<=[A-Z])(?=[A-Z][a-z])", " ", n)
    return n[:1].upper() + n[1:]


# ----------------------------------------------------------------------------- AMP settings manifest

def setting(display, category, subcategory, field, input_type, default, description, keywords, enum=None, **extra):
    s = {
        "DisplayName": display,
        "Category": category,
        "Subcategory": subcategory,
        "Description": description,
        "Keywords": keywords,
        "FieldName": field,
        "InputType": input_type,
        "IsFlagArgument": False,
        "ParamFieldName": field,
        "IncludeInCommandLine": False,
        "DefaultValue": default,
        "EnumValues": enum or {},
    }
    s.update(extra)
    return s


def amp_settings():
    cat = "Memento Server:dns"
    inst = "Install & Updates:system_update:1"
    adm = "Administration:admin_panel_settings:2"
    return [
        setting("Install Source", cat, inst, "AMP_InstallSource", "enum", "latest",
                "Where 'Update' gets the server from. Every option downloads the official release zip, because only the "
                "release contains the map data files (Data/Files) the server needs. 'Source code' then puts the code of a "
                "GitHub branch, tag or commit on top of it, so you can run fixes that are not released yet. "
                "Your Saves, Backups and Settings.override.cs are never replaced by an update.",
                "install,update,release,version,git,branch,source",
                {"latest": "Latest official release (recommended)",
                 "tag": "A specific release (set Release Tag)",
                 "git": "Latest release + source code from a branch, tag or commit"}),
        setting("Release Tag", cat, inst, "AMP_ReleaseTag", "text", "",
                "Used when Install Source is 'A specific release'. The GitHub release tag, for example 2.5.0.",
                "release,tag,version", Placeholder="2.5.0"),
        setting("Branch, Tag or Commit", cat, inst, "AMP_GitRef", "text", "main",
                "Used when Install Source is 'source code'. Any GitHub branch, tag or commit hash.",
                "git,branch,commit,ref,source", Placeholder="main"),
        setting("GitHub Repository", cat, inst, "AMP_GitHubRepo", "text", UPSTREAM_REPO,
                "owner/name of the GitHub repository to install from. Change it only to run a fork.",
                "github,repository,fork", Placeholder=UPSTREAM_REPO),
        setting("Release Asset", cat, inst, "AMP_ReleaseAsset", "text", "Ultima-Memento-Server.zip",
                "File name of the server zip attached to each GitHub release.",
                "release,asset,zip"),
        setting("Server Core (World.exe)", cat, inst, "AMP_CoreBuild", "enum", "compile",
                "How World.exe is produced on Windows. 'Compile' builds it from the downloaded source with the C# compiler "
                "that ships with Windows, which always matches the code you installed. Linux always compiles WorldLinux.exe "
                "with Mono. Only use the prebuilt file with 'Latest official release' or 'A specific release'.",
                "compile,build,exe,core,prebuilt",
                {"compile": "Compile on this machine (recommended)",
                 "prebuilt": "Use the World.exe included in the release (Windows only)"}),
        setting("Info Folder on Update", cat, inst, "AMP_InfoPolicy", "enum", "keep",
                "What an update does with the files in World/Info (news, rules, articles, decorations, spawns, Merchant.cs). "
                "Settings.cs and the .csproj files there are always updated so they match the new code, and "
                "Settings.override.cs is never replaced.",
                "info,news,rules,merchant,update,keep,overwrite",
                {"keep": "Keep my files, only add new ones (recommended)",
                 "overwrite": "Replace them with the new version"}),
        setting("Manage Settings.override.cs", cat, adm, "AMP_ManageOverrides", "checkbox", "true",
                "When on, every start rewrites World/Info/Scripts/Settings.override.cs from the settings on these pages. "
                "Only settings whose value differs from the default in Settings.cs are written. Turn it off to maintain "
                "that file by hand; AMP will then leave it alone and the gameplay settings here have no effect.",
                "override,settings,manage,generate",
                {"False": "false", "True": "true"}),
        setting("Owner Account Name", cat, adm, "AMP_OwnerUsername", "text", "Admin",
                "Account that 'Owner Account Password' applies to. It is created if it does not exist, and given Owner "
                "access. The starting world ships with an Owner account called Admin.",
                "admin,owner,account,username"),
        setting("Owner Account Password", cat, adm, "AMP_OwnerPassword", "password", "",
                "Sets the password of the owner account on the next start. Leave empty to keep the current password. "
                "The starting world's Admin account uses the publicly known password 'password', so set this before "
                "other people can reach the server.",
                "admin,owner,password,security"),
        setting("Extra Command Line Arguments", cat, adm, "AMP_ExtraArgs", "text", "",
                "Extra arguments for the server, for example -debug (debug script build) or -nocache (always recompile scripts).",
                "arguments,debug,nocache,profile"),
    ]


def game_settings(fields):
    out = []
    for f in fields:
        if f["name"] in MANAGED:
            continue
        category, subcategory = SECTION_UI.get(f["section"], FALLBACK_UI)
        name = DISPLAY_NAMES.get(f["name"])
        if not name:
            name = humanize(f["name"])
            if f["inline"]:
                name += " (" + f["inline"] + ")"
        if f["name"].startswith("S_Safari_") and f["inline"]:
            name = "Safari Animals: " + f["inline"] + " (%)"
        desc = f["description"]
        note = EXTRA_NOTES.get(f["name"])
        if note:
            desc = (desc + " " + note).strip()
        if f["raw"] == "null":
            shown_default = "empty (auto)"
        elif f["default"] == "":
            shown_default = "empty"
        else:
            shown_default = f["default"]
        desc = (desc + " Default: " + shown_default + ". [" + f["name"] + "]").strip()
        keywords = ",".join(sorted(set([f["name"], f["name"].replace("S_", "")] + re.findall(r"[a-z]{4,}", name.lower()))))
        t = f["type"]
        if t == "bool":
            s = setting(name, category, subcategory, f["name"], "checkbox", f["default"], desc, keywords,
                        {"False": "false", "True": "true"})
        elif t == "int":
            s = setting(name, category, subcategory, f["name"], "number", f["default"], desc, keywords)
        elif t == "double":
            s = setting(name, category, subcategory, f["name"], "number", f["default"], desc, keywords, MultipleOf="0.01")
        elif t == "int[]":
            s = setting(name, category, subcategory, f["name"], "text", f["default"], desc, keywords, Placeholder="0,30,60")
        else:
            s = setting(name, category, subcategory, f["name"], "text", f["default"], desc, keywords)
        out.append(s)
    return out


# ----------------------------------------------------------------------------- stages

def stage(name, platform, source, args, data=None, **extra):
    s = {"UpdateStageName": name, "UpdateSourcePlatform": platform, "UpdateSource": source}
    if data is not None:
        s["UpdateSourceData"] = data
    if args is not None:
        s["UpdateSourceArgs"] = args
    s.update(extra)
    s.setdefault("SkipOnFailure", False)
    return s


def q(path):
    """Quote a path for an Executable argument string (AMP splits arguments like Windows does)."""
    return '"' + path + '"'


def helper_stages():
    """Write and compile MementoAmp.exe (used by both the update and the pre-start stages)."""
    helper_src = read_src("MementoAmp.cs")
    return [
        stage("Create Helper Folder", "All", "CreateDirectory", R + "amp"),
        stage("Write Helper Source", "All", "CreateFile", R + "amp/MementoAmp.cs", helper_src, OverwriteExistingFiles=True),
        stage("Compile Helper", "Windows", "Executable",
              '/C ""' + CSC + '" /nologo /optimize /out:"' + R + 'amp\\MementoAmp.exe" "' + R + 'amp\\MementoAmp.cs""',
              "cmd.exe"),
        stage("Compile Helper", "Linux", "Executable",
              '-c "' + MONO_CHECK + 'mcs -optimize+ -out:\\"' + R + 'amp/MementoAmp.exe\\" \\"' + R + 'amp/MementoAmp.cs\\""',
              "/bin/bash"),
    ]


# Linux: stop with instructions instead of a bare "mcs: command not found" when Mono is missing.
# (Inside a double-quoted bash -c argument, so no double quotes or apostrophes in the message.)
MONO_CHECK = ("command -v mcs >/dev/null 2>&1 && command -v mono >/dev/null 2>&1 || { "
              "echo 'ERROR: Mono is not installed where this instance runs (the mcs compiler was not found).'; "
              "echo 'Ultima Memento needs Mono on Linux. Either recreate this instance with the Docker container "
              "option turned on (its image includes Mono), or install Mono on the host: sudo apt install mono-complete'; "
              "exit 1; }; ")


def run_helper(name, args_win, args_linux, **extra):
    return [
        stage(name, "Windows", "Executable", args_win, R + "amp\\MementoAmp.exe", **extra),
        stage(name, "Linux", "Executable", q(R + "amp/MementoAmp.exe") + " " + args_linux, "/usr/bin/mono", **extra),
    ]


def update_stages():
    gh = "https://github.com/{{AMP_GitHubRepo}}"
    staging_w, staging_l = R + "amp\\staging", R + "amp/staging"
    world_w, world_l = R + "World", R + "World"
    stages = helper_stages()
    stages += run_helper("Prepare Download Folder",
                         "prepare --staging " + q(staging_w),
                         "prepare --staging " + q(staging_l))
    for mode in ("latest", "git"):
        stages.append(stage("Download Latest Release", "All", "FetchURL", None,
                            gh + "/releases/latest/download/{{AMP_ReleaseAsset}}",
                            UpdateSourceTarget=R + "amp/staging/release",
                            UnzipUpdateSource=True, OverwriteExistingFiles=True, DeleteAfterExtract=True,
                            UpdateSourceConditionSetting="AMP_InstallSource", UpdateSourceConditionValue=mode))
    stages.append(stage("Download Release {{AMP_ReleaseTag}}", "All", "FetchURL", None,
                        gh + "/releases/download/{{AMP_ReleaseTag}}/{{AMP_ReleaseAsset}}",
                        UpdateSourceTarget=R + "amp/staging/release",
                        UnzipUpdateSource=True, OverwriteExistingFiles=True, DeleteAfterExtract=True,
                        UpdateSourceConditionSetting="AMP_InstallSource", UpdateSourceConditionValue="tag"))
    stages.append(stage("Download Source {{AMP_GitRef}}", "All", "FetchURL", None,
                        gh + "/archive/{{AMP_GitRef}}.zip",
                        UpdateSourceTarget=R + "amp/staging/source",
                        UnzipUpdateSource=True, OverwriteExistingFiles=True, DeleteAfterExtract=True,
                        UpdateSourceConditionSetting="AMP_InstallSource", UpdateSourceConditionValue="git"))
    deploy = "deploy --staging {s} --world {w} --mode {{{{AMP_InstallSource}}}} --info {{{{AMP_InfoPolicy}}}}"
    stages += run_helper("Install Server Files",
                         deploy.format(s=q(staging_w), w=q(world_w)),
                         deploy.format(s=q(staging_l), w=q(world_l)))
    stages.append(stage("Compile World.exe", "Windows", "Executable",
                        '/C "cd /d "' + R + 'World\\Source\\Tools" && "' + CSC + '" /nologo /optimize /unsafe /t:exe '
                        '/out:..\\..\\World.exe /win32icon:..\\System\\icon.ico /d:NEWTIMERS /d:NEWPARENT '
                        '/recurse:..\\System\\*.cs /main:Server.Core"',
                        "cmd.exe",
                        UpdateSourceConditionSetting="AMP_CoreBuild", UpdateSourceConditionValue="compile"))
    stages.append(stage("Compile WorldLinux.exe", "Linux", "Executable",
                        '-c "cd \\"' + R + 'World/Source/Tools\\" && mcs -optimize+ -unsafe -t:exe -out:../../WorldLinux.exe '
                        "-win32icon:../System/icon.ico -nowarn:219,414 -d:NEWTIMERS -d:NEWPARENT -d:MONO "
                        "-recurse:'../System/*.cs' -main:Server.Core\"",
                        "/bin/bash"))
    stages.append(stage("Map zlib for Mono", "Linux", "CreateFile", R + "World/WorldLinux.exe.config",
                        '<?xml version="1.0" encoding="utf-8"?>\n<configuration>\n'
                        '  <dllmap dll="libz" target="libz.so.1" os="!windows" />\n</configuration>\n',
                        OverwriteExistingFiles=True))
    return stages


def start_stages(fields):
    values = ["# Written by AMP before every start from the Configuration page. Do not edit."]
    defaults = ["# Defaults of the AMP template (Settings.cs when the template was generated)."]
    for f in fields:
        values.append("%s=%s" % (f["name"], MANAGED.get(f["name"], "{{" + f["name"] + "}}")))
        defaults.append("%s=%s" % (f["name"], f["default"]))
    runtime = "OwnerUsername={{AMP_OwnerUsername}}\nOwnerPassword={{AMP_OwnerPassword}}\n"
    stages = helper_stages()
    stages += [
        stage("Write AMP Bridge Source", "All", "CreateFile", R + "amp/AmpBridge.cs", read_src("AmpBridge.cs"),
              OverwriteExistingFiles=True),
        stage("Write Settings Values", "All", "CreateFile", R + "amp/settings.kvp", "\n".join(values) + "\n",
              OverwriteExistingFiles=True),
        stage("Write Template Defaults", "All", "CreateFile", R + "amp/template-defaults.kvp", "\n".join(defaults) + "\n",
              OverwriteExistingFiles=True),
        stage("Write Owner Account Settings", "All", "CreateFile", R + "amp/runtime.kvp", runtime,
              OverwriteExistingFiles=True),
    ]
    pre = ("prestart --world {w} --exe {exe} --values {v} --defaults {d} --bridge {b} --manage {{{{AMP_ManageOverrides}}}}")
    stages += run_helper(
        "Generate Settings.override.cs",
        pre.format(w=q(R + "World"), exe="World.exe", v=q(R + "amp\\settings.kvp"),
                   d=q(R + "amp\\template-defaults.kvp"), b=q(R + "amp\\AmpBridge.cs")),
        pre.format(w=q(R + "World"), exe="WorldLinux.exe", v=q(R + "amp/settings.kvp"),
                   d=q(R + "amp/template-defaults.kvp"), b=q(R + "amp/AmpBridge.cs")))
    return stages


# ----------------------------------------------------------------------------- kvp

def kvp():
    lines = [
        ("Meta.DisplayName", "Ultima Memento"),
        ("Meta.Description", "Ultima Memento - an Ultima Online shard for single players and small groups, based on Ruins & Riches (RunUO)"),
        ("Meta.OS", "Windows, Linux"),
        ("Meta.AarchSupport", "NotSupported"),
        ("Meta.Arch", "x86_64"),
        ("Meta.Author", "AMP template for Jascen/ultima-memento"),
        ("Meta.URL", "https://github.com/Jascen/ultima-memento"),
        ("Meta.DisplayImageSource", "url:https://uo-memento.com/assets/images/memento-m.png"),
        ("Meta.EndpointURIFormat", ""),
        ("Meta.ConfigManifest", NAME + "config.json"),
        ("Meta.MetaConfigManifest", NAME + "metaconfig.json"),
        ("Meta.ConfigRoot", NAME + ".kvp"),
        ("Meta.MinAMPVersion", MIN_AMP_VERSION),
        ("Meta.SpecificDockerImage", "cubecoders/ampbase:mono-latest"),
        ("Meta.DockerRequired", "False"),
        ("Meta.DockerBaseReadOnly", "False"),
        ("Meta.ContainerPolicy", "SupportedOnWindows, RecommendedOnLinux"),
        ("Meta.ContainerPolicyReason", "Ultima Memento needs Mono, including its C# compiler, on Linux. The container image already has it, so nothing has to be installed on the host. Without a container, install mono-complete on the host first."),
        ("Meta.ExtraSetupStepsURI", "https://github.com/Jascen/ultima-memento#readme"),
        ("Meta.Prerequisites", "[]"),
        ("Meta.ExtraContainerPackages", "[]"),
        ("Meta.ConfigReleaseState", "NotSpecified"),
        ("Meta.NoCommercialUsage", "False"),
        ("Meta.ConfigVersion", str(CONFIG_VERSION)),
        ("Meta.ReleaseNotes", ""),
        ("Meta.BreakingReleaseNotes", ""),
        ("Meta.AppConfigId", APP_CONFIG_ID),
        ("Meta.ImportableExtensions", "[]"),
        ("Meta.AppIsMultiIPAware", "False"),
        ("App.DisplayName", "Ultima Memento"),
        ("App.RootDir", "./" + NAME + "/"),
        ("App.BaseDirectory", "./" + NAME + "/World/"),
        ("App.StoresSupported", "None"),
        ("App.StoreSpecificSettings", "{}"),
        ("App.StoreDownloadLocations", "{}"),
        ("App.ExecutableWin", "World\\World.exe"),
        ("App.ExecutableLinux", "/usr/bin/mono"),
        ("App.WorkingDir", "World"),
        ("App.LinuxCommandLineArgs", "WorldLinux.exe"),
        ("App.WindowsCommandLineArgs", ""),
        ("App.CommandLineArgs", "{{$PlatformArgs}} {{AMP_ExtraArgs}}"),
        ("App.UseLinuxIOREDIR", "False"),
        ("App.AppSettings", "{}"),
        ("App.EnvironmentVariables", "{}"),
        ("App.CommandLineParameterFormat", "-{0} {1}"),
        ("App.CommandLineParameterDelimiter", " "),
        ("App.ExitMethod", "String"),
        ("App.ExitTimeout", "120"),
        ("App.ExitString", "shutdown"),
        ("App.ExitFile", "app_exit.lck"),
        ("App.RestartDelaySeconds", "0"),
        ("App.HasWriteableConsole", "True"),
        ("App.HasReadableConsole", "True"),
        ("App.UDPLogger", "False"),
        ("App.SupportsLiveSettingsChanges", "False"),
        ("App.LiveSettingChangeCommandFormat", ""),
        ("App.ForceIPBinding", "False"),
        ("App.SupportsIPv6", "False"),
        ("App.ApplicationIPBinding", "0.0.0.0"),
        ("App.Ports", "@IncludeJson[" + NAME + "ports.json]"),
        ("App.AdminPortRef", ""),
        ("App.PrimaryApplicationPortRef", "GamePort"),
        ("App.UniversalSleepApplicationUDPPortRef", ""),
        ("App.UniversalSleepSteamQueryPortRef", ""),
        ("App.MaxUsers", "100"),
        ("App.UseRandomAdminPassword", "False"),
        ("App.PersistRandomPassword", "False"),
        ("App.RemoteAdminPassword", ""),
        ("App.AdminMethod", "STDIO"),
        ("App.IgnoreSTDOUTAfterRCON", "False"),
        ("App.AdminLoginTransform", "None"),
        ("App.StripANSIControlCodes", "True"),
        ("App.LoginTransformPrefix", ""),
        ("App.RCONConnectDelaySeconds", "5"),
        ("App.RCONHeartbeatMinutes", "0"),
        ("App.RCONHeartbeatCommand", ""),
        ("App.RCONSelectIPMethod", "Default"),
        ("App.TelnetLoginFormat", "{0}"),
        ("App.TelnetNewLineType", "Default"),
        ("App.TailLogFilePath", ""),
        ("App.UpdateSources", "@IncludeJson[" + NAME + "updates.json]"),
        ("App.PreStartStages", "@IncludeJson[" + NAME + "start.json]"),
        ("App.CommandTriggers", "{}"),
        ("App.UserActions", "[]"),
        ("App.ForceUpdate", "False"),
        ("App.ForceUpdateReason", ""),
        ("App.Compatibility", "None"),
        ("App.SteamUpdateAnonymousLogin", "True"),
        ("App.RapidStartup", "False"),
        ("App.SmartExcludeExemptions", "[]"),
        ("App.SmartExcludeSupported", "False"),
        ("App.DumpFullChildProcessTree", "False"),
        ("App.MonitorChildProcessName", ""),
        ("App.MonitorDirectChildOnly", "False"),
        ("App.SupportsUniversalSleep", "False"),
        ("App.UseSteamQueryForStatus", "False"),
        ("App.WakeupMode", "Any"),
        ("App.ApplicationReadyMode", "RegexMatch"),
        ("App.QuiesceCommand", "save"),
        ("App.DequiesceCommand", ""),
        ("App.QuiesceSettleDelayMilliseconds", "15000"),
        ("Console.FilterMatchRegex", ""),
        ("Console.FilterMatchReplacement", ""),
        ("Console.ThrowawayMessageRegex", ""),
        ("Console.AppReadyRegex", r"^\[AMP\] Server started\..*$"),
        ("Console.UserJoinRegex", r"^\[AMP\] Player joined: (?<username>.+?) \(account .+?, serial (?<userid>0x[0-9A-F]+)\)$"),
        ("Console.UserLeaveRegex", r"^\[AMP\] Player left: (?<username>.+?) \(account .+?, serial (?<userid>0x[0-9A-F]+)\)$"),
        ("Console.UserChatRegex", ""),
        ("Console.UpdateAvailableRegex", ""),
        ("Console.PreConnectRegex", ""),
        ("Console.ConnectIPRegex", ""),
        ("Console.MetricsRegex", ""),
        ("Console.ServerInfoRegex", ""),
        ("Console.ServerAuthURLPromptRegex", ""),
        ("Console.ServerAuthAckRegex", ""),
        ("Console.ConsoleFormatRegex", ""),
        ("Console.DownloadProgressRegex", ""),
        ("Console.HideFromConsoleRegex", ""),
        ("Console.SuppressLogAtStart", "False"),
        ("Console.UserActions", "{}"),
        ("Limits.SleepMode", "False"),
        ("Limits.SleepOnStart", "False"),
        ("Limits.SleepDelayMinutes", "5"),
        ("Limits.DozeDelay", "2"),
        ("Limits.AutoRetryCount", "5"),
        ("Limits.SleepStartThresholdSeconds", "25"),
    ]
    return "\n".join(k + "=" + v for k, v in lines) + "\n"


# ----------------------------------------------------------------------------- checks and output

PLACEHOLDER = re.compile(r"\{\{(\$?[A-Za-z_][\w.]*)\}\}")
AMP_VARIABLES = {"$FullRootDir", "$FullBaseDir", "$GamePort", "$PlatformArgs"}


def read_src(name):
    with open(os.path.join(SRC, name), encoding="utf-8") as fh:
        text = fh.read()
    if "{{" in text or "}}" in text:
        bad = [i + 1 for i, l in enumerate(text.split("\n")) if "{{" in l or "}}" in l]
        raise SystemExit("%s contains a double curly brace on line(s) %s; AMP would treat it as a placeholder." % (name, bad))
    return text.replace("\r\n", "\n")


def check_placeholders(obj, known, where):
    if isinstance(obj, dict):
        for v in obj.values():
            check_placeholders(v, known, where)
    elif isinstance(obj, list):
        for v in obj:
            check_placeholders(v, known, where)
    elif isinstance(obj, str):
        for name in PLACEHOLDER.findall(obj):
            if name not in known and name not in AMP_VARIABLES:
                raise SystemExit("%s references unknown setting {{%s}}" % (where, name))


def write(name, content):
    path = os.path.join(ROOT, name)
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(content)
    print("wrote", os.path.relpath(path, ROOT))


def dump(obj):
    return json.dumps(obj, indent=4, ensure_ascii=True) + "\n"


def load_settings(args):
    if args.settings:
        with open(args.settings, encoding="utf-8-sig") as fh:
            return fh.read(), args.settings
    url = "https://raw.githubusercontent.com/%s/%s/World/Info/Scripts/Settings.cs" % (UPSTREAM_REPO, args.ref)
    with urllib.request.urlopen(url) as resp:
        return resp.read().decode("utf-8-sig"), url


def template_repo(arg):
    """owner/name of the GitHub repository this template is pushed to (--repo, else manifest.json)."""
    repo = arg
    if not repo:
        try:
            with open(os.path.join(ROOT, "manifest.json"), encoding="utf-8-sig") as fh:
                url = json.load(fh).get("url", "")
            repo = re.sub(r"^https://github\.com/", "", url).strip("/")
        except (OSError, ValueError):
            repo = ""
    repo = re.sub(r"^(https?://)?github\.com/", "", (repo or "").strip()).strip("/")
    repo = re.sub(r"\.git$", "", repo)
    if not re.match(r"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$", repo):
        raise SystemExit("Pass --repo owner/name: the GitHub repository you push this template to.")
    return repo


def manifest(repo):
    return {
        "id": REPO_ID,
        "authors": [repo.split("/")[0]],
        "origin": "https://github.com/%s.git" % repo,
        "url": "https://github.com/%s" % repo,
        "imagefile": "",
        "prefix": REPO_PREFIX,
        "repotype": "AppTemplates",
    }


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--settings", help="path to a local Settings.cs")
    ap.add_argument("--ref", default="main", help="branch/tag/commit to fetch Settings.cs from (default: main)")
    ap.add_argument("--repo", help="owner/name of the GitHub repo this template is pushed to (kept in manifest.json)")
    args = ap.parse_args()
    repo = template_repo(args.repo)

    text, origin = load_settings(args)
    fields = parse_settings(text)
    if len(fields) < 50:
        raise SystemExit("Only %d settings parsed from %s; the format may have changed." % (len(fields), origin))
    names = [f["name"] for f in fields]
    if len(set(names)) != len(names):
        raise SystemExit("Duplicate setting names in Settings.cs")
    for m in MANAGED:
        if m not in names:
            raise SystemExit("%s is no longer in Settings.cs" % m)

    config = amp_settings() + game_settings(fields)
    known = {s["FieldName"] for s in config}
    updates = update_stages()
    start = start_stages(fields)
    check_placeholders(updates, known, "updates")
    check_placeholders(start, known, "start")
    kvp_text = kvp()
    check_placeholders(kvp_text, known, "kvp")

    ports = [{
        "Protocol": "TCP",
        "Port": 2593,
        "Ref": "GamePort",
        "Name": "Game Port",
        "Description": "Port that Ultima Online clients connect to (S_Port in Settings.cs)",
    }]

    write(NAME + ".kvp", kvp_text)
    write(NAME + "config.json", dump(config))
    write(NAME + "ports.json", dump(ports))
    write(NAME + "metaconfig.json", "[]\n")
    write(NAME + "updates.json", dump(updates))
    write(NAME + "start.json", dump(start))
    write("manifest.json", json.dumps(manifest(repo), indent="\t") + "\n")
    print("%d game settings from %s (+%d AMP settings, %d managed by AMP)" % (
        len(fields) - len(MANAGED), origin, len(amp_settings()), len(MANAGED)))


if __name__ == "__main__":
    main()
