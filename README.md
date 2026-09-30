# DU Review

DU Review verwerkt de inzendingen van het **Destination Unknown regiospel**. Groepen plaatsen hun opdrachten als foto, video, geluidsopname of document in hun eigen Slack-kanaal. De jury beoordeelt ze met een emoji. DU Review doet de rest: bestanden opslaan en sorteren, punten tellen, en live een scorebord tonen.

## Hoe het werkt

1. **Groep plaatst een inzending.** Een scout plaatst een bestand in het Slack-kanaal van de groep, met het opdrachtnummer in de tekst, bijvoorbeeld `Opdracht 12`.
2. **De app registreert de inzending.** Het bestand wordt gedownload en opgeslagen als `{afkorting} {opdrachtnummer}`, bijvoorbeeld `CDW 12.jpg`, in de map van de groep.
3. **De jury beoordeelt in Slack.** Een jurylid reageert op het bericht met een emoji. Staan er meerdere bestanden in één bericht, dan geldt de beoordeling voor allemaal.
   - 👍 → **Approved**: het bestand verhuist naar `approved/`;
   - ❤️ ⭐ 🔥 → **Awesome**: het bestand verhuist naar `awesome/`;
   - ❌ → **Rejected**: het bestand verhuist naar `rejected/`.

   Haalt een jurylid de emoji weer weg, dan telt de beste beoordeling die nog op het bericht staat. Is er geen meer, dan wordt de inzending weer **Pending** en gaat het bestand terug naar de groepsmap.
4. **Het dashboard werkt zichzelf bij.** Scores en overzichten worden direct bijgewerkt, zonder de pagina te verversen.

Klopt er iets niet aan een inzending, dan reageert de bot zelf in Slack met een emoji en een uitleg in de thread:

- **Geen opdrachtnummer**, of een nummer dat niet bestaat: de inzending wordt niet opgeslagen. De groep moet hem opnieuw plaatsen.
- **Bestandstype niet ondersteund**, bijvoorbeeld een `.zip`: dat bestand wordt overgeslagen.
- **Bestand kon niet worden gedownload**: dat bestand wordt overgeslagen, en de groep wordt gevraagd het opnieuw te plaatsen. De app slaat nooit een leeg of vervangend bestand op.

Een inzending krijgt de tijd waarop het bericht in Slack is geplaatst, niet de tijd waarop de app het verwerkte.

## Toegang

De app is afgeschermd met een gedeelde **toegangssleutel**. Wie de app opent, moet eerst die sleutel invullen. Daarna blijft de browser 30 dagen ingelogd. Hoofdletters en spaties in de sleutel maken niet uit.

Er is daarnaast een aparte **admin-sleutel**. Alleen in admin-modus kun je:

- de sleutels opnieuw aanmaken;
- **Rebuild from Slack** starten;
- juryleden toevoegen, weghalen en hun namen aanpassen;
- **Manual mode** aan- of uitzetten;
- een status aanpassen met **Manual status override**.

Bekijken, zoeken, filteren en favorieten markeren kan iedereen met de toegangssleutel.

**Waar vind je de sleutels?** Bij de eerste start maakt de app beide sleutels zelf aan. Ze staan dan eenmalig in het logboek, en worden bewaard in `access-keys.json` (zie `Scouting:KeysFile`). Wil je zelf een sleutel kiezen, vul dan `Scouting:AccessKey` of `Scouting:AdminKey` in, bijvoorbeeld via user secrets. Dat geldt alleen zolang `access-keys.json` nog niet bestaat.

**Admin-modus:** ga naar de pagina **Admin** en vul de admin-sleutel in. Inloggen met de admin-sleutel op het loginscherm kan ook. Met **Leave admin mode** ga je terug naar gewone toegang. Bovenaan de pagina staat **Admin** zolang je in admin-modus bent.

**Sleutel opnieuw aanmaken:** op de pagina **Admin** maak je een nieuwe toegangssleutel of admin-sleutel aan. Iedereen die met de oude sleutel is ingelogd, wordt direct uitgelogd, ook in pagina's die al open staan. Jij blijft zelf ingelogd. Een nieuwe admin-sleutel wordt maar één keer getoond: noteer hem meteen.

Na 5 verkeerde sleutels binnen een minuut moet een apparaat even wachten voordat het opnieuw mag proberen.

> **Let op:** de sleutel en het login-cookie gaan onversleuteld over het netwerk als de app via gewone `http://` wordt geopend. Iedereen op hetzelfde netwerk kan ze dan meelezen. Gebruik HTTPS (bijvoorbeeld via een reverse proxy) als de app buiten een vertrouwd netwerk bereikbaar is.

## Favorieten

Klik op de ster ☆ bij een inzending om hem als favoriet te markeren. Dat kan in de lijst **Submissions**, op de pagina van een opdracht en op de detailpagina van een inzending. Met de schakelaar **Favorites only** op **Submissions** zie je alleen je favorieten.

Favorieten worden bewaard in een cookie in je eigen browser (`du_favorites`, een jaar geldig). Ze zijn dus per browser en niet zichtbaar voor anderen. Er passen maximaal 300 favorieten in.

## Manual mode

Wil de organisatie alles zelf beoordelen, zet dan **Manual mode** aan met de schakelaar op het **Dashboard** (alleen in admin-modus). De app doet dan alleen nog dit: van elk bericht met een geldig opdrachtnummer worden de bestanden gedownload en opgeslagen met de juiste naam (bijvoorbeeld `CDW 12.jpg`) in de map van de groep.

Verder gebeurt er niets:

- er worden geen inzendingen geregistreerd, dus het dashboard verandert niet;
- emoji's van de jury worden genegeerd;
- de bot plaatst niets in Slack, ook niet bij een ontbrekend opdrachtnummer of een verkeerd bestandstype.

Bovenaan elke pagina staat dan **Manual mode**. Met `Scouting:ManualMode` in `appsettings.json` stel je in of de app met manual mode aan of uit start; de schakelaar geldt tot de volgende herstart. **Rebuild from Slack** verwerkt berichten altijd volledig, ook als manual mode aan staat.

## Punten

De punten per opdracht staan in `src/ScoutingAutomation.Web/opdrachtenpunten.csv`:

```csv
Opdracht,standaard,awesome
1,1,2
2,2,4
```

- **Approved** geeft de `standaard`-punten, **Awesome** de `awesome`-punten. Pending en Rejected geven 0 punten.
- Elke opdracht telt **één keer per groep**. Heeft een groep een opdracht vaker ingestuurd, dan telt de beste beoordeling.
  - Eerst afgekeurd en daarna goedgekeurd? Dan telt alleen de goedkeuring. De afkeuring is ook niet meer zichtbaar in de statistieken.
- Groepen met evenveel punten delen een plaats in het klassement.

De app leest dit bestand bij elke start. Wijzigingen gelden dus na een herstart.

## Het opdrachtnummer

De app herkent deze schrijfwijzen:

| Voorbeeld | Vorm |
|---|---|
| `Opdracht 12`, `taak 12`, `assignment 12` | trefwoord + nummer |
| `#12` | hekje + nummer |
| `A12` | letter A + nummer |
| `12 - kampvuur`, `12: kampvuur` | nummer aan het begin, gevolgd door een scheidingsteken |

Een los getal ergens in de tekst telt **niet**. Zo wordt "we deden het met 3 man" niet per ongeluk opdracht 3.

## Pagina's

| Pagina | Wat je ziet |
|---|---|
| **Dashboard** | Totalen, het klassement en de voortgang per opdracht, en de schakelaar voor **Manual mode**. |
| **Submissions** | Alle inzendingen, met zoeken en filteren op groep, opdracht, status en jurylid (op naam of Slack-ID), en alleen je favorieten. |
| **Assignments** | Alle opdrachten. Per opdracht een pagina met de inzendingen van alle groepen naast elkaar. |
| **Groups** | Alle groepen. Per groep een pagina met score, plaats en alle opdrachten. |
| **Configuration** | Alle instellingen, de juryleden met hun namen, en **Rebuild from Slack** (zie hieronder). |
| **Admin** | Naar admin-modus gaan, de toegangssleutel bekijken en sleutels opnieuw aanmaken. |
| **Slack Simulator** | Nep-inzendingen en -reacties versturen om te testen. Alleen zichtbaar in simulatiemodus. |

Beoordelen gebeurt in Slack. Voor noodgevallen kan een status op de detailpagina van een inzending handmatig worden aangepast, onder **Manual status override** (alleen in admin-modus). Dit vraagt om een reden en wordt vastgelegd in de beoordelingsgeschiedenis. Een latere emoji in Slack overschrijft zo'n aanpassing weer.

## Opslag

```text
scouting_storage/
  de-wolven/
    CDW 16.jpg        ← nog niet beoordeeld
    approved/
    awesome/
    rejected/
```

Heeft een map al een bestand met dezelfde naam, dan krijgt het nieuwe bestand een volgnummer: `CDW 16 (2).jpg`.

Geaccepteerde bestanden: alle afbeeldingen, video's en geluidsopnames, plus PDF en Word (`.doc`, `.docx`). SVG-bestanden worden geweigerd, omdat die scripts kunnen bevatten.

## Lokaal draaien

Je hebt de [.NET 10 SDK](https://dotnet.microsoft.com/download) nodig.

**Snel, met SQLite:**

```bash
dotnet run --project src/ScoutingAutomation.Web --launch-profile http
```

De app draait dan op <http://localhost:5231>. De database `scouting_dev.db` en de map `scouting_storage/` worden automatisch aangemaakt, met voorbeelddata.

**Met PostgreSQL, via .NET Aspire** (Docker vereist):

```bash
dotnet run --project src/ScoutingAutomation.AppHost
```

Standaard staat de app in **simulatiemodus** (`Scouting:Slack:SimulationMode` = `true`). Er gaat dan niets naar Slack, en je kunt alles testen via de pagina **Slack Simulator**.

Wil je opnieuw beginnen? Stop de app en verwijder `scouting_dev.db*` en de groepsmappen in `scouting_storage/`.

## Configuratie

Alles staat onder `Scouting` in `src/ScoutingAutomation.Web/appsettings.json`. Wijzigingen gelden na een herstart. Juryleden en manual mode kun je daarnaast ook in de app aanpassen, zonder herstart.

| Instelling | Betekenis |
|---|---|
| `Groups` | Per groep: naam, afkorting (voor bestandsnamen), Slack-kanaal-ID en omschrijving. |
| `Reviewers` | Slack member-ID's van de jury. Alleen hun emoji's tellen. Op de pagina **Configuration** kun je er meer toevoegen. |
| `ReviewerNames` | Optioneel: een naam per member-ID, bijvoorbeeld `{ "U04ABC123": "Sanne" }`. Namen die in de app zijn ingevuld of uit Slack zijn opgehaald, gaan voor. |
| `RuntimeSettingsFile` | Waar de in de app toegevoegde juryleden en hun namen worden bewaard (standaard `runtime-settings.json`). |
| `AccessKey`, `AdminKey` | Optioneel: de sleutels bij de eerste start. Leeg laten om ze te laten aanmaken. Zie **Toegang**. |
| `KeysFile` | Waar de sleutels worden bewaard (standaard `access-keys.json`). |
| `ManualMode` | Of de app met manual mode aan start (standaard `false`). |
| `ReviewStatusReactions` | Welke emoji Approved, Awesome of Rejected betekent. |
| `SlackFeedback` | Emoji en tekst waarmee de bot reageert op een probleem. |
| `SupportedExtensions`, `SupportedMimeTypePrefixes` | Welke bestanden worden geaccepteerd. |
| `AssignmentPointsFile` | Het CSV-bestand met de punten. |
| `StorageRoot` | De map waarin de bestanden worden opgeslagen. |
| `Slack` | Simulatiemodus, verbindingsmethode (`SocketMode`) en de Slack-geheimen: bot-token, App-Level Token of Signing Secret. |

Lijsten in `appsettings.json` hebben geen standaardwaarden in de code. Wat in het bestand staat, is dus precies wat de app gebruikt.

## Herbouwen vanuit Slack

Is er iets misgegaan, bijvoorbeeld omdat de app een tijd niet draaide, dan kan de app alle inzendingen opnieuw opbouwen uit de Slack-berichten. Dit staat onderaan de pagina **Configuration**, onder **Rebuild from Slack**. Het werkt alleen in admin-modus en als de app met Slack verbonden is.

Kies het tijdstip vanaf wanneer berichten meetellen (standaard vandaag 00:00) en typ `REBUILD` om te bevestigen. De app:

1. leest alle berichten van alle groepskanalen sinds dat tijdstip, ook antwoorden in threads. Lukt dat voor een of meer kanalen niet, dan stopt de app en blijft alles zoals het was. Per kanaal zie je dan de groep, het kanaal, de foutcode van Slack en wat je eraan kunt doen, bijvoorbeeld de bot uitnodigen bij `not_in_channel`;
2. verplaatst alle groepsmappen naar `scouting_storage/_backup/{tijdstip}/`, samen met een `submissions.json` met alle huidige inzendingen en beoordelingen;
3. verwijdert alle inzendingen en beoordelingen, en verwerkt alle berichten opnieuw, in de volgorde waarin ze geplaatst zijn. Er wordt niets in Slack geplaatst;
4. past de beoordelingen toe zoals ze nu in Slack staan. Slack zegt niet in welke volgorde emoji's zijn geplaatst: zijn juryleden het oneens, dan telt de beste beoordeling (Awesome, dan Approved, dan Rejected);
5. toont een verslag: hoeveel inzendingen en beoordelingen er zijn, welke berichten niet zijn ingelezen en waarom, en waar juryleden het oneens waren.

Nieuwe berichten en reacties die tijdens het herbouwen binnenkomen, wachten tot het klaar is en worden daarna gewoon verwerkt.

Wat verloren gaat: handmatige aanpassingen via **Manual status override** (ze staan nog wel in de backup), en berichten die in Slack zijn verwijderd. Emoji's die later zijn weggehaald tellen niet meer mee, en bewerkte berichten worden gelezen zoals ze nu zijn.

## Koppelen aan Slack

Zie **[docs/slack-koppelen.md](docs/slack-koppelen.md)** voor het aanmaken van de Slack-app, de benodigde rechten, en het instellen van kanalen en jury.

De app kan op twee manieren verbinden:

- **Socket Mode** (aanbevolen): de app maakt zelf een verbinding met Slack. Er is geen publiek adres of HTTPS-certificaat nodig, dus dit werkt ook met alleen een IP-adres. Valt de verbinding weg, dan maakt de app zelf opnieuw verbinding.
- **Events API**: Slack stuurt berichten naar `https://<adres>/api/slack/events`. De app controleert dan bij elk verzoek de handtekening van Slack met het Signing Secret. Verzoeken zonder geldige handtekening, of ouder dan 5 minuten, worden geweigerd. Is er geen Signing Secret ingesteld, dan wordt alles geweigerd.

De verbindingsstatus staat bovenaan elke pagina en op de pagina **Configuration**.

## Projectstructuur

| Map | Inhoud |
|---|---|
| `src/ScoutingAutomation.Web` | Blazor-webapp, de Slack-webhook (`/api/slack/events`) en de configuratie. |
| `src/ScoutingAutomation.Infrastructure` | Verwerking van inzendingen en reacties, opslag, database en Slack-client. |
| `src/ScoutingAutomation.Domain` | Entiteiten (groep, opdracht, inzending, beoordeling) en regels, zoals de beste status en de punten. |
| `src/ScoutingAutomation.Contracts` | Instellingen en Slack-berichtformaten. |
| `src/ScoutingAutomation.AppHost` | .NET Aspire-host met PostgreSQL. |
| `tests/` | Unit- en integratietests. |

Tests draaien:

```bash
dotnet test ScoutingAutomation.slnx
```

Het oorspronkelijke ontwerp staat in `ScoutingAutomation.Complete.SDD.md`.

## Beperkingen

- Iedereen deelt dezelfde toegangssleutel. De app weet dus niet wie iets heeft gedaan; alleen dat het in admin-modus was.
- Zonder HTTPS gaan de sleutel en het login-cookie onversleuteld over het netwerk.
- Live bijwerken werkt binnen één server. Draai je meerdere exemplaren van de app, dan is extra werk nodig.
- Een bewerkt Slack-bericht wordt niet direct opnieuw verwerkt. Een groep moet een correctie als nieuw bericht plaatsen, of de inzendingen worden herbouwd vanuit Slack.
