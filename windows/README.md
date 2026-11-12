# RoboDust für Windows

Die Windows-Version von [RoboDust](../README.md), dem Saugroboter für deinen Desktop. RoboDust stammt von [Raufan](https://muhraufan.com) (MIT-Lizenz). Die Windows-Version wurde von Alessio Brendt entwickelt und ist ein Port seiner Mac-App: dieselbe Simulation, dieselben Shader, derselbe Roboter.

## Der wichtigste Unterschied: Sie löscht nie etwas

Die Mac-Version legt am Ende alles in den Papierkorb oder löscht es endgültig. Die Windows-Version macht keins von beidem. Du hast drei Möglichkeiten:

- **Ordnen**: sortiert die Symbole nach Art in Spalten. Nur die Symbole bewegen sich, die Dateien bleiben, wo sie sind.
- **In Ordner**: sortiert alles in Ordner auf dem Desktop, wahlweise nach Art („Bilder“, „Dokumente“, „Videos“ …), nach Alphabet („A–E“, „F–J“ …) oder nach Monat der letzten Änderung („2026-10 Oktober“). Gibt es so einen Ordner schon, kommt alles dazu. Beim Rückgängigmachen verschwinden nur die Ordner wieder, die RoboDust selbst angelegt hat, und auch die nur, wenn sie leer sind.
- **Wegräumen**: verschiebt alles in einen eigenen Ordner unter `C:\Users\<du>\RoboDust Ablage\<Datum Uhrzeit>`. Dort liegt auch eine kleine `_RoboDust-Info.txt`, die festhält, was woher kam.
- **Ausblenden**: setzt bei den Dateien das Attribut „Versteckt“. Sie bleiben genau, wo sie sind.

Alles davon lässt sich mit einem Klick auf **Rückgängig** bzw. **Zurückholen** umkehren, auch nach einem Neustart von RoboDust. Jeder Durchgang wird dafür unter `%LOCALAPPDATA%\RoboDust\runs` festgehalten, und alles, was RoboDust mit deinen Dateien gemacht hat, steht in `%LOCALAPPDATA%\RoboDust\history.log`.

Liegen gelassen wird:

- alles, was in den letzten 10 Minuten geändert wurde
- schreibgeschützte Dateien und Systemdateien
- Ordner mit einem `.git` darin
- Programme (`.exe`)
- alles, was nicht direkt in deinem Desktop-Ordner liegt (zum Beispiel Verknüpfungen aus dem öffentlichen Desktop, Papierkorb, Dieser PC)
- beim Wegräumen: OneDrive-Dateien, die nur in der Cloud liegen

Kurz vor jeder Änderung wird jede Datei noch einmal geprüft. Hat sich eine während des Saugens verändert, fliegt sie einfach zurück an ihren Platz.

## Wie es funktioniert

Windows lässt keine App die Explorer-Symbole verbiegen oder in einen Staubsauger ziehen. RoboDust schummelt also wie auf dem Mac: Es fragt Explorer, wo deine Symbole liegen, holt sich dein Hintergrundbild und zeichnet eine Kopie des Desktops in ein Fenster direkt über den echten Symbolen und unter allen anderen Fenstern. Alles, was du danach siehst, ist diese Kopie, gezeichnet mit OpenGL. Die Metal-Shader der Mac-Version sind dafür Zeile für Zeile nach GLSL übersetzt.

Die einzige Datei, die etwas auf der Festplatte ändert, ist [`Source/FileOps.cs`](Source/FileOps.cs).

## Starten und bauen

Du brauchst Windows 10 oder 11 (64 Bit) und eine Grafikkarte mit OpenGL 3.3, also praktisch jede.

Zum Bauen brauchst du das [.NET 10 SDK](https://dotnet.microsoft.com/download):

```
cd windows
.\build.ps1
.\dist\RoboDust.exe
```

`build.ps1` baut eine einzelne `RoboDust.exe`, die ohne installiertes .NET läuft (etwa 66 MB). Mit `.\build.ps1 -Small` wird sie viel kleiner, braucht dann aber die .NET 10 Desktop Runtime.

Fang mit dem Beispiel-Chaos an. Das ist erfunden und hat keine Dateien dahinter.

## Zum Testen, ohne deinen echten Desktop anzufassen

```
# Prüft Wegräumen, Ausblenden und Rückgängig an Wegwerf-Dateien im angegebenen Ordner.
RoboDust.exe --fileops-test C:\Temp\robodust-test

# Ein echter Durchgang mit einem Wegwerf-Ordner als Desktop. 1 ist Wegräumen, 2 Ausblenden, 3 In Ordner.
# Die Ablage landet daneben in C:\Temp\shelf, nicht in deiner echten.
RoboDust.exe --auto --no-hide --test-root C:\Temp\robodust-test\desk --test-mode 1

# Rendert einen Durchgang mit dem Beispiel-Chaos unsichtbar und speichert Einzelbilder als PNG.
# Mit --all-screens über alle Bildschirme, als ein Bild der ganzen Wohnung.
RoboDust.exe --dump 4,8,12 --out C:\Temp\frames --plain-floor

# Liest deinen echten Desktop nur aus und gibt Anzahlen und Geometrie aus, keine Namen.
RoboDust.exe --read-test
```

## Spaßmodus

In den Einstellungen gibt es einen Spaßmodus: einen Flammenwerfer, der alles im Weg ankokelt, bevor der Roboter es einsaugt, eine Seifenblasen-Pistole, Konfetti für jedes Symbol und eine Konfetti-Kanone an der Station am Ende, Disco-Lichter, oder alles auf einmal. Mit Feuer verschwinden die Symbole nicht einfach im Roboter: Sie verkohlen, zerfallen mit glühendem Rand zu Asche, und der Roboter saugt die Asche auf. Das ist reine Show: Deinen Dateien passiert dadurch nichts, und ein angekokeltes Symbol sieht wieder ganz normal aus, sobald es zurück an seinem Platz ist.

## Einstellungen

Hinter dem Knopf „Einstellungen“ findest du:

- **Station steht auf**: auf welchem Monitor der Roboter startet und entleert.
- **Spaßmodus**: Feuer, Blasen, Konfetti, Disco, Laser (zerlegt alles in Pixel), Tornado, Schnee (der Schneepflug schiebt erst alles zu einem Haufen), Rennen (zwei Rivalen saugen um die Wette) oder alles auf einmal. Dazu **Fernsteuerung**: Mit den Pfeiltasten fährst du selbst, solange RoboDust im Vordergrund ist.
- **Eichhörnchen-Bande** (im Spaßmodus): Fünf Eichhörnchen schnappen sich Dateien wie Nüsse, vergraben sie und vergessen, wo. RoboDust gräbt alles wieder aus. Dazu gibt es den Roboter im Eichhörnchen-Look mit Ohren.
- **Katze**: Eine von sechs Katzen kommt vorbei. Sie beaufsichtigt, schläft auf dem Roboter, fährt mit, kämpft, schubst Dateien weg oder versteckt sich unter deinen Fenstern. Du kannst sie hochheben.
- **Aussehen**: Weiß, Schwarz, Gold, Retro oder Pink, mit oder ohne Partyhut.
- **Regeln**: nur aufräumen, was seit 1, 7 oder 30 Tagen unverändert ist. Mit Rechtsklick in „Elemente prüfen“ merkst du dir eine Datei als „nie anfassen“.
- **Zeitplan**: täglich oder freitags zur gewählten Uhrzeit, über die Windows-Aufgabenplanung. Danach kommt eine Benachrichtigung; ein Klick darauf führt zum Rückgängig-Knopf.
- **Start**: mit Windows starten (im Infobereich) und Strg+Alt+R für einen sofortigen Durchgang.

Auf der Startseite steht, was RoboDust bisher geschafft hat, und „Ablage“ zeigt, was dort liegt, mit Größe und Alter. Gelöscht wird dort nichts: Das machst du selbst im Explorer.

## Shop

Für jede aufgeräumte Datei gibt es eine Münze, und beim Aufräumen liegen Münzen auf dem Desktop (fünf pro Stück), die der Roboter mit einsaugt. Im **Shop** gibt es dafür Skins (Carbon, Chrom, Tarnmuster, Neon, Galaxie), Hüte (Krone, Zylinder, Cowboyhut, Weihnachtsmütze, Propellermütze), Tuning (Heckspoiler, Rennstreifen, Flammen-Lackierung, Neon-Unterboden, Auspuff-Flammen) und Haustiere, die hinter dem Roboter herlaufen (Dackel, Golden Retriever, Kater, Eichhörnchen). Oben im Shop dreht sich der Roboter in einer Live-Vorschau; ein Klick auf ein Teil probiert es an, ohne es zu kaufen.

Münzen geben (oder mit Minus nehmen), auch während RoboDust läuft:

```
RoboDust.exe --coins 500
```

## Mehrere Bildschirme

Ein Roboter fährt über alle Bildschirme. RoboDust legt die Monitore so nebeneinander, wie sie bei dir stehen, und behandelt jeden als Raum einer Wohnung. Wo sich zwei Bildschirme berühren, ist eine Tür, durch die der Roboter hinüberfährt. Die Station steht auf dem Hauptbildschirm, oder auf dem Monitor, den du unter „Station steht auf“ auswählst. Dort startet der Roboter, und dorthin fährt er zum Entleeren. Monitore mit unterschiedlicher Skalierung sind kein Problem, und Win+D bringt RoboDust nicht aus dem Tritt.

## Was (noch) fehlt

Die automatischen Updates gibt es auf Windows noch nicht.
