# Siri Remote (gen 1) pour Windows 11

*[English version](README.md)*

Application **WPF (.NET 10)** + pilote user-mode pour utiliser la Siri Remote gen 1 (Apple TV 2015/2017, A1513/A1962)
sur Windows 11 : surface tactile, boutons, gestes façon tvOS et clavier virtuel.

Port C# de [a-funk/open-appleremote](https://github.com/a-funk/open-appleremote) (MIT, Python/Linux). Le décodeur et le
moteur de gestes sont des ports ligne à ligne de `decode.py` et `gestures.py` ; `docs/PROTOCOL.md` amont décrit le protocole.

## « Driver » : ce qui est fourni

Pas de pilote noyau. Windows 11 apporte déjà le pilote HID over GATT (`HidBthLE`). Comme la télécommande n'expose que des
reports constructeur (entrée `0xFC`, fonctionnalité `0xF0`), elle reste muette tant qu'on ne la déverrouille pas :

| Linux (open-appleremote) | Windows (ici) |
|---|---|
| `/dev/hidrawN`, `HIDIOCSFEATURE` | `SetupAPI` + `hid.dll` : `HidD_SetFeature`, lecture `ReadFile` asynchrone |
| Déverrouillage : report `0xF0` = `0xAF` | idem, tampon rembourré à `FeatureReportByteLength` (exigé par Windows) |
| Reconnexion après veille | boucle de reconnexion + sonde de liaison (déverrouillage si silence > 8 s) |
| `/dev/uinput` (clavier virtuel) | `SendInput` |

Un clavier HID virtuel « vrai » (VHF/KMDF) demanderait un pilote signé ; `SendInput` suffit pour piloter une application.

## Structure

```
src/AppleRemote.Core      décodeur, gestes (One Euro), keymap, rejeu de captures   (net10.0, portable)
src/AppleRemote.Windows   énumération HID, déverrouillage, lecture, SendInput      (net10.0-windows)
src/AppleRemote.App       fenêtre WPF                                              (net10.0-windows)
tests/AppleRemote.Tests   xunit, tournent sur n'importe quel OS (macOS inclus)
```

## Utilisation

1. **Appairer** : maintenir **Menu + Volume +** ~5 s près du PC, puis Paramètres > Bluetooth & appareils > Ajouter
   (le bouton « Appairer » ouvre la page). Elle apparaît sous son numéro de série.
2. Lancer l'application : elle cherche la télécommande, la déverrouille et affiche touches, doigts et gestes.
3. Cocher **Clavier virtuel** pour envoyer de vraies touches (mapping par défaut ci-dessous).

| Télécommande | Touche |
|---|---|
| glisser / flick haut, bas, gauche, droite | flèches |
| clic (appui sur la surface) | Entrée |
| Menu | Échap |
| TV / Home | Navigateur : accueil |
| Lecture/Pause, Volume + / − | médias |
| Siri | rien |

Mapping personnalisé : `keymap.example.json` (entrées `up down left right click menu tv playpause volup voldown siri` ;
valeur = nom de touche, code VK 1-254, ou `null` pour désactiver ; noms : `Keys.cs`).

Les fenêtres lancées **en administrateur** ignorent les touches d'un programme non élevé (UIPI) : lancez alors l'app en administrateur.

## Build

```
dotnet build AppleRemote.sln                    # sous Windows (Visual Studio 2026 ou SDK .NET 10)
dotnet test tests/AppleRemote.Tests             # sur n'importe quel OS
dotnet publish src/AppleRemote.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

Depuis macOS/Linux : `-p:EnableWindowsTargeting=true` permet de compiler (pas d'exécuter) les projets Windows.

## Diagnostic

- **« Périphériques HID Apple »** liste ce que Windows voit (VID/PID, usages, longueurs de reports). La Siri Remote gen 1 est
  `VID 004C PID 026D`. Rien dans la liste = pas appairée/connectée.
- **Scanner le Bluetooth LE** écoute les publicités autour du PC (sans appairage) : une télécommande en mode appairage apparaît
  avec le fabricant Apple (0x004C) et son numéro de série comme nom, même si « Ajouter un appareil » ne l'affiche pas.
- **Enregistrer une capture** écrit un `.jsonl` au format de `monitor --raw` (rejouable ici via « Rejouer une capture »,
  ou dans l'outil Linux). Les trames audio de Siri sont tronquées : la voix n'est jamais écrite.
- Après une longue veille, le bouton qui réveille la télécommande peut être perdu : rappuyer.

## Tests

35 tests xunit. `SampleTests` rejoue `sample.jsonl` (vraie capture d'une A1962, issue du projet amont) dans toute la chaîne et
compare chaque événement (étape, doigts, boutons, gestes) à `golden.json`, produit par le code Python d'origine sur la même
capture : le port C# est donc vérifié contre la référence.

## Limites

- **Couche Windows non testée sur matériel** : elle compile, mais l'accès HID réel (collection vue par `HidBthLE`, déverrouillage
  par `HidD_SetFeature`) n'a pas été exécuté avec une vraie télécommande. Si le déverrouillage échoue, le journal l'indique ;
  dans ce cas, envoyer la sortie de « Périphériques HID Apple » et une capture.
- Voix (Siri) non décodée ; Siri Remote gen 2/3 non prises en charge (autres reports et autre déverrouillage) ; une seule télécommande.
- Constantes de gestes réglées pour une gen 1 sous Linux : à réaccorder au besoin (`Gestures.cs`).

## Licence

GNU AGPL v3 ou ultérieure (`AGPL-3.0-or-later`), voir `LICENSE`.

`Decoder.cs` et `Gestures.cs` sont des ports de [open-appleremote](https://github.com/a-funk/open-appleremote), publié sous licence MIT :
son avis de copyright est conservé dans `LICENSE-MIT-open-appleremote`.

Le pilote noyau de `src/AppleRemoteDriver` est un composant séparé sous licence MIT : voir son `LICENSE` et son `README.md`.
