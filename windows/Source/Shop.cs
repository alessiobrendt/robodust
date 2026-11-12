using System;
using System.Collections.Generic;
using System.Linq;

namespace RoboDust;

public enum PetKind { None = 0, Dachshund = 1, Golden = 2, Cat = 3, Squirrel = 4, Fox = 5, Bunny = 6, Penguin = 7, Duck = 8 }
public enum ShopCategory { Skins, Hats, Tuning, Pets, Battle }

/// Everything the robot is wearing: what the renderer draws and the preview shows.
public struct Look
{
    public RobotSkin Skin;
    public int Hat;          // 0 none, 1 party, 2 crown, 3 top hat, 4 cowboy, 5 santa, 6 propeller
    public bool Spoiler, Glow, Exhaust;
    public int Decal;        // 0 none, 1 racing stripes, 2 flames
    public PetKind Pet;
    public int GlowColor;    // 0 blue, 1 pink, 2 green, 3 rainbow
    public int Trail;        // 0 none, 1 rainbow, 2 smoke, 3 glitter
    public bool Lightbar;
    public int Horn;         // 0 beep, 1 fanfare, 2 cucaracha, 3 train
    public int Weapon;       // 0 none, 1 saw, 2 hammer, 3 flipper, 4 spinner, 5 wedge
    public bool Armor, Spikes;

    public static Look Of(Settings s) => new()
    {
        Skin = (RobotSkin)s.Skin, Hat = s.HatKind != 0 ? s.HatKind : (s.Hat ? 1 : 0),
        Spoiler = s.Spoiler, Glow = s.Glow, Exhaust = s.Exhaust, Decal = s.Decal, Pet = (PetKind)s.Pet,
        GlowColor = s.GlowColor, Trail = s.Trail, Lightbar = s.Lightbar, Horn = s.Horn, Weapon = s.Weapon, Armor = s.Armor, Spikes = s.Spikes,
    };
}

public sealed class ShopItem
{
    public string Id, Name, Emoji, Blurb;
    public ShopCategory Category;
    public int Price;
    public Func<Look, Look> Wear;           // puts it on
    public Func<Look, bool> Worn;           // is it on
    public Func<Look, Look> TakeOff;        // null when it can't be taken off (a skin)
}

/// The shop: cosmetics for coins. Coins come from cleaning up, and from coins lying on the desktop.
public static class Shop
{
    public static readonly List<ShopItem> Items = Build();

    static List<ShopItem> Build()
    {
        var list = new List<ShopItem>();
        void Skin(RobotSkin s, string name, string emoji, int price, string blurb) => list.Add(new ShopItem
        {
            Id = "skin:" + (int)s, Name = name, Emoji = emoji, Price = price, Blurb = blurb, Category = ShopCategory.Skins,
            Wear = l => { l.Skin = s; return l; }, Worn = l => l.Skin == s,
        });
        Skin(RobotSkin.White, "Weiß", "⚪", 0, "Das Original.");
        Skin(RobotSkin.Black, "Schwarz", "⚫", 0, "Glänzend schwarz.");
        Skin(RobotSkin.Gold, "Gold", "🟡", 0, "Poliertes Gold.");
        Skin(RobotSkin.Retro, "Retro", "📼", 0, "Beige wie ein Computer von 1987.");
        Skin(RobotSkin.Pink, "Pink", "🌸", 0, "Pink.");
        Skin(RobotSkin.Squirrel, "Eichhörnchen", "🐿", 0, "Rotbraun mit Pinselohren. Für die Bande.");
        Skin(RobotSkin.Carbon, "Carbon", "🏁", 150, "Echtes Kohlefaser-Geflecht. Spart kein Gewicht.");
        Skin(RobotSkin.Camo, "Tarnmuster", "🪖", 120, "Auf dem Desktop praktisch unsichtbar.");
        Skin(RobotSkin.Neon, "Neon", "💡", 250, "Schwarz mit leuchtendem Rand.");
        Skin(RobotSkin.Chrome, "Chrom", "🪞", 300, "Spiegelt das ganze Zimmer.");
        Skin(RobotSkin.Galaxy, "Galaxie", "🌌", 400, "Ein Nebel und funkelnde Sterne.");
        Skin(RobotSkin.Wood, "Holz", "🪵", 100, "Warmes Nussbaum-Furnier.");
        Skin(RobotSkin.Zebra, "Zebra", "🦓", 140, "Kein Streifen wie der andere.");
        Skin(RobotSkin.Leopard, "Leopard", "🐆", 160, "Fleckig und flink.");
        Skin(RobotSkin.Police, "Polizei", "🚓", 180, "Blau-weiß. Am besten mit Blaulicht.");
        Skin(RobotSkin.Marble, "Marmor", "🏛", 220, "Poliert wie im Museum.");
        Skin(RobotSkin.Ice, "Eis", "🧊", 260, "Frostig, mit funkelnden Kristallen.");
        Skin(RobotSkin.Rainbow, "Regenbogen", "🌈", 350, "Das ganze Farbrad, das sich langsam dreht.");
        Skin(RobotSkin.Lava, "Lava", "🌋", 450, "Dunkle Kruste, glühende Risse.");

        void Hat(int kind, string name, string emoji, int price, string blurb) => list.Add(new ShopItem
        {
            Id = "hat:" + kind, Name = name, Emoji = emoji, Price = price, Blurb = blurb, Category = ShopCategory.Hats,
            Wear = l => { l.Hat = kind; return l; }, Worn = l => l.Hat == kind, TakeOff = l => { l.Hat = 0; return l; },
        });
        Hat(1, "Partyhut", "🥳", 0, "Gestreift, mit Bommel.");
        Hat(5, "Weihnachtsmütze", "🎅", 100, "Mit Bommel, der zur Seite hängt.");
        Hat(3, "Zylinder", "🎩", 150, "Für feine Aufräumarbeiten.");
        Hat(4, "Cowboyhut", "🤠", 200, "Yeehaw.");
        Hat(6, "Propellermütze", "🧢", 250, "Der Propeller dreht sich wirklich.");
        Hat(2, "Krone", "👑", 300, "Gold, mit Edelsteinen.");
        Hat(8, "Kochmütze", "👨‍🍳", 90, "Für den Desktop-Eintopf.");
        Hat(10, "Bauhelm", "⛑", 110, "Sicherheit geht vor.");
        Hat(12, "Blumenkranz", "🌸", 160, "Neun kleine Blüten im Kreis.");
        Hat(7, "Wikingerhelm", "🪓", 280, "Mit Hörnern. Historisch ungenau.");
        Hat(9, "Zauberhut", "🧙", 320, "Mit Sternen, die funkeln, und einem Mond.");
        Hat(11, "Heiligenschein", "😇", 500, "Schwebt und leuchtet. Für sehr brave Roboter.");

        void Tune(string id, string name, string emoji, int price, string blurb, Func<Look, Look> wear, Func<Look, bool> worn, Func<Look, Look> off) => list.Add(new ShopItem
        {
            Id = "tune:" + id, Name = name, Emoji = emoji, Price = price, Blurb = blurb, Category = ShopCategory.Tuning, Wear = wear, Worn = worn, TakeOff = off,
        });
        Tune("stripes", "Rennstreifen", "🏎", 80, "Zwei weiße Streifen. Macht schneller. Gefühlt.",
            l => { l.Decal = 1; return l; }, l => l.Decal == 1, l => { l.Decal = 0; return l; });
        Tune("flames", "Flammen-Lackierung", "🔥", 120, "Flammen, die vom Stoßfänger nach hinten lecken.",
            l => { l.Decal = 2; return l; }, l => l.Decal == 2, l => { l.Decal = 0; return l; });
        Tune("spoiler", "Heckspoiler", "🪽", 150, "Roter Flügel am Heck, für mehr Anpressdruck.",
            l => { l.Spoiler = true; return l; }, l => l.Spoiler, l => { l.Spoiler = false; return l; });
        Tune("glow", "Unterboden Blau", "🟦", 200, "Leuchtet blau auf den Boden.",
            l => { l.Glow = true; l.GlowColor = 0; return l; }, l => l.Glow && l.GlowColor == 0, l => { l.Glow = false; return l; });
        Tune("exhaust", "Auspuff-Flammen", "💨", 250, "Spuckt Flammen, wenn er Gas gibt.",
            l => { l.Exhaust = true; return l; }, l => l.Exhaust, l => { l.Exhaust = false; return l; });

        // Underglow in more colours: each is its own item, and they replace each other.
        foreach (var (c, name, emoji, price) in new[] { (1, "Unterboden Pink", "🩷", 220), (2, "Unterboden Grün", "💚", 220), (3, "Unterboden Regenbogen", "🌈", 380) })
            Tune("glow" + c, name, emoji, price, "Leuchtet auf den Boden.",
                l => { l.Glow = true; l.GlowColor = c; return l; }, l => l.Glow && l.GlowColor == c, l => { l.Glow = false; l.GlowColor = 0; return l; });
        // Trails it leaves behind as it drives.
        foreach (var (t, name, emoji, price, blurb) in new[] { (2, "Rauchspur", "💨", 140, "Eine Spur aus weißem Rauch."), (3, "Glitzerspur", "✨", 200, "Glitzert, wo er langfährt."), (1, "Regenbogenspur", "🌈", 300, "Ein Regenbogen hinter ihm her.") })
            Tune("trail" + t, name, emoji, price, blurb, l => { l.Trail = t; return l; }, l => l.Trail == t, l => { l.Trail = 0; return l; });
        Tune("lightbar", "Blaulicht", "🚨", 260, "Rot und blau, abwechselnd blinkend.",
            l => { l.Lightbar = true; return l; }, l => l.Lightbar, l => { l.Lightbar = false; return l; });
        // Horns: what it plays when it bumps into something.
        foreach (var (h, name, emoji, price, blurb) in new[] { (1, "Hupe: Fanfare", "🎺", 120, "Tätärätä, wenn er wo anstößt."), (3, "Hupe: Zug", "🚂", 150, "Tuut tuut."), (2, "Hupe: La Cucaracha", "🎵", 220, "Die Melodie, die jeder kennt.") })
            Tune("horn" + h, name, emoji, price, blurb, l => { l.Horn = h; return l; }, l => l.Horn == h, l => { l.Horn = 0; return l; });

        void Pet(PetKind p, string name, string emoji, int price, string blurb) => list.Add(new ShopItem
        {
            Id = "pet:" + (int)p, Name = name, Emoji = emoji, Price = price, Blurb = blurb, Category = ShopCategory.Pets,
            Wear = l => { l.Pet = p; return l; }, Worn = l => l.Pet == p, TakeOff = l => { l.Pet = PetKind.None; return l; },
        });
        Pet(PetKind.Squirrel, "Eichhörnchen", "🐿", 250, "Flitzt hinterher und vergisst nie, wo der Roboter ist.");
        Pet(PetKind.Dachshund, "Dackel", "🌭", 300, "Lang, treu, bellt gern.");
        Pet(PetKind.Cat, "Kater", "🐈", 300, "Rot getigert. Läuft mit, wenn er Lust hat.");
        Pet(PetKind.Golden, "Golden Retriever", "🦮", 350, "Freut sich über jede Datei.");
        Pet(PetKind.Duck, "Entenküken", "🐤", 200, "Watschelt treu hinterher.");
        Pet(PetKind.Bunny, "Hase", "🐰", 280, "Hoppelt mit langen Ohren.");
        Pet(PetKind.Fox, "Fuchs", "🦊", 380, "Schlau, mit weißer Schwanzspitze.");
        Pet(PetKind.Penguin, "Pinguin", "🐧", 420, "Watschelt im Frack.");

        // Fighting parts for the duel. One weapon at a time; armour and spikes go with any of them.
        void Fight(string id, string name, string emoji, int price, string blurb, Func<Look, Look> wear, Func<Look, bool> worn, Func<Look, Look> off) => list.Add(new ShopItem
        {
            Id = "fight:" + id, Name = name, Emoji = emoji, Price = price, Blurb = blurb, Category = ShopCategory.Battle, Wear = wear, Worn = worn, TakeOff = off,
        });
        foreach (var (w, name, emoji, price, blurb) in new[]
        {
            (5, "Rammkeil", "🔺", 0, "Ein Stahlkeil vorne. Schiebt Gegner weg und hebelt sie kurz aus."),
            (1, "Kreissäge", "🪚", 200, "Sägt Funken aus jedem, der ihm vor den Stoßfänger kommt."),
            (2, "Hammer", "🔨", 260, "Holt aus und haut drauf. Wer getroffen wird, ist kurz benommen."),
            (3, "Flipper", "🤸", 300, "Eine Klappe, die Gegner durch die Luft schleudert."),
            (4, "Drehklinge", "🌀", 420, "Eine Stahlklinge auf dem Deckel, die alles wegschleudert, was ihr zu nahe kommt."),
        })
            Fight("weapon" + w, name, emoji, price, blurb, l => { l.Weapon = w; return l; }, l => l.Weapon == w, l => { l.Weapon = 0; return l; });
        Fight("armor", "Panzerung", "🛡", 180, "Genietete Stahlplatten rundum. Halbiert fast jeden Treffer.",
            l => { l.Armor = true; return l; }, l => l.Armor, l => { l.Armor = false; return l; });
        Fight("spikes", "Stacheln", "🦔", 150, "Stahlstacheln rundum. Wer ihn rammt, tut sich selbst weh.",
            l => { l.Spikes = true; return l; }, l => l.Spikes, l => { l.Spikes = false; return l; });
        return list;
    }

    public static bool Owns(Settings s, ShopItem it) => it.Price == 0 || s.Owned.Contains(it.Id);

    /// Spends the coins and puts it on. False if there aren't enough.
    public static bool Buy(Settings s, ShopItem it)
    {
        if (Owns(s, it)) { Wear(s, it); return true; }
        if (s.Coins < it.Price) return false;
        s.Coins -= it.Price;
        s.Owned.Add(it.Id);
        Wear(s, it);
        return true;
    }

    public static void Wear(Settings s, ShopItem it) => Save(s, it.Wear(Look.Of(s)));
    public static void TakeOff(Settings s, ShopItem it) { if (it.TakeOff != null) Save(s, it.TakeOff(Look.Of(s))); }

    static void Save(Settings s, Look l)
    {
        s.Skin = (int)l.Skin; s.HatKind = l.Hat; s.Hat = l.Hat == 1;
        s.Spoiler = l.Spoiler; s.Glow = l.Glow; s.Exhaust = l.Exhaust; s.Decal = l.Decal; s.Pet = (int)l.Pet;
        s.GlowColor = l.GlowColor; s.Trail = l.Trail; s.Lightbar = l.Lightbar; s.Horn = l.Horn; s.Weapon = l.Weapon; s.Armor = l.Armor; s.Spikes = l.Spikes;
    }
}
