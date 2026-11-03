# Handleiding voor de organisatie

Laatst bijgewerkt: 6 oktober 2026

Deze handleiding is voor wie DU Review opzet en op de speeldag beheert: de app starten, Slack koppelen, accounts, instellingen, en wat te doen bij een storing. Hoe de jury beoordeelt en wat de pagina's van het dashboard doen, staat in de [handleiding voor de jury](handleiding-jury.md); wat groepen moeten weten in de [handleiding voor groepen](handleiding-groepen.md).

## Zo werkt het

Groepen plaatsen hun opdrachten in Slack, de jury beoordeelt ze daar met een emoji, en DU Review doet de rest: bestanden opslaan, punten tellen en een live scorebord tonen. Slack is altijd leidend: wat in Slack staat, telt.

1. Een scout plaatst een foto, video, geluidsopname of document in het Slack-kanaal van de eigen groep, met het opdrachtnummer in de tekst.
2. De app downloadt het bestand en slaat het op in de map van de groep, met een vaste naam zoals `CDW 12 14-32-05.jpg`.
3. Een jurylid reageert in Slack met een emoji: goedgekeurd, awesome of afgekeurd.
4. De app verplaatst het bestand naar de juiste map en werkt het scorebord direct bij.

Klopt er iets niet, bijvoorbeeld een ontbrekend opdrachtnummer, dan reageert de bot zelf in Slack met een emoji en een uitleg in de thread.

Het koppelen van Slack (de Slack-app, rechten, kanalen) staat in [Slack koppelen](slack-koppelen.md). Hoe de app gemiste berichten inhaalt en herbouwt, staat uitgebreid in [Slack-synchronisatie](slack-sync.md).

## Beheer in de app

Beheertaken doe je met een **beheerdersaccount**. Een juryaccount is genoeg om te kijken. Wat elke soort account mag, staat in de [README](../README.md#toegang).

### De eerste beheerder

- Bij de eerste start maakt de app een beheerder voor het adres in `Scouting:Accounts:BootstrapAdminEmail`. Bij Docker zet je dat als `BOOTSTRAP_ADMIN_EMAIL` in `.env`; bij `run-gameday.sh` in de user secrets:

  ```bash
  dotnet user-secrets set "Scouting:Accounts:BootstrapAdminEmail" "jij@example.nl" --project src/ScoutingAutomation.Web
  ```

- Die beheerder krijgt een mail met een link om een wachtwoord te kiezen. Alleen als er geen mail kan worden verstuurd, staat de link in het logboek (`docker compose logs app | grep wachtwoord-instellen`). De link is 3 dagen geldig.
- Is er geen beheerder en geen adres ingesteld, dan start de app niet en zegt het logboek wat er mist.
- **Wachtwoord kwijt?** Gebruik **Wachtwoord vergeten?** op het loginscherm, of laat een andere beheerder op **Beheer** > **Accounts** een link maken. Maak daarom altijd een tweede beheerder aan.

### Accounts

Op **Beheer** > **Accounts**:

- **Account maken**: naam, e-mailadres en soort (jury, beheerder, groep of beamer). Bij een groep kies je ook welke groep. De app mailt een uitnodiging met een link om een wachtwoord te kiezen.
- **Link mailen** stuurt een nieuwe link om een wachtwoord te kiezen. Zonder mailserver heet de knop **Link maken** en toont hij de link, zodat je hem zelf kunt doorsturen (bijvoorbeeld in het Slack-kanaal van een groep). Een link werkt één keer en is 3 dagen geldig.
- Met **Wijzigen** pas je naam, soort of groep aan. **Blokkeren** houdt iemand buiten zonder het account weg te halen; **Verwijderen** haalt het weg. Wie geblokkeerd, verwijderd of van soort veranderd is, wordt binnen een minuut uitgelogd.
- Bij de lijst staat welke groepen nog geen account hebben (**Nog geen account: …**).
- Je kunt jezelf niet blokkeren of verwijderen, en er blijft altijd minstens één beheerder.
- Maak voor de beamer één account van de soort **Beamer**: dat ziet alleen het scorebord en de diavoorstelling, ook als iemand anders achter de laptop kruipt.

### Mail

De app mailt uitnodigingen en links voor **Wachtwoord vergeten?**. Stel de mailserver in met `Scouting:Email`, bij Docker met deze regels in `.env`:

| Regel | Betekenis |
|---|---|
| `SMTP_HOST` | De mailserver, bijvoorbeeld `smtp.example.nl`. Leeg: de app mailt niets. |
| `SMTP_PORT` | Standaard `587`. |
| `SMTP_SECURITY` | `StartTls` (poort 587, standaard), `SslOnConnect` (poort 465) of `None`. |
| `SMTP_USERNAME`, `SMTP_PASSWORD` | Inloggegevens van de mailserver. |
| `SMTP_FROM` | Het afzenderadres. |
| `PUBLIC_URL` | Het adres van de app van buiten, bijvoorbeeld `https://du.jouwdomein.nl`. De links in de mails beginnen hiermee. Zonder dit adres mailt **Wachtwoord vergeten?** niets; een beheerder maakt de link dan op **Beheer** > **Accounts**. |

Op **Beheer** > **Mail** zie je of mail is ingesteld en stuur je een testmail. Zonder mailserver werkt alles ook: maak de links dan met **Link maken** en stuur ze zelf door.

### Van sleutels naar accounts

Kom je van een versie met sleutels (toegangssleutel, beheerderssleutel, groepssleutels)? Die werken niet meer. Bij de eerste start van de nieuwe versie krijgt de database de tabellen voor accounts erbij; inzendingen, punten en instellingen blijven staan. Zet `BOOTSTRAP_ADMIN_EMAIL` voor je de nieuwe versie start, kies je wachtwoord via de link, en maak dan de accounts voor de jury, de groepen en de beamer. `access-keys.json` mag weg.

### Edities en groepen

Elk jaar is een editie met eigen groepen, opdrachten en inzendingen; alleen de actieve editie verwerkt Slack. Alles staat op **Beheer** > **Edities** (zie ook de [README](../README.md#edities)).

**Een nieuw jaar voorbereiden** (een paar weken vooraf):

1. Maak bij **Nieuwe editie** de editie aan, bijvoorbeeld "DU 2027". Laat **Groepen van de actieve editie overnemen** aan als de meeste groepen weer meedoen.
2. Kies **Bekijken** bij de nieuwe editie en zet de groepen goed: haal groepen weg die niet meedoen, voeg nieuwe toe (kies een bestaand team als een groep van eerder terugkomt, zodat haar account blijft werken), en controleer de Slack-kanalen. Hetzelfde kanaal als vorig jaar mag.
3. Maak accounts voor nieuwe groepen op **Accounts**; je kunt daar ook teams kiezen die nog niet in de actieve editie zitten.

**Op de speeldag** (of de avond ervoor): kies bij de nieuwe editie **Deze editie starten** en bevestig. De vorige editie wordt gearchiveerd met haar eindstand, groepen loggen opnieuw in, en vanaf dan gaat alles over de nieuwe editie. Zet daarna op **Configuratie** de start- en eindtijd; kijk ook of de juryleden, teksten en regels nog kloppen.

Een groep met inzendingen kan niet uit een editie worden gehaald. Groepen in de actieve editie wijzigen kan altijd; de app pauzeert Slack daarvoor heel even. `Groups` in `appsettings.json` gebruikt de app alleen voor een installatie zonder groepen.

**Een editie bewaren of verhuizen**: kies bij een editie **Exporteren**. Je krijgt één ZIP met alle gegevens en bestanden van die editie. Zet die met **Editie importeren** (onderaan **Beheer**) terug in een andere installatie, of later in dezelfde; hij komt erbij als gearchiveerde editie en de actieve editie blijft zoals hij is. Bestaat er al een editie met die naam, hernoem die dan eerst. Een grote ZIP importeer je via het lokale adres (`http://localhost:5231`, of de server zelf), niet via de Cloudflare-tunnel: die laat op het gratis plan maar ongeveer 100 MB door.

### Van één spel naar edities

Vanaf deze versie hoort alles bij een **editie** (een jaar van het spel); zie de [README](../README.md#edities). Bij de eerste start wordt een bestaande database de eerste editie, genoemd naar het jaar van de eerste inzending ("DU 2025"). Groepen, opdrachten, locaties, inzendingen en punten blijven staan en groepsaccounts blijven werken.

- **SQLite** (`run-gameday.sh`, lokaal draaien): de app bewaart vooraf een kopie in `scouting_storage/_backup/pre-editions-{tijdstip}.db`. Gaat er iets mis, zet dan die kopie terug als database (verwijder dan ook `scouting_dev.db-wal` en `scouting_dev.db-shm`, als die er staan) en start de vorige versie.
- **PostgreSQL** (Docker Compose): maak vóór de update zelf een back-up, bijvoorbeeld met `pg_dump scoutingdb > voor-edities.sql` op de databaseserver. De omzetting gebeurt in één keer; lukt die niet, dan blijft de database zoals hij was en start de app niet.

### Juryleden

Op **Configuratie** voeg je juryleden toe met hun Slack member-ID, haal je ze weg en pas je hun namen aan. Alleen emoji's van juryleden op deze lijst tellen. Wijzigingen gelden direct, zonder herstart.

Een member-ID vind je in Slack via het profiel van iemand: **⋮** → **Copy member ID**. Het begint met `U`.

### Beoordelingsregels

Op **Configuratie**, onder **Beoordelingsregels** (alleen voor beheerders):

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

Op **Beheer** (alleen voor beheerders):

- **Extra punten**: vul per groep de punten per categorie in en klik op **Opslaan**. Ze tellen direct mee in de score en de stand.
- **Puntencategorieën**: voeg een categorie toe (bijvoorbeeld een fotowedstrijd), wijzig de naam met het potlood, of haal er een weg met de prullenbak. De sleutel wordt uit de naam gemaakt als je hem leeg laat, en blijft daarna altijd hetzelfde.

Haal je een categorie weg, dan tellen de punten ervan niet meer mee, maar ze blijven bewaard. Voeg je dezelfde sleutel later weer toe, dan zijn de punten terug. De standaardlijst staat in `appsettings.json` (`Scouting:BonusCategories`); **Terug naar appsettings** zet die terug.

### Avonturiers

Het Avonturiers-deel staat standaard uit. Zo zet je het op:

1. Maak per groep een tweede Slack-kanaal voor de Avonturiers en nodig de bot uit (`/invite @DU Review`).
2. Vul op **Beheer** > **Edities** bij elke groep het Avonturiers-kanaal in, en zet `Scouting:Adventurers:Enabled` op `true`. Een kanaal mag maar bij één groep per editie horen.
3. Zet `Scouting:PublicUrl` op het adres waarop de app van buiten bereikbaar is (bijvoorbeeld het adres van de [Cloudflare Tunnel](#via-internet-cloudflare-tunnel)). Zonder dat adres staat er "(de kaartlink volgt)" in plaats van de kaartlink.
4. Herstart de app. In het menu staat nu **Avonturiers**.

**Locaties** (**Avonturiers → Locaties**, beheerders): klik op de kaart om een locatie te plaatsen, vul nummer, naam en eventueel een hint in, en sla op. Een hele lijst importeer je uit een KML-, KMZ-, GeoJSON-, GPX- of CSV-bestand (maximaal 5 MB). Het nummer komt vooraan de naam (`15 Opdracht`) of uit een kolom `nummer`.

*Vanuit Google My Maps*: noem elke locatie `{nummer} {naam}` (bijvoorbeeld `15 Opdracht`) en zet de uitleg in de beschrijving; die wordt de hint op de kaart. Exporteer de laag via de drie puntjes naast de laagnaam → **Gegevens exporteren** → **KML/KMZ**, en kies dat bestand bij **Importeren**. Lijnen, vlakken en punten zonder nummer worden overgeslagen; ze staan in de lijst onder de knop. Een CSV heeft de kolommen `nummer;naam;lat;lon;hint`; puntkomma's en decimale komma's van Nederlandse Excel mogen. Een import vult aan en past bestaande nummers aan; hij verwijdert niets. Wat niet gelezen kon worden, staat in een lijst onder de knop.

**Open locaties per groep** (**Beheer**): een groep heeft locatie 1 tot en met een nummer open. Laat het veld leeg voor het startaantal (`InitialOpenLocations`, standaard 10, ook aan te passen op **Configuratie → Avonturiers**). Opent het andere spelonderdeel een locatie voor een groep, verhoog dan haar nummer, of klik op **+1**. Alleen open locaties staan op de kaart van die groep. Een check-in op een locatie die nog niet open is, krijgt 🚧 en telt vanzelf zodra je het nummer verhoogt; de bot meldt de Toeristen dan de nieuwe opdrachten. Ernaast zie je per groep hoeveel check-ins zijn goedgekeurd, hoeveel daarvan tellen en hoeveel opdrachten open zijn.

**Opdrachten vrijspelen**: de eerste `InitialOpenAssignments` opdrachten (standaard 30) zijn altijd open. De rest gaat open naar rato van de getelde locaties: met 160 opdrachten en 100 locaties opent elke locatie er 1 of 2, in nummervolgorde, en na alle 100 locaties zijn alle 160 open. Een check-in telt pas vanaf het einde van de ronde waarin hij is geplaatst (vóór of tussen de rondes: het einde van de volgende ronde; na de laatste ronde of zonder rondes: meteen), zodat de opdrachten per ronde opengaan. Zet de locaties dus in de app voordat het spel begint: zonder locaties zijn alle opdrachten open. Een inzending voor een opdracht op slot krijgt 🔒 en telt niet. Op **Configuratie → Avonturiers** pas je de teksten van de bot aan.

**Supercoördinaten**: een goedgekeurde check-in op een supercoördinaat telt als twee locaties en opent dus twee keer zoveel opdrachten; alle locaties samen openen nog steeds alle opdrachten. Standaard staan ze aan op 10, 20, 30 … 100 (`SuperLocations` in appsettings). Op **Configuratie → Avonturiers** zet je ze aan of uit en vul je de nummers in, gescheiden door komma's; **Elke 10e** vult elke tiende locatie in tot de hoogste. Een wijziging telt meteen voor alle groepen, en de bot meldt de nieuwe opdrachten (of een correctie) in de Toeristen-kanalen. Pas ze dus liefst aan vóór het spel.

**Opdrachten en afbeeldingen**: op de pagina van een opdracht kan de jury met **Bewerken** de titel, beschrijving en punten aanpassen en afbeeldingen toevoegen. Wat in de app is aangepast, wint van `opdrachtenpunten.csv`, ook na een herstart, tot iemand op **Terug naar puntenbestand** klikt. Afbeeldingen staan in de map `_opdrachten` van de editie (voor de eerste editie `scouting_storage/_opdrachten/`); neem die mee in je back-up. Ze gaan mee in de export van een editie. Een nieuwe editie neemt de aangepaste opdrachten over, maar niet de afbeeldingen.

**Rondes** (**Avonturiers → Rondes**, beheerders): vul begin en einde in en klik op **Genereer rondes** voor rondes van `RoundMinutes` minuten (standaard 30) met half-uur opdracht N in ronde N. Pas daarna per ronde de half-uur opdrachten of de openingstekst aan en klik op **Opslaan**. Rondes mogen niet overlappen en een half-uur opdracht hoort bij één ronde. Sluiten twee rondes op elkaar aan (zoals na **Genereer rondes**), dan plaatst de bot één overgangsbericht in plaats van de sluiting en de opening. Die tekst pas je aan onder **Overgang naar de volgende ronde**; maak je hem leeg, dan gaan sluiting en opening los. Onder **Berichten per ronde** zie je per ronde wat de bot al geplaatst heeft, en stuur je een bericht met de hand, met een foto of opnieuw naar alle groepen.

**Startinformatie en eindlocatie** (**Avonturiers → Rondes**): de tekst, het tijdstip waarop de bot hem zelf plaatst (vooraf in te stellen met `Scouting:Adventurers:StartAnnouncement:PostAt` en `FinalAnnouncement:PostAt`; een tijd die je in de app aanpast zet je met **Terug naar appsettings** terug). Die tijden zijn ook de start en het einde van de Avonturiers: berichten in hun kanalen tellen alleen daartussen, en groepen zien hun kaart pas vanaf de start. Zonder eigen tijd gelden de start- en eindtijd van het spel. Verder: of nu sturen met eventueel een foto (bijvoorbeeld een plattegrond). In de tekst: `{group}`, `{map}`, `{time}`, `{approved}` en `{rejected}`.

**Punten**: de punten van de half-uur opdrachten worden uitgerekend, met een maximum van `ThemeMaxPoints` (standaard 14). Dat maximum en dat van het eindspel pas je aan op **Configuratie → Avonturiers** onder **Maximum aantal punten**; punten erboven blijven bewaard en tellen weer mee als je het maximum verhoogt. Het **eindspel**, en een startbonus voor groepen met eigen vervoer, vul je met de hand in op **Beheer** onder **Extra punten**, in de kolom **Avonturiers: eindspel** (maximaal `FinalMaxPoints`, standaard 14). Onder elk veld staat het totaal dat telt. De categorie Avonturiers is vast zolang de Avonturiers aan staan: hij kan niet weg, alleen een andere naam krijgen. Het totaal staat ook op **Avonturiers → Overzicht**, op het dashboard en in de export.

**Herstel**: catch-up en herbouwen lezen ook de Avonturiers-kanalen. De bot plaatst daarbij geen berichten over vrijgespeelde opdrachten of rondes opnieuw. Zie [Slack-sync](slack-sync.md).

### Handmatige modus

Zet **Handmatige modus** aan met de schakelaar op het **Dashboard** als de organisatie alles zelf wil beoordelen. De app slaat dan alleen nog bestanden op met de juiste naam in de map van de groep. Er komen geen inzendingen op het dashboard, emoji's worden genegeerd en de bot zegt niets in Slack. Bovenaan elke pagina staat dan **Handmatige modus**. De schakelaar blijft staan na een herstart en geldt voor alle instanties van de app.

### Automatisch inhalen

Slack stuurt elk bericht en elke emoji maar één keer. Draaide de app even niet, of was de verbinding weg, dan haalt hij zelf in wat hij heeft gemist: bij het opstarten en bij elke nieuwe verbinding met Slack.

- De app leest de berichten van vandaag uit alle groepskanalen en registreert wat ontbreekt, ook uit de kanalen van de Avonturiers.
- Beoordelingen worden gelijkgezet met de emoji's die nu in Slack staan.
- Berichten die al in de app staan, krijgen geen 🔒, ook niet als de groep de opdracht in een ander bericht al goedgekeurd heeft. Alleen een gemist bericht dat ná de eerste goedkeuring is geplaatst, wordt geweigerd.
- Er wordt niets weggegooid. Het logboek meldt wat er is ingehaald.
- In handmatige modus wordt niet ingehaald. Gebruik dan **Opnieuw opbouwen uit Slack**.

### Opnieuw opbouwen uit Slack

Hiermee bouw je alle inzendingen opnieuw op uit de Slack-berichten. Gebruik het als er iets flink mis is gegaan, bijvoorbeeld als berichten zijn verwijderd of aangepast terwijl de app niet draaide. Het werkt alleen als beheerder.

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
| Docker Compose | Speeldag op een server met Docker en een eigen PostgreSQL-server | PostgreSQL (eigen server) |
| Portainer | Hetzelfde, maar beheerd vanuit [Portainer](#met-portainer) | PostgreSQL (eigen server) |
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
2. Staat er nog geen database in `~/du-app`, dan neemt het een `runtime-settings.json` uit de projectmap mee. De eerste start leest dat bestand één keer in de database in, zodat de juryleden en regels hetzelfde blijven. Daarna staan de instellingen alleen nog in de database.
3. Het haalt de Slack-tokens en het adres van de eerste beheerder (zie [De eerste beheerder](#de-eerste-beheerder)) uit de user secrets. Ontbreekt de BotToken of de AppToken, dan stopt het script met een melding. De app start dan niet.
4. Het start de app met SQLite. De database `scouting_dev.db` staat in `~/du-app`. Het puntenbestand `opdrachtenpunten.csv` leest de app uit de repository, dus een aanpassing daarin geldt na een herstart.
5. Zolang de app draait, gaat de Mac niet in de slaapstand en blijft het scherm aan.

Het script toont het adres van de app: <http://localhost:5231> en het adres in het netwerk, zoals `http://192.168.1.20:5231`. Met dat tweede adres kunnen de jury en de beamer meekijken op hetzelfde netwerk. Stop de app met **Ctrl+C**.

Een andere map of poort kies je zo:

```bash
APP_DIR=~/du-test PORT=8080 ./run-gameday.sh
```

Laat de Mac aan de stroom hangen en houd het deksel open: met het deksel dicht gaat een MacBook toch slapen.

### Met Docker Compose (productie)

In de repository staan een `Dockerfile` en een `docker-compose.yml`. Samen starten ze twee instanties van de app (`app`) en een proxy ervoor (`proxy`, [Caddy](https://caddyserver.com)). De app draait als productieversie, met Socket Mode en zonder simulatiemodus. De database zit er niet in: de app gebruikt een PostgreSQL-server die je zelf beheert, op dezelfde computer of ergens anders.

De proxy verdeelt de bezoekers over de instanties en houdt elke bezoeker bij dezelfde instantie, met een cookie `du_instance`. Eén instantie praat met Slack; valt die weg, dan neemt de andere het binnen een paar seconden over. Zie [Meerdere instanties](../README.md#meerdere-instanties) in de README. Het aantal instanties stel je in met `APP_REPLICAS` in `.env` (standaard `2`).

Het image van de app hoef je niet zelf te bouwen: GitHub bouwt het bij elke release en zet het in de GitHub Container Registry (`ghcr.io/ricardotill/du_review`), voor `amd64` en `arm64` (bijvoorbeeld een Mac met Apple-chip of een Raspberry Pi). Zie [Updates en versies](#updates-en-versies).

**De eerste keer**

1. Installeer Docker (op een Mac: Docker Desktop) en start het.
   Zorg voor een PostgreSQL-server (versie 14 of nieuwer) met een lege database en een gebruiker die daarin tabellen mag maken:

   ```sql
   CREATE USER du WITH PASSWORD 'een-lang-wachtwoord';
   CREATE DATABASE scoutingdb OWNER du;
   ```

   Draait PostgreSQL op dezelfde computer als Docker, dan bereiken de containers hem als `host.docker.internal`. Laat PostgreSQL dan ook luisteren op het adres van Docker (`listen_addresses` in `postgresql.conf`, bijvoorbeeld `'*'`) en sta de Docker-netwerken toe in `pg_hba.conf`, bijvoorbeeld `host scoutingdb du 172.16.0.0/12 scram-sha-256`.
2. Maak in de map van de repository het bestand `.env` aan, met `.env.example` als voorbeeld:

   ```bash
   cp .env.example .env
   ```

3. Vul in `.env` in:
   - `DATABASE_CONNECTION_STRING`: hoe de app de database bereikt, bijvoorbeeld `Host=host.docker.internal;Database=scoutingdb;Username=du;Password=een-lang-wachtwoord`. Staat de database op een andere machine, zet er dan `;SSL Mode=VerifyFull` achter (of minstens `;SSL Mode=Require`). Anders gaan het wachtwoord en alle gegevens onversleuteld over het netwerk.
   - `SLACK_BOT_TOKEN` (`xoxb-...`) en `SLACK_APP_TOKEN` (`xapp-...`): de tokens uit [Slack koppelen](slack-koppelen.md).
   - Optioneel `PORT`, de poort op deze computer (standaard `5231`).
   - `BOOTSTRAP_ADMIN_EMAIL`: jouw e-mailadres. Daarvoor maakt de app bij de eerste start het eerste beheerdersaccount.
   - `PUBLIC_URL` en de `SMTP_`-regels, voor mails over accounts (zie [Mail](#mail)). Zonder mailserver kan het ook.

   `.env` bevat geheimen en staat niet in Git. Ontbreekt een verplichte waarde, dan start Docker Compose niet en noemt het de ontbrekende waarde.

4. Log eenmalig in bij de registry. Het image is privé; maak op GitHub een token (classic) met alleen het recht `read:packages` en gebruik dat als wachtwoord:

   ```bash
   docker login ghcr.io -u <je GitHub-gebruikersnaam>
   ```

5. Haal het image op en start de app:

   ```bash
   docker compose pull
   docker compose up -d
   ```

   Is de database niet bereikbaar, dan stopt de app met een melding in het logboek en probeert Docker het steeds opnieuw. Wil je een eigen aanpassing draaien die nog niet is uitgebracht? Bouw het image dan zelf met `docker compose up -d --build` (dat duurt een paar minuten).

6. Kies je wachtwoord via de link in de mail. Alleen als er geen mail kan worden verstuurd, staat de link in het logboek:

   ```bash
   docker compose logs app | grep wachtwoord-instellen
   ```

   Maak daarna op **Beheer** > **Accounts** de accounts voor de jury, de groepen en de beamer.

7. Open <http://localhost:5231>, of in het netwerk het adres van de computer, zoals `http://192.168.1.20:5231`. Controleer bovenaan de pagina de Slack-status: **Verbonden met Slack**.

**Instellingen overnemen van een eerdere installatie**

De app bewaart wat je in de app instelt in de database. Een oudere versie gebruikte daarvoor `runtime-settings.json`. Bij de overstap naar deze versie leest de app een bestaand `/data/runtime-settings.json` vanzelf één keer in en hernoemt het naar `runtime-settings.json.imported`. Wil je een bestand van een andere installatie overnemen, en zijn er nog geen instellingen in de database opgeslagen? Zet het bestand dan in de container en herstart de app:

```bash
docker compose exec -T app sh -c 'cat > /data/runtime-settings.json' < src/ScoutingAutomation.Web/runtime-settings.json
docker compose restart app
```

Gebruik dit commando en niet `docker compose cp`: met `cp` hoort het bestand bij een andere gebruiker, en dan kan de app het niet lezen.

**Dagelijks gebruik**

| Wat | Commando |
|---|---|
| Starten | `docker compose up -d` |
| Stoppen | `docker compose down` |
| Logboek volgen | `docker compose logs -f app` |
| Herstarten, bijvoorbeeld na een aanpassing in `opdrachtenpunten.csv` | `docker compose restart app` (alle instanties) |
| Aantal instanties tijdelijk aanpassen | `docker compose up -d --scale app=3` |
| Nieuwe versie | `docker compose pull app` en daarna `docker compose up -d app` (of vanzelf, zie [Updates en versies](#updates-en-versies)) |
| Eigen versie van de code bouwen | `git pull` en daarna `docker compose up -d --build` |

Gaat de computer opnieuw aan, dan starten de containers vanzelf mee zodra Docker draait.

#### Updates en versies

GitHub Actions test elke wijziging en bouwt daarna het image:

| Tag | Wanneer | Gebruik |
|---|---|---|
| `latest` | bij elke stabiele release: een Git-tag `v1.4.0` op een commit van `main` (een tag als `v1.4.0-rc1` schuift `latest` niet) | standaard in `docker-compose.yml` |
| `1.4.0`, `1.4` | bij die release | een vaste versie, met `APP_VERSION=1.4.0` in `.env` |
| `main`, `sha-<code>` | bij elke wijziging op `main` | testen van een versie die nog niet is uitgebracht |

Een release maak je met een tag op `main`, bijvoorbeeld `git tag v1.4.0 && git push origin v1.4.0`, of via **Releases** op GitHub. Alleen dan schuift `latest`: een wijziging op `main` komt dus nooit vanzelf op de server.

Haalt de server nieuwe images automatisch op (bijvoorbeeld met Watchtower), zet dan voor de speeldag `APP_VERSION` op de huidige versie en start opnieuw met `docker compose up -d app`. Zo herstart de app niet midden in het spel. Haal de regel na de speeldag weer weg.

**Waar staat alles?**

- De database staat op je eigen PostgreSQL-server. Maak daar ook de back-ups, bijvoorbeeld met `pg_dump`.
- De accounts, de instellingen uit de app en de sleutels voor de inlogcookies en wachtwoordlinks staan in de database. Daardoor blijft iedereen ingelogd na een herstart.
- De bestanden staan in het volume `app-data`, in de container onder `/data`. Alle instanties gebruiken dat ene volume. De bestanden van de groepen staan in `/data/storage`.
- Na de overstap naar deze versie moet iedereen één keer opnieuw inloggen, omdat de sleutels voor de inlogcookies van het volume `app-keys` naar de database gaan. Dat volume is daarna niet meer nodig.
- Het puntenbestand komt direct uit de repository: `src/ScoutingAutomation.Web/opdrachtenpunten.csv` (via `docker-compose.override.yml`, dat `docker compose` vanzelf meeleest).
- Andere instellingen, zoals de start- en eindtijd, pas je aan in de app (als beheerder). Of je zet ze in `docker-compose.yml` onder `environment`, met `__` in plaats van `:`, bijvoorbeeld `Scouting__EventStart: "2026-10-03T09:00"`. Een waarde die je in de app hebt aangepast, gaat altijd voor.

Volumes blijven bewaard bij `docker compose down`, maar `docker compose down -v` wist ze, met alle bestanden. Gebruik `-v` dus nooit op de speeldag.

Een kopie van alle bestanden maak je zo:

```bash
docker compose cp app:/data/storage ./du-bestanden
```

Let op: de app draait zonder HTTPS. In een eigen netwerk is dat goed. Moet de app via internet bereikbaar zijn, gebruik dan een [Cloudflare Tunnel](#via-internet-cloudflare-tunnel).

### Met Portainer

Draai je containers met [Portainer](https://www.portainer.io), dan zet je dezelfde `docker-compose.yml` neer als een **stack**. Het bestand haalt niets uit de repository: de instellingen van de proxy staan erin, en het puntenbestand zit in het image. Daardoor werkt het als stack uit Git én als stack die je in de editor plakt. Gebruik een recente Portainer (2.21 of nieuwer).

**Vooraf**

1. Een PostgreSQL-server met een lege database en een gebruiker, zoals bij [Met Docker Compose](#met-docker-compose-productie) stap 1. Staat PostgreSQL op dezelfde machine als Docker, gebruik dan `host.docker.internal` als host. Laat PostgreSQL dan luisteren op het adres van Docker en sta de Docker-netwerken toe in `pg_hba.conf`. Staat hij op een andere machine, zet dan `;SSL Mode=VerifyFull` achter de verbinding.
2. Laat Portainer het image ophalen. Het image is privé: ga in Portainer naar **Registries** > **Add registry** > **Custom registry**, met als URL `ghcr.io`, je GitHub-gebruikersnaam en als wachtwoord een GitHub-token (classic) met alleen `read:packages`.

**De stack aanmaken**

1. Ga naar **Stacks** > **Add stack** en geef hem een naam, bijvoorbeeld `du-review`.
2. Kies **Repository**:
   - **Repository URL**: `https://github.com/ricardotill/DU_Review`. De repository is privé: zet **Authentication** aan, met je gebruikersnaam en een token met leesrecht op de repository.
   - **Repository reference**: `refs/heads/main`.
   - **Compose path**: `docker-compose.yml`.

   Liever zonder Git? Kies dan **Web editor** en plak de inhoud van `docker-compose.yml`. Een wijziging in de repository moet je dan zelf opnieuw plakken.
3. Zet onder **Environment variables** dezelfde waarden als in `.env.example` (met **Advanced mode** kun je ze in één keer plakken):

   | Variabele | Waarde |
   |---|---|
   | `DATABASE_CONNECTION_STRING` | bijvoorbeeld `Host=host.docker.internal;Database=scoutingdb;Username=du;Password=…` |
   | `SLACK_BOT_TOKEN`, `SLACK_APP_TOKEN` | de tokens uit [Slack koppelen](slack-koppelen.md) |
   | `BOOTSTRAP_ADMIN_EMAIL` | jouw e-mailadres, voor het eerste beheerdersaccount |
   | `PUBLIC_URL` en `SMTP_…` | voor mails over accounts (zie [Mail](#mail)) |
   | `PORT` | optioneel, de poort op de server (standaard `5231`) |
   | `APP_REPLICAS` | optioneel, het aantal instanties (standaard `2`) |
   | `APP_VERSION` | optioneel, een vaste versie, bijvoorbeeld `1.4.0` |
   | `CLOUDFLARE_TUNNEL_TOKEN` en `COMPOSE_PROFILES=tunnel` | optioneel, voor de [Cloudflare Tunnel](#via-internet-cloudflare-tunnel): `COMPOSE_PROFILES` start de container `cloudflared` mee. Verschijnt die niet onder de stack, draai `cloudflared` dan als eigen container in het netwerk `<stacknaam>_web` met adres `172.29.53.10` |

4. Klik op **Deploy the stack**. Kies je wachtwoord via de mail, of zoek de link in het logboek van een `app`-container (**Containers** > container > **Logs**, zoek op `wachtwoord-instellen`).

**Bijwerken**

- Met een stack uit Git kan Portainer zelf bijwerken: zet **GitOps updates** aan (bijvoorbeeld elke 5 minuten) en ook **Re-pull image**. Dan haalt Portainer een gewijzigde `docker-compose.yml` op, en bij elke controle ook een nieuw `latest`-image.
- Met de hand: open de stack en klik op **Pull and redeploy** (bij Git) of **Update the stack** met **Re-pull image and redeploy** aan (bij de editor).
- Zet voor de speeldag `APP_VERSION` op de huidige versie, zodat de app niet midden in het spel herstart. Haal hem daarna weer weg (zie [Updates en versies](#updates-en-versies)).

**Goed om te weten**

- De bestanden staan in het volume `<stacknaam>_app-data`, onder **Volumes**. Alle instanties gebruiken dat ene volume.
- Het puntenbestand komt in Portainer uit het image. Een aanpassing in `opdrachtenpunten.csv` geldt dus vanaf de volgende versie van het image.
- De stack gebruikt een eigen netwerk `172.29.53.0/24`. Is dat bij jou al in gebruik, pas dan het subnet en de adressen van `proxy`, `cloudflared` en `trusted_proxies` samen aan.

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
   - met Docker Compose: `proxy:8080` (de proxy voor de instanties; stond hier eerder `app:8080`, pas het dan aan)
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

Gebruik `--profile tunnel` ook bij `docker compose down` en `docker compose up -d`, anders blijft de tunnel draaien of start hij niet mee. `docker compose restart app` herstart alleen de app; de proxy en de tunnel blijven draaien.

**Waarom de app de tunnel moet vertrouwen**

De tunnel geeft het echte adres van de bezoeker door, en dat het verzoek via HTTPS binnenkwam. De app gebruikt dat voor de grens op mislukte logins en om het login-cookie alleen via HTTPS te versturen. Dat doorgeven vertrouwt de app alleen van een proxy die hij kent:

- `cloudflared` op dezelfde computer als de app (`run-gameday.sh`): vertrouwd, er hoeft niets ingesteld.
- `cloudflared` in Docker Compose: de container heeft een vast adres (`172.29.53.10`). De proxy vertrouwt alleen dat adres (`trusted_proxies` in `docker-compose.yml`), en de app vertrouwt het eigen netwerk van de proxy en de tunnel via `ForwardedHeaders__KnownNetworks__0` in `docker-compose.yml`. Ook hier hoef je niets in te stellen.
- `cloudflared` of een andere proxy op een andere machine: zet het adres van die machine in `ForwardedHeaders:KnownNetworks`, als lijst van netwerken in CIDR-vorm (`"192.168.1.5/32"`).

Klopt dit niet, dan werkt de app nog steeds, maar ziet hij voor iedereen het adres van de proxy en staat bij elke inlog van een groep een waarschuwing over gewone HTTP in het logboek.

**Extra afschermen (optioneel)**

De app vraagt altijd om een account. Wil je het dashboard daarnaast alleen voor bekende mensen openzetten, dan kan dat met **Cloudflare Access** (Zero Trust > Access > Applications), bijvoorbeeld met een eenmalige code per e-mail. Voor groepen is dat meestal een extra drempel die niet nodig is.

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
- [ ] Controleer de tekst van het startbericht: **Configuratie** > **Startbericht in Slack** > **Voorbeeld**. Pas hem zo nodig aan met het potlood (als beheerder).
- [ ] De eindtijd (`Scouting:EventEnd`, of **Configuratie** > **Speeltijd** als beheerder) staat op het einde van het spel. Inzendingen vanaf dat moment tellen niet; de bot reageert met :alarm_clock:. Op de eindtijd plaatst de bot het eindbericht in elk groepskanaal.
- [ ] Controleer de tekst van het eindbericht: **Configuratie** > **Eindbericht in Slack** > **Voorbeeld**.
- [ ] Er is genoeg schijfruimte: reken op enkele GB voor video's.
- [ ] Generale repetitie: vier mensen plaatsen tegelijk, één met een grote video (200 MB of meer). Twee juryleden zetten verschillende emoji's op één bericht. Iemand bewerkt een bericht en iemand verwijdert er een. Herstart de app halverwege en controleer dat niets ontbreekt.
- [ ] Gebruik je een [Cloudflare Tunnel](#via-internet-cloudflare-tunnel)? Open het HTTPS-adres op een telefoon via 4G (niet via de wifi), log in en controleer dat het dashboard live bijwerkt.
- [ ] Elk jurylid heeft een account en heeft ingelogd. Er zijn minstens twee beheerders.
- [ ] Elke groep heeft een account (**Beheer** > **Accounts** toont welke groepen er nog geen hebben), en het wachtwoord is gekozen.
- [ ] Er is één beamer-account, en de beamer is daarmee ingelogd.
- [ ] Werkt mail? Stuur een testmail op **Beheer** > **Mail**. Zo niet, weet dan hoe je met **Link maken** een link doorstuurt.
- [ ] Speel je met Avonturiers? Elke groep heeft een Avonturiers-kanaal met de bot erin, `Scouting:PublicUrl` staat goed, de locaties staan op **Avonturiers → Locaties**, het startaantal open locaties klopt (**Configuratie → Avonturiers**), en de rondes en de tijden van startinformatie en eindlocatie op **Avonturiers → Rondes**. Open een kaartlink van een groep op een telefoon via 4G.
- [ ] Wil je de stand aan het eind verbergen? Plan de periode op **Stand voor groepen**, en kies of de beamer meedoet.

### Op de ochtend van het spel

- [ ] Start de app met `./run-gameday.sh` of `docker compose up -d` (zie [De app starten](#de-app-starten)) en controleer bovenaan de pagina de Slack-status: **Verbonden met Slack**.
- [ ] Kijk op **Gebeurtenissen**: er staat **Verbonden met Slack** en een **Bijwerken**-melding, en geen **Fout in de app**. Houd deze pagina tijdens het spel ergens open.
- [ ] **Handmatige modus** staat uit (geen melding bovenaan).
- [ ] Plaats in elk kanaal een testbericht met een foto en `Opdracht 1` (in een Avonturiers-kanaal `Locatie 1`). Controleer dat het op het dashboard verschijnt, en verwijder het daarna in Slack.
- [ ] Gebruik je een tunnel? Controleer dat hij draait (Cloudflare-dashboard: **Healthy**) en geef groepen alleen het HTTPS-adres.
- [ ] Iedereen weet zijn inlog. Een groep logt alleen in met haar eigen groepsaccount, nooit met een juryaccount: daarmee zie je alles.
- [ ] Zet de diavoorstelling of het dashboard klaar op de beamer.
- [ ] Na de starttijd: elk groepskanaal heeft het startbericht. Op **Configuratie** > **Startbericht in Slack** staat per groep **Geplaatst om …**. Mist er een, of is er geen starttijd ingesteld, klik dan als beheerder op **Startbericht nu sturen**.
- [ ] Na de eindtijd: elk groepskanaal heeft het eindbericht (**Configuratie** > **Eindbericht in Slack**), en bovenaan de app staat **Spel afgelopen**.

### Uitleg aan de groepen (twee minuten)

Alles voor de groepen staat in de [handleiding voor groepen](handleiding-groepen.md). Kort:

- Plaats je opdracht in je eigen kanaal met het nummer erbij: `Opdracht 12`.
- Meerdere foto's in één bericht mag.
- Zet de bot een ❓, bewerk dan je bericht en zet het juiste nummer erin.
- Zet de bot een ⚠️, lees de uitleg in de thread.
- Met jullie groepsaccount zie je in de app jullie score, de stand en jullie inzendingen.

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
- Exporteer de editie op **Beheer** > **Edities** (**Exporteren**) en bewaar de ZIP op een tweede plek: daarmee zet je de hele editie, met foto's, later terug.
- Download de uitslag en de bestanden via **Exporteren** op het **Dashboard** (als beheerder): de scores als CSV, en de awesome of alle goedgekeurde bestanden als ZIP. Per groep kan dat ook met **Downloaden** op de pagina van de groep.
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
| Iemand kan niet meer inloggen | Gebruik **Wachtwoord vergeten?**, of maak op **Beheer** > **Accounts** een link. Is het account geblokkeerd? Na 5 verkeerde wachtwoorden staat een account 15 minuten op slot; na 30 foute pogingen binnen een minuut moet het adres even wachten. |
| Een handmatige aanpassing is verdwenen | Er is opnieuw opgebouwd uit Slack, of een jurylid heeft daarna een emoji gezet. Slack is leidend; zet de emoji in Slack. |
| Een video speelt niet af in het dashboard | Download het bestand via de detailpagina; niet elke browser speelt elk videoformaat af. |
| Een bestand staat in `_deleted` | Het bericht of bestand is in Slack verwijderd. Zet het terug door het opnieuw in Slack te plaatsen. |

Voor problemen met het koppelen van Slack zelf (tokens, rechten, Socket Mode) staat een uitgebreide tabel in [Slack koppelen](slack-koppelen.md#problemen-oplossen).
