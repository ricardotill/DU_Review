# Handleiding voor de organisatie

Laatst bijgewerkt: 6 oktober 2026

Deze handleiding is voor wie DU Review opzet en op de speeldag beheert: de app starten, Slack koppelen, sleutels, instellingen, en wat te doen bij een storing. Hoe de jury beoordeelt en wat de pagina's van het dashboard doen, staat in de [handleiding voor de jury](handleiding-jury.md); wat groepen moeten weten in de [handleiding voor groepen](handleiding-groepen.md).

## Zo werkt het

Groepen plaatsen hun opdrachten in Slack, de jury beoordeelt ze daar met een emoji, en DU Review doet de rest: bestanden opslaan, punten tellen en een live scorebord tonen. Slack is altijd leidend: wat in Slack staat, telt.

1. Een scout plaatst een foto, video, geluidsopname of document in het Slack-kanaal van de eigen groep, met het opdrachtnummer in de tekst.
2. De app downloadt het bestand en slaat het op in de map van de groep, met een vaste naam zoals `CDW 12 14-32-05.jpg`.
3. Een jurylid reageert in Slack met een emoji: goedgekeurd, awesome of afgekeurd.
4. De app verplaatst het bestand naar de juiste map en werkt het scorebord direct bij.

Klopt er iets niet, bijvoorbeeld een ontbrekend opdrachtnummer, dan reageert de bot zelf in Slack met een emoji en een uitleg in de thread.

Het koppelen van Slack (de Slack-app, rechten, kanalen) staat in [Slack koppelen](slack-koppelen.md). Hoe de app gemiste berichten inhaalt en herbouwt, staat uitgebreid in [Slack-synchronisatie](slack-sync.md).

## Beheer in de app

Beheertaken doe je in beheermodus. Daarvoor heb je de aparte beheerderssleutel nodig; de gewone toegangssleutel is genoeg om te kijken.

### Beheermodus

- **Aan:** ga naar **Beheer** en vul de beheerderssleutel in. Inloggen met de beheerderssleutel op het loginscherm kan ook.
- **Uit:** klik op **Beheermodus verlaten**.
- Zolang beheermodus aan staat, staat **Beheer** bovenaan de pagina.

### Sleutels

- Bij de eerste start maakt de app een toegangssleutel en een beheerderssleutel aan. Ze staan eenmalig in het logboek en worden bewaard in `access-keys.json`.
- **Sleutel kwijt?** Bij een volgende start staan de sleutels niet meer in het logboek. Je leest ze dan uit `access-keys.json`, op de computer waar de app draait:

  | Gestart met | Commando |
  |---|---|
  | `run-gameday.sh` | `cat ~/du-app/access-keys.json` |
  | Docker Compose | `docker compose exec app cat /data/access-keys.json` |
  | `dotnet run` | `cat src/ScoutingAutomation.Web/access-keys.json` |

  Wie dit bestand kan lezen, heeft beheerderstoegang. Deel het dus nooit.
- Op de pagina **Beheer** maak je een nieuwe sleutel aan. Iedereen die met de oude sleutel is ingelogd, wordt direct uitgelogd. Jij blijft ingelogd.
- Een nieuwe beheerderssleutel wordt maar één keer getoond. Schrijf hem meteen op.
- **Groepssleutels**: op **Beheer** > **Groepssleutels** maak je in één keer sleutels voor alle groepen. Per groep kun je een nieuwe sleutel maken of de sleutel intrekken; dat logt alleen die groep uit.

### Juryleden

Op **Configuratie** voeg je juryleden toe met hun Slack member-ID, haal je ze weg en pas je hun namen aan. Alleen emoji's van juryleden op deze lijst tellen. Wijzigingen gelden direct, zonder herstart.

Een member-ID vind je in Slack via het profiel van iemand: **⋮** → **Copy member ID**. Het begint met `U`.

### Beoordelingsregels

Op **Configuratie**, onder **Beoordelingsregels** (alleen in beheermodus):

- **Nieuwe poging na goedkeuring weigeren.** Aan: een groep kan een opdracht die al goedgekeurd of awesome is niet opnieuw insturen. Uit: opnieuw insturen mag altijd, en de beste beoordeling telt. De standaard staat in `appsettings.json` (`Scouting:BlockAttemptsAfterApproval`).
- **Herinnering voor de jury.** Na hoeveel minuten wachten de bot de jury herinnert. Leeg betekent geen herinneringen. Het jurykanaal stel je in `appsettings.json` in (`Scouting:ReviewReminder:ChannelId`).

Wat je hier verandert, geldt direct en blijft bewaard na een herstart. Met **Terug naar appsettings** ga je terug naar de waarde uit `appsettings.json`.

### Status handmatig aanpassen

Gebruik dit alleen als Slack hapert en een beoordeling niet aankomt.

1. Open de inzending (detailpagina).
2. Kies onder **Status handmatig aanpassen** de juiste status.
3. Vul een reden in en bevestig.

De aanpassing geldt voor alle bestanden van hetzelfde bericht, net als een emoji in Slack. Staan er twee foto's in het bericht, dan krijgen ze dus allebei de nieuwe status, en veranderen de punten mee.

De aanpassing komt in de beoordelingsgeschiedenis. Slack blijft leidend: zet een jurylid later een emoji op het bericht, dan telt die emoji. Na **Opnieuw opbouwen uit Slack** verdwijnen handmatige aanpassingen. Bij het automatisch inhalen blijft een aanpassing staan zolang er in Slack geen beoordeling op het bericht staat.

### Extra punten en puntencategorieën

Op **Beheer** (alleen in beheermodus):

- **Extra punten**: vul per groep de punten per categorie in en klik op **Opslaan**. Ze tellen direct mee in de score en de stand.
- **Puntencategorieën**: voeg een categorie toe (bijvoorbeeld een fotowedstrijd), wijzig de naam met het potlood, of haal er een weg met de prullenbak. De sleutel wordt uit de naam gemaakt als je hem leeg laat, en blijft daarna altijd hetzelfde.

Haal je een categorie weg, dan tellen de punten ervan niet meer mee, maar ze blijven bewaard. Voeg je dezelfde sleutel later weer toe, dan zijn de punten terug. De standaardlijst staat in `appsettings.json` (`Scouting:BonusCategories`); **Terug naar appsettings** zet die terug.

### Avonturiers

Het Avonturiers-deel staat standaard uit. Zo zet je het op:

1. Maak per groep een tweede Slack-kanaal voor de Avonturiers en nodig de bot uit (`/invite @DU Review`).
2. Zet in `appsettings.json` bij elke groep `AdventurersSlackChannelId`, en zet `Scouting:Adventurers:Enabled` op `true`. Een kanaal mag maar bij één groep horen; anders start de app niet.
3. Zet `Scouting:PublicUrl` op het adres waarop de app van buiten bereikbaar is (bijvoorbeeld het adres van de [Cloudflare Tunnel](#via-internet-cloudflare-tunnel)). Zonder dat adres staat er "(de kaartlink volgt)" in plaats van de kaartlink.
4. Herstart de app. In het menu staat nu **Avonturiers**.

**Locaties** (**Avonturiers → Locaties**, beheermodus): klik op de kaart om een locatie te plaatsen, vul nummer, naam en eventueel een hint in, en sla op. Een hele lijst importeer je uit een KML-, KMZ-, GeoJSON-, GPX- of CSV-bestand (maximaal 5 MB). Het nummer komt vooraan de naam (`15 Opdracht`) of uit een kolom `nummer`.

*Vanuit Google My Maps*: noem elke locatie `{nummer} {naam}` (bijvoorbeeld `15 Opdracht`) en zet de uitleg in de beschrijving; die wordt de hint op de kaart. Exporteer de laag via de drie puntjes naast de laagnaam → **Gegevens exporteren** → **KML/KMZ**, en kies dat bestand bij **Importeren**. Lijnen, vlakken en punten zonder nummer worden overgeslagen; ze staan in de lijst onder de knop. Een CSV heeft de kolommen `nummer;naam;lat;lon;hint`; puntkomma's en decimale komma's van Nederlandse Excel mogen. Een import vult aan en past bestaande nummers aan; hij verwijdert niets. Wat niet gelezen kon worden, staat in een lijst onder de knop.

**Open locaties per groep** (**Beheer**): een groep heeft locatie 1 tot en met een nummer open. Laat het veld leeg voor het startaantal (`InitialOpenLocations`, standaard 10, ook aan te passen op **Configuratie → Avonturiers**). Opent het andere spelonderdeel een locatie voor een groep, verhoog dan haar nummer, of klik op **+1**. Alleen open locaties staan op de kaart van die groep. Een check-in op een locatie die nog niet open is, krijgt 🚧 en telt vanzelf zodra je het nummer verhoogt; de bot meldt de Toeristen dan de nieuwe opdrachten. Ernaast zie je per groep hoeveel check-ins zijn goedgekeurd, hoeveel daarvan tellen en hoeveel opdrachten open zijn.

**Opdrachten vrijspelen**: de eerste `InitialOpenAssignments` opdrachten (standaard 30) zijn altijd open. De rest gaat open naar rato van de getelde locaties: met 160 opdrachten en 100 locaties opent elke locatie er 1 of 2, in nummervolgorde, en na alle 100 locaties zijn alle 160 open. Een check-in telt pas vanaf het einde van de ronde waarin hij is geplaatst (vóór of tussen de rondes: het einde van de volgende ronde; na de laatste ronde of zonder rondes: meteen), zodat de opdrachten per ronde opengaan. Zet de locaties dus in de app voordat het spel begint: zonder locaties zijn alle opdrachten open. Een inzending voor een opdracht op slot krijgt 🔒 en telt niet. Op **Configuratie → Avonturiers** pas je de teksten van de bot aan.

**Supercoördinaten**: een goedgekeurde check-in op een supercoördinaat telt als twee locaties en opent dus twee keer zoveel opdrachten; alle locaties samen openen nog steeds alle opdrachten. Standaard staan ze aan op 10, 20, 30 … 100 (`SuperLocations` in appsettings). Op **Configuratie → Avonturiers** zet je ze aan of uit en vul je de nummers in, gescheiden door komma's; **Elke 10e** vult elke tiende locatie in tot de hoogste. Een wijziging telt meteen voor alle groepen, en de bot meldt de nieuwe opdrachten (of een correctie) in de Toeristen-kanalen. Pas ze dus liefst aan vóór het spel.

**Opdrachten en afbeeldingen**: op de pagina van een opdracht kan de jury met **Bewerken** de titel, beschrijving en punten aanpassen en afbeeldingen toevoegen. Wat in de app is aangepast, wint van `opdrachtenpunten.csv`, ook na een herstart, tot iemand op **Terug naar puntenbestand** klikt. Afbeeldingen staan in `scouting_storage/_opdrachten/`; neem die map mee in je back-up.

**Rondes** (**Avonturiers → Rondes**, beheermodus): vul begin en einde in en klik op **Genereer rondes** voor rondes van `RoundMinutes` minuten (standaard 30) met half-uur opdracht N in ronde N. Pas daarna per ronde de half-uur opdrachten of de openingstekst aan en klik op **Opslaan**. Rondes mogen niet overlappen en een half-uur opdracht hoort bij één ronde. Sluiten twee rondes op elkaar aan (zoals na **Genereer rondes**), dan plaatst de bot één overgangsbericht in plaats van de sluiting en de opening. Die tekst pas je aan onder **Overgang naar de volgende ronde**; maak je hem leeg, dan gaan sluiting en opening los. Onder **Berichten per ronde** zie je per ronde wat de bot al geplaatst heeft, en stuur je een bericht met de hand, met een foto of opnieuw naar alle groepen.

**Startinformatie en eindlocatie** (**Avonturiers → Rondes**): de tekst, het tijdstip waarop de bot hem zelf plaatst (vooraf in te stellen met `Scouting:Adventurers:StartAnnouncement:PostAt` en `FinalAnnouncement:PostAt`; een tijd die je in de app aanpast zet je met **Terug naar appsettings** terug). Die tijden zijn ook de start en het einde van de Avonturiers: berichten in hun kanalen tellen alleen daartussen, en groepen zien hun kaart pas vanaf de start. Zonder eigen tijd gelden de start- en eindtijd van het spel. Verder: of nu sturen met eventueel een foto (bijvoorbeeld een plattegrond). In de tekst: `{group}`, `{map}`, `{time}`, `{approved}` en `{rejected}`.

**Punten**: de punten van de half-uur opdrachten worden uitgerekend, met een maximum van `ThemeMaxPoints` (standaard 14). Dat maximum en dat van het eindspel pas je aan op **Configuratie → Avonturiers** onder **Maximum aantal punten**; punten erboven blijven bewaard en tellen weer mee als je het maximum verhoogt. Het **eindspel**, en een startbonus voor groepen met eigen vervoer, vul je met de hand in op **Beheer** onder **Extra punten**, in de kolom **Avonturiers: eindspel** (maximaal `FinalMaxPoints`, standaard 14). Onder elk veld staat het totaal dat telt. De categorie Avonturiers is vast zolang de Avonturiers aan staan: hij kan niet weg, alleen een andere naam krijgen. Het totaal staat ook op **Avonturiers → Overzicht**, op het dashboard en in de export.

**Herstel**: catch-up en herbouwen lezen ook de Avonturiers-kanalen. De bot plaatst daarbij geen berichten over vrijgespeelde opdrachten of rondes opnieuw. Zie [Slack-sync](slack-sync.md).

### Handmatige modus

Zet **Handmatige modus** aan met de schakelaar op het **Dashboard** als de organisatie alles zelf wil beoordelen. De app slaat dan alleen nog bestanden op met de juiste naam in de map van de groep. Er komen geen inzendingen op het dashboard, emoji's worden genegeerd en de bot zegt niets in Slack. Bovenaan elke pagina staat dan **Handmatige modus**. Na een herstart geldt weer de instelling `Scouting:ManualMode`.

### Automatisch inhalen

Slack stuurt elk bericht en elke emoji maar één keer. Draaide de app even niet, of was de verbinding weg, dan haalt hij zelf in wat hij heeft gemist: bij het opstarten en bij elke nieuwe verbinding met Slack.

- De app leest de berichten van vandaag uit alle groepskanalen en registreert wat ontbreekt, ook uit de kanalen van de Avonturiers.
- Beoordelingen worden gelijkgezet met de emoji's die nu in Slack staan.
- Berichten die al in de app staan, krijgen geen 🔒, ook niet als de groep de opdracht in een ander bericht al goedgekeurd heeft. Alleen een gemist bericht dat ná de eerste goedkeuring is geplaatst, wordt geweigerd.
- Er wordt niets weggegooid. Het logboek meldt wat er is ingehaald.
- In handmatige modus wordt niet ingehaald. Gebruik dan **Opnieuw opbouwen uit Slack**.

### Opnieuw opbouwen uit Slack

Hiermee bouw je alle inzendingen opnieuw op uit de Slack-berichten. Gebruik het als er iets flink mis is gegaan, bijvoorbeeld als berichten zijn verwijderd of aangepast terwijl de app niet draaide. Het werkt alleen in beheermodus.

1. Ga naar **Configuratie**, onderaan staat **Opnieuw opbouwen uit Slack**. Voor één groep staat dezelfde knop op de groepspagina.
2. Kies vanaf welk tijdstip berichten meetellen (standaard vandaag 00:00).
3. Typ `OPBOUWEN` en start.

De app leest eerst alle kanalen. Lukt dat voor een kanaal niet, dan stopt hij en verandert er niets. Daarna gaan alle bestanden naar `scouting_storage/_backup/{tijdstip}/`, samen met een `submissions.json` van de oude stand. Vervolgens worden alle berichten en emoji's opnieuw verwerkt. Tijdens het opnieuw opbouwen staat het scorebord even leeg; nieuwe berichten wachten tot hij klaar is.

**Bouw niet opnieuw op tijdens het spel**, tenzij het echt nodig is. Met veel video's kan het meer dan 10 minuten duren.

### Waar de bestanden staan

```text
scouting_storage/
  christiaan-de-wet/                ← nog niet beoordeeld, bijv. CDW 12 14-32-05.jpg
    approved/
    awesome/
    rejected/
    avonturiers/                    ← check-ins en half-uur opdrachten, bijv. CDW L7 13-05-12.jpg
      approved/
      awesome/
      rejected/
  _deleted/christiaan-de-wet/       ← bestanden van verwijderde berichten
  _backup/20261001-143000/          ← back-up van opnieuw opbouwen
```

## De app starten

Er zijn drie manieren om de app te starten:

| Manier | Wanneer | Database |
|---|---|---|
| `run-gameday.sh` | Speeldag op een Mac | SQLite |
| Docker Compose | Speeldag op een server of een andere computer met Docker | PostgreSQL |
| .NET Aspire | Ontwikkelen en testen | PostgreSQL |

Moeten groepen of jury via internet bij de app, zonder in hetzelfde netwerk te zitten? Zet er dan een [Cloudflare Tunnel](#via-internet-cloudflare-tunnel) voor; dat kan bij `run-gameday.sh` en bij Docker Compose.

Voor `run-gameday.sh` en Aspire heb je de [.NET 10 SDK](https://dotnet.microsoft.com/download) en [Node.js](https://nodejs.org/) (met npm) nodig, en de Slack-tokens in de user secrets (zie [Slack koppelen](slack-koppelen.md)). Voor Docker Compose heb je alleen Docker nodig.

### Op de speeldag: `run-gameday.sh`

Start in de map van de repository:

```bash
./run-gameday.sh
```

Het script doet het volgende:

1. Het bouwt een Release-versie van de app in `~/du-app`.
2. De eerste keer neemt het `access-keys.json` en `runtime-settings.json` over uit de projectmap. Zo blijven de sleutels, juryleden en regels hetzelfde. Staan ze al in `~/du-app`, dan blijven die staan.
3. Het haalt de Slack-tokens uit de user secrets. Ontbreekt de BotToken of de AppToken, dan stopt het script met een melding. De app start dan niet.
4. Het start de app met SQLite. De database `scouting_dev.db` staat in `~/du-app`. Het puntenbestand `opdrachtenpunten.csv` leest de app uit de repository, dus een aanpassing daarin geldt na een herstart.
5. Zolang de app draait, gaat de Mac niet in de slaapstand en blijft het scherm aan.

Het script toont het adres van de app: <http://localhost:5231> en het adres in het netwerk, zoals `http://192.168.1.20:5231`. Met dat tweede adres kunnen de jury en de beamer meekijken op hetzelfde netwerk. Stop de app met **Ctrl+C**.

Een andere map of poort kies je zo:

```bash
APP_DIR=~/du-test PORT=8080 ./run-gameday.sh
```

Laat de Mac aan de stroom hangen en houd het deksel open: met het deksel dicht gaat een MacBook toch slapen.

### Met Docker Compose (productie)

In de repository staan een `Dockerfile` en een `docker-compose.yml`. Samen starten ze twee containers: een PostgreSQL-database (`db`) en de app (`app`). De app draait als productieversie, met Socket Mode en zonder simulatiemodus.

**De eerste keer**

1. Installeer Docker (op een Mac: Docker Desktop) en start het.
2. Maak in de map van de repository het bestand `.env` aan, met `.env.example` als voorbeeld:

   ```bash
   cp .env.example .env
   ```

3. Vul in `.env` in:
   - `POSTGRES_PASSWORD`: een zelfgekozen, lang wachtwoord voor de database.
   - `SLACK_BOT_TOKEN` (`xoxb-...`) en `SLACK_APP_TOKEN` (`xapp-...`): de tokens uit [Slack koppelen](slack-koppelen.md).
   - Optioneel `PORT`, de poort op deze computer (standaard `5231`).
   - Optioneel `ACCESS_KEY` en `ADMIN_KEY`, als je de sleutels zelf wilt kiezen. Dat werkt alleen bij de allereerste start.

   `.env` bevat geheimen en staat niet in Git. Ontbreekt een verplichte waarde, dan start Docker Compose niet en noemt het de ontbrekende waarde.

4. Bouw en start de app:

   ```bash
   docker compose up -d --build
   ```

   De eerste keer duurt het bouwen een paar minuten. De app wacht tot de database klaar is.

5. Zoek de sleutels op. Bij de eerste start maakt de app een toegangssleutel en een beheerderssleutel aan:

   ```bash
   docker compose logs app | grep "Generated"
   ```

   Kwijt na een herstart? Zie **Sleutel kwijt?** bij [Sleutels](#sleutels).

6. Open <http://localhost:5231>, of in het netwerk het adres van de computer, zoals `http://192.168.1.20:5231`. Controleer bovenaan de pagina de Slack-status: **Verbonden met Slack**.

**Instellingen overnemen van een eerdere installatie**

Heb je juryleden, regels en bottekst al ingesteld in `runtime-settings.json`? Zet dat bestand dan in de container en herstart de app:

```bash
docker compose exec -T app sh -c 'cat > /data/runtime-settings.json' < src/ScoutingAutomation.Web/runtime-settings.json
docker compose restart app
```

Gebruik dit commando en niet `docker compose cp`: met `cp` hoort het bestand bij een andere gebruiker, en dan kan de app het niet lezen. Voor sleutels uit een eerdere `access-keys.json` werkt het op dezelfde manier, met `/data/access-keys.json`.

**Dagelijks gebruik**

| Wat | Commando |
|---|---|
| Starten | `docker compose up -d` |
| Stoppen | `docker compose down` |
| Logboek volgen | `docker compose logs -f app` |
| Herstarten, bijvoorbeeld na een aanpassing in `opdrachtenpunten.csv` | `docker compose restart app` |
| Nieuwe versie van de code | `git pull` en daarna `docker compose up -d --build` |

Gaat de computer opnieuw aan, dan starten de containers vanzelf mee zodra Docker draait.

**Waar staat alles?**

- De database staat in het Docker-volume `db-data`.
- De bestanden, `runtime-settings.json` en `access-keys.json` staan in het volume `app-data`, in de container onder `/data`. De bestanden van de groepen staan in `/data/storage`.
- De sleutels voor de inlogcookies staan in het volume `app-keys`. Daardoor blijft iedereen ingelogd na een herstart.
- Het puntenbestand komt direct uit de repository: `src/ScoutingAutomation.Web/opdrachtenpunten.csv`.
- Andere instellingen, zoals de start- en eindtijd, pas je aan in de app (beheermodus). Of je zet ze in `docker-compose.yml` onder `environment`, met `__` in plaats van `:`, bijvoorbeeld `Scouting__EventStart: "2026-10-03T09:00"`. Een waarde die je in de app hebt aangepast, gaat altijd voor.

Volumes blijven bewaard bij `docker compose down`, maar `docker compose down -v` wist ze, met de database en alle bestanden. Gebruik `-v` dus nooit op de speeldag.

Een kopie van alle bestanden maak je zo:

```bash
docker compose cp app:/data/storage ./du-bestanden
```

Let op: de app draait zonder HTTPS. In een eigen netwerk is dat goed. Moet de app via internet bereikbaar zijn, gebruik dan een [Cloudflare Tunnel](#via-internet-cloudflare-tunnel).

### Via internet: Cloudflare Tunnel

Met een Cloudflare Tunnel is de app via internet bereikbaar op een HTTPS-adres, bijvoorbeeld `https://du.jouwdomein.nl`. Het programma `cloudflared` maakt zelf een uitgaande verbinding met Cloudflare, net als Socket Mode met Slack. Je hoeft dus geen poort open te zetten in de router, en je hebt geen vast IP-adres of certificaat nodig. Groepen en jury kunnen zo ook via 4G meekijken, en de sleutels gaan versleuteld over het netwerk.

Voor de app maakt het niet uit hoe Slack verbonden is: met Socket Mode gaat Slack niet via de tunnel, alleen het dashboard.

**Wat je nodig hebt**

- Een gratis Cloudflare-account.
- Een eigen domein waarvan Cloudflare de DNS beheert. Zonder domein kan het ook, met een snelle tunnel (zie hieronder), maar dan krijg je bij elke start een ander adres.

**Is het genoeg voor de speeldag?**

Ja. Zes groepen, de jury en een beamer zijn een paar tientallen browsers; dat is weinig voor een tunnel. Goed om te weten:

- Het dashboard werkt met een WebSocket-verbinding. Cloudflare ondersteunt dat. Valt de verbinding even weg, bijvoorbeeld bij een storing bij Cloudflare, dan maakt de pagina vanzelf opnieuw verbinding.
- Groepen uploaden hun bestanden in Slack, niet in de app. De uploadgrens van Cloudflare (100 MB per verzoek) speelt dus geen rol. Video's bekijken en ZIP-exports downloaden werkt gewoon.
- Een snelle tunnel (`trycloudflare.com`) is bedoeld om te testen: het adres verandert bij elke start, er is geen garantie dat hij blijft werken, en er mogen maximaal 200 verzoeken tegelijk lopen. Dat laatste haal je met zes groepen niet, maar een nieuw adres midden in het spel betekent wel dat iedereen opnieuw moet inloggen via een nieuwe link. Gebruik op de speeldag dus een vaste tunnel.
- De tunnel hangt af van het internet van de computer waarop de app draait. Valt dat weg, dan is het dashboard via internet niet bereikbaar. In hetzelfde netwerk werkt het lokale adres (`http://192.168.…:5231`) nog wel.

**Een vaste tunnel aanmaken (eenmalig)**

1. Log in op het [Cloudflare-dashboard](https://dash.cloudflare.com) en ga naar **Zero Trust** > **Networks** > **Tunnels**.
2. Klik op **Create a tunnel**, kies **Cloudflared** en geef de tunnel een naam, bijvoorbeeld `du-review`.
3. Cloudflare toont een token (een lange tekst na `--token`). Kopieer die en bewaar hem veilig: met de token kan iedereen jouw tunnel draaien.
4. Voeg een **Public hostname** toe: kies een subdomein en je domein (bijvoorbeeld `du` en `jouwdomein.nl`), type **HTTP**, en als URL:
   - met `run-gameday.sh`: `localhost:5231`
   - met Docker Compose: `app:8080`
5. Zet bij **SSL/TLS** > **Edge Certificates** **Always Use HTTPS** aan, zodat ook wie `http://` intypt op HTTPS uitkomt.

Gebruik in de tunnel altijd `http` naar de app, niet `https`: de app heeft zelf geen certificaat, Cloudflare zorgt voor HTTPS.

**Met `run-gameday.sh` (Mac)**

Installeer `cloudflared` eenmalig met [Homebrew](https://brew.sh):

```bash
brew install cloudflared
```

Start op de speeldag eerst de app met `./run-gameday.sh`, en in een tweede terminal de tunnel:

```bash
cloudflared tunnel run --token <token>
```

Wil je de tunnel ook na een herstart van de Mac automatisch laten starten? Installeer hem dan eenmalig als dienst: `sudo cloudflared service install <token>`.

Even snel testen zonder domein en zonder account kan met een snelle tunnel. Het adres (`https://….trycloudflare.com`) staat in de terminal:

```bash
cloudflared tunnel --url http://localhost:5231
```

**Met Docker Compose**

`docker-compose.yml` heeft een container `cloudflared` die alleen meestart met het profiel `tunnel`:

1. Zet de token in `.env`:

   ```
   CLOUDFLARE_TUNNEL_TOKEN=<token>
   ```

2. Start alles, met de tunnel erbij:

   ```bash
   docker compose --profile tunnel up -d --build
   ```

3. Controleer de tunnel met `docker compose logs cloudflared`: er staat `Registered tunnel connection`. In het Cloudflare-dashboard staat de tunnel op **Healthy**.

Gebruik `--profile tunnel` ook bij `docker compose down` en `docker compose up -d`, anders blijft de tunnel draaien of start hij niet mee. `docker compose restart app` herstart alleen de app; de tunnel maakt daarna vanzelf weer verbinding.

**Waarom de app de tunnel moet vertrouwen**

De tunnel geeft het echte adres van de bezoeker door, en dat het verzoek via HTTPS binnenkwam. De app gebruikt dat voor de grens op verkeerde sleutels en om het login-cookie alleen via HTTPS te versturen. Dat doorgeven vertrouwt de app alleen van een proxy die hij kent:

- `cloudflared` op dezelfde computer als de app (`run-gameday.sh`): vertrouwd, er hoeft niets ingesteld.
- `cloudflared` in Docker Compose: de container heeft een vast adres (`172.29.53.10`), en de app vertrouwt dat adres via `ForwardedHeaders__KnownNetworks__0` in `docker-compose.yml`. Ook hier hoef je niets in te stellen.
- `cloudflared` of een andere proxy op een andere machine: zet het adres van die machine in `ForwardedHeaders:KnownNetworks`, als lijst van netwerken in CIDR-vorm (`"192.168.1.5/32"`).

Klopt dit niet, dan werkt de app nog steeds, maar ziet hij voor iedereen het adres van de proxy en staat bij elke inlog van een groep een waarschuwing over gewone HTTP in het logboek.

**Extra afschermen (optioneel)**

De app vraagt altijd om een sleutel. Wil je het dashboard daarnaast alleen voor bekende mensen openzetten, dan kan dat met **Cloudflare Access** (Zero Trust > Access > Applications), bijvoorbeeld met een eenmalige code per e-mail. Voor groepen is dat meestal een extra drempel die niet nodig is.

### Voor ontwikkelen: .NET Aspire

Start eerst Docker Desktop. Start dan in de map van de repository:

```bash
dotnet run --project src/ScoutingAutomation.AppHost
```

Aspire start een PostgreSQL-database in Docker (met pgAdmin erbij) en daarna de app. De database blijft bewaard in een Docker-volume, ook na een herstart. In de terminal staat een link naar het Aspire-dashboard. Daar zie je het adres van de app (`web`) en de logboeken. Stop alles met **Ctrl+C**.

De app draait hier als ontwikkelversie en is alleen lokaal te bereiken. Gebruik op de speeldag `run-gameday.sh` of Docker Compose.

## Draaiboek voor de speeldag

Doe de generale repetitie een paar dagen vóór het spel, op dezelfde server en in dezelfde Slack-workspace. Dan is de speeldag zelf vooral kijken.

### Een paar dagen vooraf

- [ ] Elk groepskanaal bestaat en de bot is lid van elk kanaal (`/invite @DU Review`).
- [ ] Alle juryleden staan op **Configuratie**, met hun naam.
- [ ] `opdrachtenpunten.csv` bevat alle opdrachten met de juiste punten, ook de speciale opdrachten met een letter (`A`, `B`, ...). Titel en beschrijving zijn optioneel.
- [ ] De puntencategorieën kloppen (**Beheer** > **Puntencategorieën**, of `Scouting:BonusCategories`).
- [ ] De tijdzone (`Scouting:TimeZone`) staat op `Europe/Amsterdam`.
- [ ] De starttijd (`Scouting:EventStart`) staat op het begin van het spel, bijvoorbeeld `"2026-10-03T09:00"`. Berichten van daarvoor tellen niet mee. Een testbericht van vóór de starttijd verschijnt dus niet op het dashboard. Groepen zien hun opdrachten pas vanaf de starttijd. Op de starttijd plaatst de bot het startbericht in elk groepskanaal.
- [ ] Controleer de tekst van het startbericht: **Configuratie** > **Startbericht in Slack** > **Voorbeeld**. Pas hem zo nodig aan met het potlood (in beheermodus).
- [ ] De eindtijd (`Scouting:EventEnd`, of **Configuratie** > **Speeltijd** in beheermodus) staat op het einde van het spel. Inzendingen vanaf dat moment tellen niet; de bot reageert met :alarm_clock:. Op de eindtijd plaatst de bot het eindbericht in elk groepskanaal.
- [ ] Controleer de tekst van het eindbericht: **Configuratie** > **Eindbericht in Slack** > **Voorbeeld**.
- [ ] Er is genoeg schijfruimte: reken op enkele GB voor video's.
- [ ] Generale repetitie: vier mensen plaatsen tegelijk, één met een grote video (200 MB of meer). Twee juryleden zetten verschillende emoji's op één bericht. Iemand bewerkt een bericht en iemand verwijdert er een. Herstart de app halverwege en controleer dat niets ontbreekt.
- [ ] Gebruik je een [Cloudflare Tunnel](#via-internet-cloudflare-tunnel)? Open het HTTPS-adres op een telefoon via 4G (niet via de wifi), log in en controleer dat het dashboard live bijwerkt.
- [ ] Toegangssleutel en beheerderssleutel staan veilig genoteerd.
- [ ] Elke groep heeft een groepssleutel (**Beheer** > **Groepssleutels**), klaar om uit te delen.
- [ ] Speel je met Avonturiers? Elke groep heeft een Avonturiers-kanaal met de bot erin, `Scouting:PublicUrl` staat goed, de locaties staan op **Avonturiers → Locaties**, het startaantal open locaties klopt (**Configuratie → Avonturiers**), en de rondes en de tijden van startinformatie en eindlocatie op **Avonturiers → Rondes**. Open een kaartlink van een groep op een telefoon via 4G.
- [ ] Wil je de stand aan het eind verbergen? Plan de periode op **Stand voor groepen**, en kies of de beamer meedoet.

### Op de ochtend van het spel

- [ ] Start de app met `./run-gameday.sh` of `docker compose up -d` (zie [De app starten](#de-app-starten)) en controleer bovenaan de pagina de Slack-status: **Verbonden met Slack**.
- [ ] Kijk op **Gebeurtenissen**: er staat **Verbonden met Slack** en een **Bijwerken**-melding, en geen **Fout in de app**. Houd deze pagina tijdens het spel ergens open.
- [ ] **Handmatige modus** staat uit (geen melding bovenaan).
- [ ] Plaats in elk kanaal een testbericht met een foto en `Opdracht 1` (in een Avonturiers-kanaal `Locatie 1`). Controleer dat het op het dashboard verschijnt, en verwijder het daarna in Slack.
- [ ] Gebruik je een tunnel? Controleer dat hij draait (Cloudflare-dashboard: **Healthy**) en geef groepen alleen het HTTPS-adres.
- [ ] Deel de toegangssleutel met de jury en de mensen bij het scorebord.
- [ ] Geef elke groep haar eigen groepssleutel. Deel nooit de toegangssleutel met een groep: daarmee zie je alles.
- [ ] Zet de diavoorstelling of het dashboard klaar op de beamer.
- [ ] Na de starttijd: elk groepskanaal heeft het startbericht. Op **Configuratie** > **Startbericht in Slack** staat per groep **Geplaatst om …**. Mist er een, of is er geen starttijd ingesteld, klik dan in beheermodus op **Startbericht nu sturen**.
- [ ] Na de eindtijd: elk groepskanaal heeft het eindbericht (**Configuratie** > **Eindbericht in Slack**), en bovenaan de app staat **Spel afgelopen**.

### Uitleg aan de groepen (twee minuten)

Alles voor de groepen staat in de [handleiding voor groepen](handleiding-groepen.md). Kort:

- Plaats je opdracht in je eigen kanaal met het nummer erbij: `Opdracht 12`.
- Meerdere foto's in één bericht mag.
- Zet de bot een ❓, bewerk dan je bericht en zet het juiste nummer erin.
- Zet de bot een ⚠️, lees de uitleg in de thread.
- Met jullie groepssleutel zie je in de app jullie score, de stand en jullie inzendingen.

### Uitleg aan de jury (twee minuten)

Alles voor de jury staat in de [handleiding voor de jury](handleiding-jury.md). Kort:

- Eén emoji per bericht: 👍 goed, 🔥 awesome, ❌ afgekeurd.
- De emoji geldt voor alle bestanden in het bericht.
- Bij twee verschillende emoji's telt de beste. Overleg als je wilt afwaarderen.
- Andere emoji's tellen niet; je kunt ze vrij gebruiken.

### Tijdens het spel

- Houd de Slack-status bovenaan in de gaten. Staat er een rode melding in plaats van **Verbonden met Slack**, kijk dan in het logboek.
- Gebruik **Opnieuw opbouwen uit Slack** niet tijdens het spel, tenzij het echt nodig is.
- Gebruik **Status handmatig aanpassen** alleen als Slack hapert.
- Kijk op het **Dashboard** naar **Laatst actief**. Geel betekent dat een groep 30 minuten niets heeft ingestuurd, rood een uur. Misschien zitten ze vast of zijn ze verdwaald.
- Zet de pagina **Scorebord** op de beamer, of zet de diavoorstelling aan met **Scorebord tussendoor**.

### Na een herstart of storing

1. Wacht tot de Slack-status **Verbonden met Slack** is.
2. Kijk in het logboek of het inhalen klaar is: "Catch-up done" of "Catch-up found missed Slack activity".
3. Ontbreekt er toch iets, of zijn er berichten verwijderd of aangepast tijdens de storing? Gebruik dan **Opnieuw opbouwen uit Slack** vanaf vandaag 00:00.

### Na afloop

- Maak een kopie van de map `scouting_storage` voordat je iets opruimt.
- Download de uitslag en de bestanden via **Exporteren** op het **Dashboard** (beheermodus): de scores als CSV, en de awesome of alle goedgekeurde bestanden als ZIP. Per groep kan dat ook met **Downloaden** op de pagina van de groep.
- Gebruik de favorieten en de diavoorstelling voor de prijsuitreiking.

## Problemen oplossen

Begin altijd bij de Slack-status bovenaan de pagina en het logboek van de app. De meeste problemen zijn een kanaal zonder bot of een jurylid dat niet op de lijst staat.

| Wat je ziet | Wat je doet |
| --- | --- |
| Een inzending verschijnt niet op het dashboard | Staat er een opdrachtnummer in het bericht? Zit er een bestand bij? Is de bot lid van het kanaal? Zie je een ❓ of ⚠️ van de bot, lees dan de thread. Op de pagina **Gebeurtenissen** zie je wat de app met het bericht deed. |
| De bot reageert met ❓ | Het nummer ontbreekt of de opdracht bestaat niet. De groep bewerkt het bericht en zet het juiste nummer erin. |
| De bot reageert met ⚠️ "kon niet worden opgehaald" | De download mislukte, vaak door een slechte verbinding. De groep plaatst het bestand opnieuw. |
| De emoji van een jurylid doet niets | Staat het jurylid op **Configuratie**? Is het een beoordelings-emoji (zie de [handleiding voor de jury](handleiding-jury.md#beoordelen-in-slack))? Staat **Handmatige modus** uit? |
| De status is anders dan het laatste emoji | Dat klopt als juryleden het oneens zijn: de beste beoordeling telt. Haal de emoji weg die niet meer moet tellen. |
| Bovenaan staat een rode melding **Slack: fout** of **Slack: verbinden** | De app heeft geen verbinding met Slack. Controleer het internet van de server en het logboek. Na herstel haalt de app vanzelf in wat hij miste. |
| De app is herstart of was een tijd weg | Wacht op **Verbonden met Slack**. De app haalt de berichten van vandaag vanzelf in. Ontbreekt er daarna nog iets, gebruik dan **Opnieuw opbouwen uit Slack**. |
| Iemand kan niet meer inloggen | Is de toegangssleutel net vernieuwd? Geef de nieuwe sleutel. Na 30 foute pogingen binnen een minuut even wachten. |
| Een handmatige aanpassing is verdwenen | Er is opnieuw opgebouwd uit Slack, of een jurylid heeft daarna een emoji gezet. Slack is leidend; zet de emoji in Slack. |
| Een video speelt niet af in het dashboard | Download het bestand via de detailpagina; niet elke browser speelt elk videoformaat af. |
| Een bestand staat in `_deleted` | Het bericht of bestand is in Slack verwijderd. Zet het terug door het opnieuw in Slack te plaatsen. |

Voor problemen met het koppelen van Slack zelf (tokens, rechten, Socket Mode) staat een uitgebreide tabel in [Slack koppelen](slack-koppelen.md#problemen-oplossen).
