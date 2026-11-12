# RoboDust für Windows

Die Windows-Version von [RoboDust](../README.md), dem Saugroboter für deinen Desktop. RoboDust stammt von [Raufan](https://muhraufan.com) (MIT-Lizenz), und diese Version ist ein Port seiner Mac-App: dieselbe Simulation, dieselben Shader, derselbe Roboter.

## Der wichtigste Unterschied: Sie löscht nie etwas

Die Mac-Version legt am Ende alles in den Papierkorb oder löscht es endgültig. Die Windows-Version macht keins von beidem. Du hast drei Möglichkeiten:

- **Ordnen**: sortiert die Symbole nach Art in Spalten. Nur die Symbole bewegen sich, die Dateien bleiben, wo sie sind.
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

# Ein echter Durchgang mit einem Wegwerf-Ordner als Desktop. 1 ist Wegräumen, 2 Ausblenden.
# Die Ablage landet daneben in C:\Temp\shelf, nicht in deiner echten.
RoboDust.exe --auto --no-hide --test-root C:\Temp\robodust-test\desk --test-mode 1

# Rendert einen Durchgang mit dem Beispiel-Chaos unsichtbar und speichert Einzelbilder als PNG.
RoboDust.exe --dump 4,8,12 --out C:\Temp\frames --plain-floor

# Liest deinen echten Desktop nur aus und gibt Anzahlen und Geometrie aus, keine Namen.
RoboDust.exe --read-test
```

## Was (noch) fehlt

Die Katze aus der Mac-Version und die automatischen Updates gibt es auf Windows noch nicht. RoboDust nutzt den Hauptbildschirm. Symbole auf weiteren Bildschirmen bleiben, wo sie sind.
