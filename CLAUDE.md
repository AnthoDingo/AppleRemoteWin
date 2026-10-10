# CLAUDE.md

Application WPF + pilote user-mode Windows 11 pour la **Siri Remote gen 1** (Apple TV, A1513/A1962).
Port C# de [a-funk/open-appleremote](https://github.com/a-funk/open-appleremote) (MIT, Python/Linux) : `Decoder.cs` et `Gestures.cs`
sont des ports de `decode.py` et `gestures.py`. Protocole de référence : `docs/PROTOCOL.md` du dépôt amont.

## Structure

```
src/AppleRemote.Core      décodeur, gestes (One Euro), keymap, rejeu de captures,  net10.0, portable, AUCUNE dépendance Windows
                          pilote Bluetooth direct : Hci.cs (HCI), Ble.cs (central LE, L2CAP), Gatt.cs (client ATT),
                          Smp.cs (appairage « Just Works »), SiriRemoteBle.cs (connexion, chiffrement/appairage, HID)
src/AppleRemote.Windows   HID (SetupAPI + hid.dll), déverrouillage, lecture, SendInput, scanner BLE,   net10.0-windows10.0.19041.0
                          transport HCI sur WinUSB (UsbHci.cs, n'importe quel adaptateur sous pilote WinUSB)
src/AppleRemote.App       fenêtre WPF (projet de démarrage)                        net10.0-windows10.0.19041.0
                          WPF-UI + MVVM : MainWindow = menu NavigationView ; pages Télécommande, Settings, Debug
                          (pied de menu) partageant MainViewModel ; styles communs dans Views/Styles.xaml
tests/AppleRemote.Tests   xunit, tournent sur tout OS (sample.jsonl + golden.json)
tools/siri_bumble.py      sonde Python (Bumble) : télécommande via un adaptateur sous WinUSB, hors pile Bluetooth Windows
tools/test_siri_bumble.py son test sans matériel (fausse télécommande sur contrôleurs Bumble virtuels)
```

Dépendances : `App` -> `Windows` -> `Core`. Ne jamais faire dépendre `Core` de Windows : il doit rester testable partout.

## Commandes

```
dotnet build AppleRemote.sln
dotnet test tests/AppleRemote.Tests
dotnet run --project src/AppleRemote.App
```

Hors Windows : ajouter `-p:EnableWindowsTargeting=true` pour compiler (pas exécuter) `Windows` et `App`.
SDK requis : **.NET 10**. Lancer les tests après toute modification de `Core`.

## Protocole (à ne pas casser)

- Rapports d'entrée `0xFC`, rapport de fonctionnalité `0xF0`. La télécommande reste **muette** tant qu'on n'a pas envoyé `0xF0 0xAF`
  (`HidEnumerator.Unlock`). À refaire à chaque connexion.
- VID `0x004C` (Apple, Bluetooth SIG) / PID `0x026D`. Elle diffuse son **numéro de série** comme nom.
- Sous Windows, `ReadFile` rend toujours `InputReportByteLength` octets (rembourrés de zéros) : le décodeur ne lit que ce que l'octet 1 annonce.
- `HidD_SetFeature` exige un tampon de `FeatureReportByteLength` octets (repli : 2 octets nus).
- Siri (voix) non décodée via HID.

## Conventions

- Interface, messages et commentaires en **français**. Identifiants de code en anglais.
- `Nullable` activé, `ImplicitUsings` activé. Dans `App` (WPF), `System.IO` n'est **pas** importé implicitement : ajouter `using System.IO;`.
- Le TFM doit rester `net10.0-windows10.0.19041.0` (un `SupportedOSPlatformVersion` séparé provoque NETSDK1135).
- `Core` : toute logique nouvelle = un test. Le test `Whole_pipeline_matches_the_python_reference` compare à `golden.json`
  (sortie du code Python d'origine sur `sample.jsonl`) : ne pas régénérer `golden.json` pour faire passer un test sans comprendre l'écart.
- Événements du pilote levés depuis des threads du pool : marshaler vers l'UI avec `Dispatcher`.

## Fonctionnalités de diagnostic

- **Périphériques HID Apple** : liste ce que Windows voit (`HidEnumerator.ListApple`).
- **Scanner le Bluetooth LE** (`BleScanner`, `BleScanWindow`) : écoute les publicités LE sans appairage, met en évidence
  Apple (0x004C), le service HID (0x1812) et les noms en forme de n° de série. Sert à savoir si la télécommande émet.
  Double-clic / « Appairer » : appairage sans les Paramètres (`BleScanner.PairAsync`, `DeviceInformationCustomPairing`).

## État connu

- **La pile Bluetooth de Windows ne garde pas la télécommande** (constaté le 2026-10-09, Windows 11 build 26300,
  adaptateur Qualcomm QCA61x4A, trace HCI à l'appui) : l'appairage réussit, puis Windows lit la caractéristique Apple
  `ce5ad0bc…` (handle `0x003A`, service `8341f2b4…`) ; la télécommande ne répond jamais, Windows coupe au bout de 30 s
  (délai ATT), aucune collection HID n'est créée et la télécommande ne se reconnecte plus. Aucune API ne permet de
  l'éviter : la couche HID (`hid.dll`) n'est donc pas validée sur matériel. Contournement : adaptateur sous WinUSB + Bumble
  (autre piste : filtre noyau type [Jack-R1/SiriRemoteDriver](https://github.com/Jack-R1/SiriRemoteDriver)).
- **`tools/siri_bumble.py` validé sur matériel** (2026-10-10, QCA61x4A `0CF3:E007` passé sous WinUSB avec Zadig, firmware
  ROM, redémarrage du PC nécessaire) : appairage, reconnexion chiffrée avec les clés enregistrées après veille,
  déverrouillage, boutons, pavé et voix ; la capture rejouée dans `Core` donne les bons événements. Ce firmware refuse
  les paquets LE de 251 octets (Bumble s'arrête au démarrage) : la sonde demande 27 octets.
- **Pilote C# (`UsbHci` + `SiriRemoteBle`) validé sur matériel** (2026-10-10) : appairage « Just Works » (clés dans un
  `BondStore` JSON), connexion, chiffrement, découverte HID, déverrouillage, tous les boutons et gestes décodés par
  `RemotePipeline`, reconnexion chiffrée après veille. Juste après un appairage, la télécommande coupe une ou deux fois
  (raison 0x13) : la boucle se reconnecte seule. Ne réappairer que sur refus de clé (0x05/0x06), pas sur 0x3E.
- **Dans l'app** : `RemoteSession.Start` prend le Bluetooth direct si `UsbHci.Open()` réussit (premier adaptateur Bluetooth USB
  sous WinUSB, détecté par sa classe `E0/01/01`, tout VID/PID), sinon le HID de Windows. Clés dans `%AppData%\AppleRemoteonds.json` ; sans clés, balayage d'une
  télécommande en mode appairage (service 0x1812 + données Apple 0x004C). « Appairer » oublie les clés et relance.
  Un seul adaptateur géré (le premier trouvé ; `WinUsbDriver.Find` pour l'installation/restauration du pilote). Le firmware n'est pas chargé : la UB500 (Realtek) demanderait en plus le chargement de son firmware.
  `FakeRemote` (tests) reproduit la base GATT de la vraie (handles `0x1D` F0, `0x20` F1, `0x23`/`0x24` FC).
- Rapports reçus en GATT : l'ID (`0xFC`) n'est pas dans la notification ; on le remet en tête pour rester au format `hid.dll`.
- Les fenêtres lancées en administrateur ignorent `SendInput` d'un programme non élevé (UIPI).
- Une seule télécommande à la fois ; Siri Remote gen 2/3 non prises en charge (autres rapports, autre déverrouillage).
- Constantes de gestes (`Gestures.cs`) réglées sous Linux : à réajuster sur matériel.
