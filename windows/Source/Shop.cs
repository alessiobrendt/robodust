using System;
using System.Collections.Generic;
using System.Linq;

namespace RoboDust;

public enum PetKind { None = 0, Dachshund = 1, Golden = 2, Cat = 3, Squirrel = 4 }
public enum ShopCategory { Skins, Hats, Tuning, Pets }

/// Everything the robot is wearing: what the renderer draws and the preview shows.
public struct Look
{
    public RobotSkin Skin;
    public int Hat;          // 0 none, 1 party, 2 crown, 3 top hat, 4 cowboy, 5 santa, 6 propeller
    public bool Spoiler, Glow, Exhaust;
    public int Decal;        // 0 none, 1 racing stripes, 2 flames
    public PetKind Pet;

    public static Look Of(Settings s) => new()
    {
        Skin = (RobotSkin)s.Skin, Hat = s.HatKind != 0 ? s.HatKind : (s.Hat ? 1 : 0),
        Spoiler = s.Spoiler, Glow = s.Glow, Exhaust = s.Exhaust, Decal = s.Decal, Pet = (PetKind)s.Pet,
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
        Tune("glow", "Neon-Unterboden", "🟦", 200, "Leuchtet blau auf den Boden.",
            l => { l.Glow = true; return l; }, l => l.Glow, l => { l.Glow = false; return l; });
        Tune("exhaust", "Auspuff-Flammen", "💨", 250, "Spuckt Flammen, wenn er Gas gibt.",
            l => { l.Exhaust = true; return l; }, l => l.Exhaust, l => { l.Exhaust = false; return l; });

        void Pet(PetKind p, string name, string emoji, int price, string blurb) => list.Add(new ShopItem
        {
            Id = "pet:" + (int)p, Name = name, Emoji = emoji, Price = price, Blurb = blurb, Category = ShopCategory.Pets,
            Wear = l => { l.Pet = p; return l; }, Worn = l => l.Pet == p, TakeOff = l => { l.Pet = PetKind.None; return l; },
        });
        Pet(PetKind.Squirrel, "Eichhörnchen", "🐿", 250, "Flitzt hinterher und vergisst nie, wo der Roboter ist.");
        Pet(PetKind.Dachshund, "Dackel", "🌭", 300, "Lang, treu, bellt gern.");
        Pet(PetKind.Cat, "Kater", "🐈", 300, "Rot getigert. Läuft mit, wenn er Lust hat.");
        Pet(PetKind.Golden, "Golden Retriever", "🦮", 350, "Freut sich über jede Datei.");
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
    }
}
