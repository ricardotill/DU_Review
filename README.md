# 🏕 ️DU Review

DU Review verwerkt de inzendingen van het **Destination Unknown regiospel**. Groepen plaatsen hun opdrachten als foto, video, geluidsopname of document in hun eigen Slack-kanaal. De jury beoordeelt ze met een emoji. DU Review doet de rest: bestanden opslaan en sorteren, punten tellen, en live een scorebord tonen.

Zoek je uitleg voor de speeldag? Er is een handleiding per rol: voor **[groepen](docs/handleiding-groepen.md)**, voor de **[jury](docs/handleiding-jury.md)** en voor de **[organisatie](docs/handleiding-beheer.md)** (opzetten en beheren).

## Hoe het werkt

1. **Groep plaatst een inzending.** Een scout plaatst een bestand in het Slack-kanaal van de groep, met het opdrachtnummer in de tekst, bijvoorbeeld `Opdracht 12`.
2. **De app registreert de inzending.** Het bestand wordt gedownload en opgeslagen als `{afkorting} {opdrachtnummer}`, bijvoorbeeld `CDW 12.jpg`, in de map van de groep.
3. **De jury beoordeelt in Slack.** Een jurylid reageert op het bericht met een emoji. Staan er meerdere bestanden in één bericht, dan geldt de beoordeling voor allemaal.
   - 👍 → **Goedgekeurd**: het bestand verhuist naar `approved/`;
   - ❤️ ⭐ 🔥 → **Awesome**: het bestand verhuist naar `awesome/`;
   - ❌ → **Afgekeurd**: het bestand verhuist naar `rejected/`.

   Zijn juryleden het oneens, dan telt de beste beoordeling op het bericht, in welke volgorde de emoji's ook zijn geplaatst. Haalt een jurylid de emoji weer weg, dan telt de beste beoordeling die nog op het bericht staat. Is er geen meer, dan wordt de inzending weer **Open** en gaat het bestand terug naar de groepsmap.
4. **Het dashboard werkt zichzelf bij.** Scores en overzichten worden direct bijgewerkt, zonder de pagina te verversen.

Klopt er iets niet aan een inzending, dan reageert de bot zelf in Slack met een emoji en een uitleg in de thread:

- **Geen opdrachtnummer**, of een nummer dat niet bestaat: de inzending wordt niet opgeslagen. De groep kan het bericht bewerken en het nummer toevoegen; dan wordt de inzending alsnog geregistreerd en haalt de bot zijn ❓ weg. Opnieuw plaatsen kan ook.
- **Bestandstype niet ondersteund**, bijvoorbeeld een `.zip`: dat bestand wordt overgeslagen.
- **Bestand kon niet worden gedownload**: dat bestand wordt overgeslagen, en de groep wordt gevraagd het opnieuw te plaatsen. De app slaat nooit een leeg of vervangend bestand op.
- **Opdracht al goedgekeurd** (alleen als `BlockAttemptsAfterApproval` aan staat): heeft de groep deze opdracht al goedgekeurd of awesome in een eerder geplaatst bericht, dan wordt de nieuwe poging niet opgeslagen. De bot zet er 🔒 op. Een open of afgekeurde eerdere poging houdt een nieuwe poging niet tegen. Zie **Nieuwe poging na goedkeuring**.

Bewerken en verwijderen in Slack:

- **Opdrachtnummer aangepast** in een bericht dat al is geregistreerd: de inzendingen verhuizen naar de nieuwe opdracht en de bestanden krijgen een nieuwe naam. Een beoordeling blijft staan. Is het nieuwe nummer ongeldig, dan verandert er niets.
- **Bericht verwijderd**: de inzendingen tellen niet meer mee. De bestanden blijven bewaard in `scouting_storage/_deleted/{groep}/`. Hetzelfde geldt voor één bestand dat uit een bericht is verwijderd.

Een inzending krijgt de tijd waarop het bericht in Slack is geplaatst, niet de tijd waarop de app het verwerkte.

Elke groep heeft een eigen wachtrij. Een groot filmpje van de ene groep houdt de inzendingen van andere groepen en de beoordelingen daarop dus niet op. Een bericht dat twee keer binnenkomt, wordt maar één keer geregistreerd.

## Toegang

Iedereen logt in met een eigen **account**: een e-mailadres en een wachtwoord. Daarna blijft de browser 30 dagen ingelogd. Er zijn vier soorten accounts:

| Soort | Wat je ziet en mag |
| --- | --- |
| **Jury** | Alles bekijken, zoeken, filteren en favorieten markeren, de stand voor groepen aan- of uitzetten. |
| **Beheerder** | Alles wat de jury kan, en ook: accounts beheren, **Opnieuw opbouwen uit Slack**, juryleden en bot-teksten aanpassen, **Handmatige modus**, **Status handmatig aanpassen**, extra punten, exporteren. |
| **Groep** | Alleen de eigen groep: dashboard en **Onze inzendingen**. Zie hieronder. |
| **Beamer** | Alleen het **Scorebord** en de **Diavoorstelling**, voor het scherm in de zaal. |

**De eerste beheerder.** Bij de eerste start maakt de app een beheerder voor het adres in `Scouting:Accounts:BootstrapAdminEmail` (bij Docker: `BOOTSTRAP_ADMIN_EMAIL` in `.env`). Die krijgt een mail met een link om een wachtwoord te kiezen. Alleen als er geen mail kan worden verstuurd, staat de link in het logboek. Is er geen beheerder en geen adres, dan start de app niet. Lokaal (`dotnet run`) is de eerste beheerder `admin@du.local`; open de link uit de console, of zet het wachtwoord meteen met `dotnet user-secrets set "Scouting:Accounts:BootstrapAdminPassword" "<wachtwoord>" --project src/ScoutingAutomation.Web`.

**Accounts beheren.** Een beheerder maakt accounts aan op **Beheer** > **Accounts**: naam, e-mailadres, soort en bij een groep welke groep. De app mailt een uitnodiging met een link om een wachtwoord te kiezen; de link is 3 dagen geldig. Met **Link mailen** stuur je een nieuwe; zonder mailserver heet die knop **Link maken** en kopieer je de link zelf. Een account kun je aanpassen, blokkeren en verwijderen. Blokkeren, verwijderen of de soort wijzigen logt dat account binnen een minuut uit, ook in pagina's die al open staan. Je kunt jezelf niet blokkeren of verwijderen, en er blijft altijd minstens één beheerder.

**Wachtwoord.** Minstens 10 tekens; een zin werkt goed. **Wachtwoord vergeten?** op het loginscherm mailt een link om een nieuw wachtwoord te kiezen. Dat werkt alleen als `Scouting:PublicUrl` (`PUBLIC_URL`) is ingesteld: de link komt nooit uit het adres van het verzoek, want dat kan een aanvaller vervalsen. Op **Account** (je naam onderaan het menu) wijzig je je wachtwoord; andere apparaten worden dan uitgelogd. De mailserver stel je in met `Scouting:Email` (bij Docker: de `SMTP_`-regels in `.env`), zie **Instellingen**. Op **Beheer** > **Mail** zie je of mail is ingesteld en stuur je een testmail.

**Verkeerde wachtwoorden.** Na 5 verkeerde wachtwoorden voor één account is dat account 15 minuten op slot. Daarnaast moet een adres na 30 mislukte pogingen binnen een minuut even wachten. Die grens is ruim, omdat alle groepen en de jury op de locatie vaak hetzelfde internetadres delen. Een goede login wist eerdere fouten niet, dus wie zelf een account heeft (zoals een groep) kan daarmee niet onbeperkt wachtwoorden van anderen proberen. Een onbekend adres en een verkeerd wachtwoord krijgen dezelfde melding.

**Groepsaccounts.** Elke groep kan een eigen account krijgen. Een groep ziet alleen:

- een eigen dashboard met haar score, plaats, de stand en de punten in de loop van het spel;
- haar eigen inzendingen (**Onze inzendingen**) met de beoordeling, en de bestanden zelf.

Meldingen van nieuwe inzendingen (rechtsonder) krijgt alleen de jury; een groep ziet zo niet wat andere groepen insturen.

Inzendingen en bestanden van andere groepen, de namen van juryleden, opmerkingen bij beoordelingen en alle pagina's voor de jury (zoals **Te beoordelen**, **Groepen**, **Opdrachten**, **Scorebord** en **Configuratie**) zijn voor een groep afgeschermd; die komt dan op **Geen toegang** uit. Nieuwe pagina's zijn standaard ook afgeschermd: alleen pagina's met `[GroupAccess]` staan open voor groepen (en met `[BeamerAccess]` voor de beamer), en zulke pagina's tonen een groep alleen haar eigen gegevens. Op **Beheer** > **Accounts** staat welke groepen nog geen account hebben. De beamer krijgt geen bestanden van afgekeurde inzendingen.

**Van sleutels naar accounts.** Eerdere versies werkten met een gedeelde toegangssleutel, een beheerderssleutel en groepssleutels. Die bestaan niet meer: `access-keys.json` wordt niet meer gelezen en mag weg. Een bestaande database krijgt bij de eerste start de tabellen voor accounts erbij; inzendingen, punten en instellingen blijven zoals ze waren. Daarna maak je de accounts aan voor de jury, de groepen en de beamer.

> **Let op:** het wachtwoord en het login-cookie gaan onversleuteld over het netwerk als de app via gewone `http://` wordt geopend. Iedereen op hetzelfde netwerk kan ze dan meelezen. Gebruik HTTPS (bijvoorbeeld via een reverse proxy of tunnel) als de app buiten een vertrouwd netwerk bereikbaar is, en geef groepen alleen het HTTPS-adres. Logt een groep toch in via gewone `http://` van een ander apparaat, dan staat er een waarschuwing in het logboek. De app vertrouwt een proxy op dezelfde computer (zoals `cloudflared` of `ngrok`) en de `cloudflared`-container uit `docker-compose.yml`; draait de proxy ergens anders, zet het adres dan in `ForwardedHeaders:KnownNetworks` (CIDR, bijvoorbeeld `"192.168.1.5/32"`), anders ziet de app voor iedereen hetzelfde adres. Zie [Via internet](#via-internet-cloudflare-tunnel).

## Favorieten

Klik op de ster ☆ bij een inzending om hem als favoriet te markeren. Dat kan in de lijst **Inzendingen**, op de pagina van een opdracht en op de detailpagina van een inzending. Met de schakelaar **Alleen favorieten** op **Inzendingen** zie je alleen je favorieten.

Favorieten worden bewaard in een cookie in je eigen browser (`du_favorites`, een jaar geldig). Ze zijn dus per browser en niet zichtbaar voor anderen. Er passen maximaal 300 favorieten in.

## Handmatige modus

Wil de organisatie alles zelf beoordelen, zet dan **Handmatige modus** aan met de schakelaar op het **Dashboard** (alleen voor beheerders). De app doet dan alleen nog dit: van elk bericht met een geldig opdrachtnummer worden de bestanden gedownload en opgeslagen met de juiste naam (bijvoorbeeld `CDW 12.jpg`) in de map van de groep.

Verder gebeurt er niets:

- er worden geen inzendingen geregistreerd, dus het dashboard verandert niet;
- emoji's van de jury worden genegeerd;
- de bot plaatst niets in Slack, ook niet bij een ontbrekend opdrachtnummer of een verkeerd bestandstype.

Bovenaan elke pagina staat dan **Handmatige modus**. De schakelaar geldt voor alle instanties van de app en blijft staan na een herstart. `Scouting:ManualMode` in `appsettings.json` geldt alleen zolang niemand de schakelaar heeft gebruikt. **Opnieuw opbouwen uit Slack** verwerkt berichten altijd volledig, ook als handmatige modus aan staat.

## Nieuwe poging na goedkeuring

Met `Scouting:BlockAttemptsAfterApproval` aan is een opdracht klaar zodra de groep er een goedkeuring of awesome voor heeft. Een nieuwe poging voor die opdracht wordt dan niet opgeslagen. De bot reageert met de emoji en tekst uit `SlackFeedback:AlreadyCompleted`. Zo kan een groep een goedgekeurde opdracht niet blijven insturen in de hoop op awesome.

- Als beheerder zet je de regel aan of uit op **Configuratie**, onder **Beoordelingsregels**. Dat geldt direct en blijft bewaard na een herstart, in de database. **Terug naar appsettings** zet de waarde uit `appsettings.json` terug.
- Alleen een **nieuw** bericht dat **na** de eerste goedgekeurde inzending is geplaatst, wordt geweigerd. Een bericht dat al als inzending in de app staat, wordt nooit geweigerd. Dat geldt ook bij inhalen: een eerdere afgekeurde poging, of een tweede bericht dat al was goedgekeurd, krijgt dan geen 🔒. Een bericht dat de app gemist heeft en dat vóór de goedkeuring is geplaatst, telt gewoon mee; de beste beoordeling telt.
- Verandert een groep met een bewerking het nummer naar een opdracht die al klaar is, dan blijft de inzending bij het oude nummer en reageert de bot op dezelfde manier.
- In handmatige modus geldt de regel niet: dan worden alleen bestanden opgeslagen.
- **Opnieuw opbouwen uit Slack** slaat berichten met de 🔒 van de bot over. Zo komen pogingen die live werden geweigerd er ook bij het herbouwen niet bij.
- Het startbericht zegt wat er geldt via de plaatshouder `{retryRule}`.

## Punten

De opdrachten en hun punten staan in `src/ScoutingAutomation.Web/opdrachtenpunten.csv`:

```csv
Opdracht,standaard,awesome,titel,beschrijving
1,1,2,Kampvuur,Maak een vuur zonder aansteker
2,2,4
A,3,6,Speciale toren,"Bouw een toren, minstens 2 meter hoog"
```

- De eerste kolom is het opdrachtnummer, of voor een **speciale opdracht** één tot drie letters (`A`, `B`, `AB`). Hoofd- en kleine letters maken niet uit. Speciale opdrachten staan in de lijsten na de genummerde.
- `titel` en `beschrijving` zijn optioneel. De titel staat bij de opdracht op de pagina's **Opdrachten**, de opdrachtpagina, de pagina van een groep en in het filter van **Inzendingen**. De beschrijving staat op de opdrachtpagina.
- Komma's of puntkomma's mogen allebei (Nederlandse Excel slaat op met puntkomma's); de eerste regel bepaalt welke. Zet een veld tussen aanhalingstekens als er een komma, puntkomma of regelovergang in staat. Een aanhalingsteken in zo'n veld schrijf je dubbel (`""`).
- **Goedgekeurd** geeft de `standaard`-punten, **Awesome** de `awesome`-punten. **Open** en **Afgekeurd** geven 0 punten.
- Elke opdracht telt **één keer per groep**, ook als er meerdere foto's in één bericht staan. Heeft een groep een opdracht vaker ingestuurd, dan telt de beste beoordeling. Met `BlockAttemptsAfterApproval` aan kan een groep na een goedkeuring niet opnieuw insturen.
  - Eerst afgekeurd en daarna goedgekeurd? Dan telt alleen de goedkeuring. De afkeuring is ook niet meer zichtbaar in de statistieken.
- Groepen met evenveel punten delen een plaats op het scorebord.

De app leest dit bestand bij elke start. Wijzigingen gelden dus na een herstart.

## Het opdrachtnummer

De app herkent deze schrijfwijzen:

| Voorbeeld | Vorm |
|---|---|
| `Opdracht 12`, `taak 12`, `assignment 12` | trefwoord + nummer |
| `#12` | hekje + nummer |
| `A12` | letter A + nummer (dat is opdracht 12, niet opdracht A) |
| `12 - kampvuur`, `12: kampvuur` | nummer aan het begin, gevolgd door een scheidingsteken |
| `Opdracht A`, `opdracht: b`, `#A` | trefwoord of hekje + letter(s), voor een speciale opdracht |

Een los getal ergens in de tekst telt **niet**. Zo wordt "we deden het met 3 man" niet per ongeluk opdracht 3. Een letter telt alleen na een trefwoord of een hekje, en alleen als er geen nummer in de tekst staat: "a photo" of "taak a 12" (opdracht 12) wordt nooit opdracht A. Staan de letters niet in het puntenbestand, bijvoorbeeld bij "opdracht is klaar", dan vraagt de bot om het opdrachtnummer.

## Pagina's

| Pagina | Wat je ziet |
|---|---|
| **Dashboard** | Totalen, het scorebord met **Laatst actief** per groep, de punten in de loop van het spel en de voortgang per opdracht. Als beheerder ook de schakelaar voor **Handmatige modus** en **Exporteren**. Zie **Scorebord en export**. |
| **Te beoordelen** | De open inzendingen per Slack-bericht, de langst wachtende eerst, met de wachttijd. Een klik opent het bericht in de Slack-app; ernaast staat een link naar het bericht in de browser. Zie **Te beoordelen**. |
| **Inzendingen** | Alle inzendingen, met zoeken en filteren op groep, opdracht, status en jurylid (op naam of Slack-ID), en alleen je favorieten. Met de Avonturiers aan ook op soort: Toeristen, check-in of thema. |
| **Opdrachten** | Alle opdrachten. Per opdracht een pagina met de inzendingen van alle groepen naast elkaar. Met **Alleen open inzendingen** beoordeel je één opdracht in één keer; sneltoetsen ← en → gaan naar de vorige en volgende opdracht, **N** naar de volgende met open inzendingen. |
| **Groepen** | Alle groepen. Per groep een pagina met score, plaats, de punten in de loop van het spel (de andere groepen in grijs ter vergelijking) en alle opdrachten. |
| **Scorebord** | De totalen van de inzendingen, het scorebord in grote letters en de punten in de loop van het spel, voor een beamer. |
| **Diavoorstelling** | Alle foto's, video's en geluidsopnames achter elkaar, bijvoorbeeld op een beamer. Afgekeurde inzendingen worden overgeslagen. Kies een groep, een opdracht, alleen awesome of alleen favorieten, en of het scorebord tussendoor komt. |
| **Gebeurtenissen** | Live elke Slack-gebeurtenis (bericht, bewerking, verwijdering, emoji erbij of eraf) met wat de app ermee deed: verwerkt, genegeerd met reden, of mislukt. Ook de verbinding met Slack (verbonden, weggevallen, vernieuwd), elke inhaalronde, en elke fout die de app logt. Valt de verbinding weg, dan staat er een rode balk bovenaan tot hij terug is. Toont de laatste 500 sinds de start van de app; na een herstart begint de lijst leeg. |
| **Avonturiers** | Alleen als het Avonturiers-deel aan staat. **Overzicht**: per groep de goedgekeurde locaties, de vrijgespeelde opdrachten, de rondepunten en het totaal, plus alle rondes met hun uitslag. **Kaart**: alle groepen op één kaart. **Rondes**: de themarondes en de berichten van de bot. **Locaties**: de locaties beheren en importeren. Zie **Avonturiers**. |
| **Slack-simulator** | Nep-inzendingen en -reacties versturen om te testen. Alleen zichtbaar in simulatiemodus. |
| **Configuratie** | Alle instellingen, de juryleden met hun namen, de **Beoordelingsregels**, en **Opnieuw opbouwen uit Slack** (zie hieronder). |
| **Stand voor groepen** | De stand verbergen voor groepen (en desgewenst op de beamer), periodes plannen en een tussenstand publiceren. Zie **Stand verbergen**. |
| **Beheer** | Accounts beheren, mail controleren, extra punten en puntencategorieën. Alleen voor beheerders. |

Een groepsaccount ziet alleen **Dashboard** en **Onze inzendingen**, de beamer alleen **Scorebord** en **Diavoorstelling**. Je naam onderaan het menu opent **Account**. Zie **Toegang**.

Beoordelen gebeurt in Slack. Voor noodgevallen kan een status op de detailpagina van een inzending handmatig worden aangepast, onder **Status handmatig aanpassen** (alleen voor beheerders). Dit vraagt om een reden en wordt vastgelegd in de beoordelingsgeschiedenis. Net als een emoji in Slack geldt de aanpassing voor alle bestanden van hetzelfde bericht: anders zou bij twee foto's de andere foto haar status houden, en omdat de beste beoordeling telt, zouden de punten niet veranderen. Een latere emoji in Slack overschrijft zo'n aanpassing weer.

## Extra punten en puntencategorieën

Punten die buiten Slack worden verdiend, zoals de **Avonturiers** en het **Reisbureau** (pubquiz), vul je als beheerder in op **Beheer**, onder **Extra punten**: één kolom per categorie, één rij per groep. Ze tellen mee in de score, de stand en de grafiek, en blijven staan bij **Opnieuw opbouwen uit Slack**.

Welke categorieën er zijn, staat in `Scouting:BonusCategories`. Elke categorie heeft een `Key` (kleine letters, cijfers en streepjes; de punten worden eronder bewaard), een `Name` en optioneel een `ShortName` voor smalle plekken zoals het **Dashboard**. Ontbreekt de lijst, dan gelden Avonturiers en Reisbureau.

Tijdens het spel pas je de categorieën aan op **Beheer**, onder **Puntencategorieën**: toevoegen, hernoemen en weghalen. Dat geldt direct, voor iedereen, en blijft bewaard in de database. **Terug naar appsettings** zet de lijst uit `appsettings.json` terug. Een categorie weghalen haalt haar punten uit de score, de stand en de export, maar de punten blijven bewaard: voeg je een categorie met dezelfde sleutel later weer toe, dan zijn ze terug.

## Scorebord en export

**Laatst actief.** Het scorebord op het **Dashboard** en de kaarten op **Groepen** tonen wanneer een groep voor het laatst iets in Slack plaatste. Een upload zonder geldig opdrachtnummer telt ook mee. Tijdens het spel (tussen `EventStart` en `EventEnd`) wordt dat geel na 30 minuten zonder upload en rood na een uur. Heeft een groep nog niets ingestuurd, dan telt de tijd vanaf de start. Voor de start en na het einde blijft het grijs.

**Punten in de loop van het spel.** Het **Dashboard** en de pagina **Scorebord** tonen per groep een lijn met de punten. De pagina van een groep toont die groep in kleur en de andere groepen in grijs. Een opdracht telt vanaf het moment waarop de inzending met de beste beoordeling in Slack is geplaatst, niet vanaf het moment van beoordelen. Zo blijft de lijn hetzelfde na **Opnieuw opbouwen uit Slack**. Een open inzending die later wordt goedgekeurd, verschijnt dus achteraf op het tijdstip van plaatsen. Extra punten (zoals **Avonturiers** en **Reisbureau**) staan op het moment dat ze zijn ingevoerd, bij elke wijziging opnieuw: een verhoging is een stap omhoog, een verlaging een stap omlaag. Wijzigingen binnen 5 minuten na elkaar tellen als één stap, zodat een tikfout die je meteen herstelt (100 in plaats van 10) geen piek in de grafiek geeft. Elke groep houdt dezelfde kleur, op alfabetische volgorde van naam; vanaf de negende groep zijn de lijnen grijs.

**Beamer.** De pagina **Scorebord** toont dezelfde totalen als het **Dashboard** en de stand in grote letters, met de resterende speeltijd en de grafiek. De knop rechtsboven zet hem op volledig scherm. In de **Diavoorstelling** kun je met **Scorebord tussendoor** elke 3, 5, 10 of 20 items 15 seconden de tussenstand tonen.

**Exporteren** (alleen voor beheerders), via de knop op het **Dashboard**:

- `/api/export/scores.csv`: het scorebord als CSV, met puntkomma's, zodat Excel het direct goed opent. Elke puntencategorie heeft een eigen kolom;
- `/api/export/files.zip?status=awesome`: alle awesome bestanden;
- `/api/export/files.zip?status=approved`: alle goedgekeurde en awesome bestanden. Op de pagina van een groep doet **Downloaden** hetzelfde voor alleen die groep (`&group={id}`).

De ZIP heeft per groep een map, met daarin `approved/` en `awesome/`. Bestanden die niet meer op schijf staan, worden overgeslagen en in het logboek gemeld.

## Stand verbergen

Op de pagina **Stand voor groepen** kan de jury (ook zonder beheerdersaccount) de stand verbergen voor groepen, bijvoorbeeld in het laatste uur van het spel:

- **Stand verbergen**: een schakelaar die direct werkt, los van de periodes.
- **Periodes**: van–tot, in de tijdzone van het spel. Binnen een periode is de stand vanzelf verborgen; het begin telt mee, het einde niet. Open pagina's wisselen op het moment zelf.
- **Ook verbergen op het scorebord (beamer)**: dan verbergen de pagina **Scorebord** en het scorebord in de **Diavoorstelling** de stand ook. Staat dit uit, dan blijft de beamer de actuele stand tonen.

Zolang de stand verborgen is, ziet een groep geen plaatsen, geen scores van andere groepen en geen grafiek. Haar eigen score, de punten per opdracht en de beoordeling van haar inzendingen blijft ze altijd zien. Het dashboard van de jury toont altijd de actuele stand, met een melding dat hij verborgen is voor groepen.

**Tussenstand.** Met **Tussenstand publiceren** leg je de stand van dat moment vast. Zolang de stand verborgen is, zien groepen (en de beamer, als die ook verbergt) deze tussenstand met het tijdstip erbij, in plaats van niets. De tussenstand verandert daarna niet meer mee; publiceer opnieuw voor een nieuwere, of verwijder hem. Is de stand weer zichtbaar, dan zien groepen gewoon de actuele stand.

Alles op deze pagina wordt bewaard in de database en blijft staan na een herstart en na **Opnieuw opbouwen uit Slack**.

## Te beoordelen

De pagina **Te beoordelen** toont elk Slack-bericht met open inzendingen één keer, want één emoji beoordeelt het hele bericht. De oudste staat bovenaan, met de wachttijd. Die telt live door. Een bericht dat langer wacht dan de herinneringstijd krijgt een gele rand.

- **Klik** op een bericht: het opent in de Slack-app (`slack://channel?team=…&id=…&message=…`). Het pijltje ernaast opent de permalink in de browser, voor als de app niet naar het bericht springt. Het chevron opent de detailpagina.
- De links werken alleen als de app met Slack verbonden is; de app vraagt de workspace één keer op met `auth.test`. In simulatiemodus opent een klik de detailpagina.
- **Telt niet meer**: open berichten van een groep die de opdracht al goedgekeurd of awesome heeft in een ander bericht. Een beoordeling levert dan geen punten op. Ze staan ingeklapt onderaan.
- Filter op groep en opdracht.

De detailpagina van een inzending en de opdrachtpagina hebben dezelfde Slack-links.

## Herinnering voor de jury

Wacht een bericht langer dan `Scouting:ReviewReminder:AfterMinutes` minuten op een beoordeling, dan plaatst de bot één bericht in het jurykanaal (`Scouting:ReviewReminder:ChannelId`). Het bericht noemt de groep, de opdracht en de wachttijd, met een link naar elk bericht. Er staan maximaal 15 berichten in; de rest wordt geteld.

- De app kijkt elke minuut. Elk bericht krijgt één herinnering. Na een herstart kan een bericht er nog één krijgen.
- Berichten die **niet meer tellen** krijgen geen herinnering.
- Als beheerder pas je de tijd aan op **Configuratie**, onder **Beoordelingsregels**. Leeg betekent geen herinneringen. De tijd geldt direct en blijft bewaard na een herstart, in de database.
- Geen kanaal ingesteld, handmatige modus aan, of geen tijd: dan komen er geen herinneringen. In simulatiemodus komt de herinnering alleen in het logboek.
- Nodig de bot uit in het jurykanaal. Er zijn geen extra Slack-rechten nodig.

## Avonturiers

Naast de Toeristen (de opdrachten in het groepskanaal) heeft elke groep **Avonturiers**: lopers die locaties bezoeken en themafoto's maken. Ze plaatsen hun foto's in een tweede Slack-kanaal per groep. Zet het deel aan met `Scouting:Adventurers:Enabled` en geef elke groep een Avonturiers-kanaal (op **Beheer** > **Edities**). Staat het uit, dan negeert de app die kanalen en zijn de Avonturiers-pagina's verborgen.

### Check-ins op locaties

- Een loper plaatst een foto met het locatienummer: `Locatie 7`, `Loc 7`, `L7` of `#7`.
- De app slaat hem op als `{afkorting} L7 {tijd}`, bijvoorbeeld `CDW L7 14-32-05.jpg`, in `{groep}/avonturiers/`.
- De jury beoordeelt met dezelfde emoji als bij de Toeristen. Awesome telt bij de Avonturiers als goedgekeurd; er zijn geen extra punten voor.
- De beste beoordeling telt en elke locatie telt één keer per groep.
- Een nummer dat niet in de lijst van **Locaties** staat, krijgt ❓ en een uitleg. Zolang er nog geen locaties zijn ingevoerd, telt elk nummer.
- Met **Jury waarschuwen** (op **Beheer**, onder **Vrijgespeelde locaties**, of `RequireUnlockedLocation`) krijgt een check-in op een locatie die niet voor die groep is vrijgespeeld 🚧 en een waarschuwing voor de jury in de thread.

### Opdrachten vrijspelen

Elke goedgekeurde locatie speelt één opdracht vrij voor de Toeristen van dezelfde groep. Het aantal wordt steeds uitgerekend uit de beoordelingen, dus het volgt een late beoordeling en een herbouw vanzelf. Verandert het aantal, dan plaatst de bot een bericht in het Toeristen-kanaal ("Jullie Avonturiers hebben opdracht 4 van 12 vrijgespeeld!"). Wordt een goedkeuring teruggedraaid, dan volgt een correctie.

Er zijn twee manieren:

- **Alleen tellen** (standaard): de Toeristen kiezen zelf welke opdracht ze doen, bijvoorbeeld van een lijst op papier.
- **Vaste volgorde**: zet de opdrachten in `UnlockPool`, of op **Configuratie** onder **Avonturiers**. De bot noemt dan de nieuwe opdracht, en een inzending voor een opdracht uit de lijst die nog niet is vrijgespeeld krijgt 🔒, wordt niet opgeslagen en telt niet. Opdrachten die niet in de lijst staan, zijn altijd open.

### Themarondes

Op **Avonturiers → Rondes** stel je de rondes in: nummer, begin, einde, de themanummers en een tekst voor de opening. **Genereer rondes** vult rondes van 30 minuten tussen twee tijden, bijvoorbeeld 12:00 tot 16:30 geeft 9 rondes, met thema N in ronde N. Rondes mogen niet overlappen en een themanummer hoort bij één ronde. De rondes worden bewaard in `runtime-settings.json`.

- Een loper plaatst een foto met `Thema 3`, `T3`, of zoals bij opdrachten `Opdracht 3`, `Opdr 3` of `O3`. In het Toeristen-kanaal blijven die woorden gewoon een opdracht.
- Een thema telt voor een ronde als de **tijd van het Slack-bericht** binnen de ronde valt en het nummer bij die ronde hoort. Wanneer de jury beoordeelt, maakt niet uit.
- Een thema van een andere ronde, of een thema terwijl er geen ronde loopt, wordt opgeslagen maar telt niet. De bot zet er ⌛ op met een uitleg.
- Per ronde telt per groep het aantal verschillende goedgekeurde thema's. De beste groep krijgt 2 punten, de tweede 1. Gelijke groepen delen de plaats en de volgende plaats vervalt: twee groepen op 3 krijgen allebei 2 punten en niemand krijgt 1. Een groep zonder goedgekeurd thema krijgt niets.
- Een ronde staat op **Voorlopig** zolang er nog inzendingen open staan. Rondepunten tellen pas mee als de ronde voorbij is.

De bot plaatst in elk Avonturiers-kanaal de opening bij de start van een ronde, een sluiting aan het einde en de uitslag zodra de jury alles van die ronde heeft beoordeeld ("Uitslag ronde 4: 1. Team A (3), 2. Team B (2)"). Elk kanaal krijgt elk bericht één keer, ook na een herstart. Start de app meer dan een uur na zo'n moment, dan plaatst hij het bericht niet meer zelf. Onder **Berichten per ronde** stuur je een bericht met de hand, eventueel met een foto, of opnieuw naar iedereen.

### Berichten aan de Avonturiers

- **Startinformatie** en **Eindlocatie**: op **Avonturiers → Rondes**, met een tijd waarop de bot ze zelf plaatst, of met de hand (eventueel met een foto). Plaatshouders: `{group}`, `{map}` (de kaartlink van de groep), `{time}` (de eindtijd), `{approved}` en `{rejected}`. Voor `{map}` moet `Scouting:PublicUrl` het adres van de app zijn.
- **Losse berichten**: op **Bericht sturen** kies je Toeristen, Avonturiers of beide, of in één klik **Alle Avonturiers**.

### Kaarten

De kaarten gebruiken [Leaflet](https://leafletjs.com) uit de app zelf en de kaarttegels van OpenStreetMap; er is geen Google-account of API-sleutel nodig. Tegelserver en bronvermelding zijn instellingen (`MapTileUrl`, `MapAttribution`).

- **Locaties** (beheerders): toevoegen met een klik op de kaart, aanpassen, verwijderen, of importeren uit een GeoJSON-, GPX- of CSV-bestand (bijvoorbeeld uit Google My Maps of uMap). Het nummer komt uit een kolom `nummer` of vooraan de naam (`7 Molen`, `Locatie 7 - Molen`). Een CSV heeft kolommen `nummer;naam;lat;lon;hint`; puntkomma's en decimale komma's van Nederlandse Excel mogen.
- **Vrijgespeelde locaties** (op **Beheer**): per groep een vinkje per locatie. Alleen die locaties staan op de kaart van de groep.
- **Kaart van de groep**: `/kaart/{code}`, zonder inloggen; de geheime code in de link is de sleutel. De link staat op de pagina van de groep en komt via `{map}` in de startinformatie. Kleuren: grijs nog niet bezocht, oranje wacht op beoordeling, groen goedgekeurd, rood afgekeurd. De kaart ververst elke halve minuut en toont geen foto's.
- **Avonturiers → Kaart** (jury): alle groepen op één kaart, met een groepsfilter. Een klik op een locatie toont de check-in en de beoordeling.

### Punten

De Avonturiers-punten zijn de rondepunten (maximaal `ThemeMaxPoints`, standaard 14) plus het eindspel (maximaal `FinalMaxPoints`, standaard 14). Het eindspel, en een eventuele startbonus voor groepen met eigen vervoer, vul je met de hand in op **Beheer** onder **Extra punten**, in de categorie `FinalGameCategoryKey` (standaard `avonturiers`). Overal waar die categorie staat (dashboard, stand, export) staat het uitgerekende Avonturiers-totaal; in de grafiek stijgen de rondepunten aan het einde van elke ronde.

## Edities

Elk jaar is een **editie** (bijvoorbeeld "DU 2026"), met haar eigen groepen, opdrachten, locaties en inzendingen. Er is altijd één actieve editie: alleen die verwerkt Slack, en alle pagina's tonen die. Een groep is daarnaast een vast **team** dat elk jaar mee kan doen; een groepsaccount hoort bij het team en komt na het inloggen bij de groep van dat team in de actieve editie. Doet het team niet mee aan de actieve editie, dan ziet het account alleen **Eerdere edities** en **Mijn account**.

Beheerders doen alles op **Beheer** > **Edities**:

- **Nieuwe editie**: naam en jaar. Hij krijgt de opdrachten van de actieve editie, en desgewenst ook de groepen (met hun team en Slack-kanalen). Een nieuwe editie is **in voorbereiding**: ze verwerkt nog geen Slack.
- **Groepen**: per editie toevoegen (een nieuw team, of een team dat al eerder meedeed), wijzigen (naam, afkorting, Slack-kanaal, Avonturiers-kanaal) en weghalen. Een kanaal en een team horen maar bij één groep per editie; volgend jaar mag hetzelfde kanaal weer. Een groep met inzendingen kan niet uit een editie: eerst moeten ze beoordeeld of verwijderd zijn, anders zouden ze mee verdwijnen.
- **Deze editie starten**: de actieve editie wordt gearchiveerd met haar eindstand, de nieuwe wordt actief. Slack, alle pagina's en de bestanden gaan vanaf dan over de nieuwe editie; groepen loggen opnieuw in en komen bij hun groep in de nieuwe editie. Zet daarna de start- en eindtijd op **Configuratie**: de instellingen (teksten, tijden, regels) gelden voor de actieve editie en schuiven niet vanzelf mee.
- Een editie in voorbereiding kan weer weg; een gearchiveerde editie verandert niet meer.

De bestanden van de eerste editie staan in `scouting_storage/` zoals altijd; elke latere editie krijgt een eigen map `scouting_storage/_edities/{editie}/`, zodat een groep die opnieuw meedoet haar bestanden niet mengt met die van vorig jaar.

Op **Eerdere edities** ziet een groep de gearchiveerde edities waaraan haar team meedeed, met haar plaats en punten in de eindstand; de jury ziet daar alle eindstanden.

### Editie exporteren en importeren

Op **Beheer** > **Edities** zet **Exporteren** een editie in één ZIP-bestand (`{editie}.du-editie.zip`): groepen met hun team, opdrachten, locaties, inzendingen met beoordelingen, extra punten, vrijgespeelde opdrachten, de eindstand en alle bestanden (foto's en video's). Van de actieve editie gaat de stand van dat moment mee. Ontbreekt een bestand op schijf, dan gaat de rest gewoon mee en staat het in het logboek.

**Editie importeren** (op **Beheer**) zet zo'n ZIP terug als een **nieuwe, gearchiveerde** editie, in deze of een andere installatie; een editie in voorbereiding blijft in voorbereiding. De actieve editie verandert niet. De bestanden komen in een eigen map onder `_edities/`. Teams blijven hetzelfde team: bestaat het team al, dan ziet de groep de editie bij **Eerdere edities**. Een editie met dezelfde naam als een bestaande wordt geweigerd; hernoem die eerst. Een kapot of onbekend bestand wordt geweigerd zonder dat er iets verandert.

Een export kan groot zijn. Cloudflare laat op een gratis tunnel uploads tot ongeveer 100 MB door; importeer een grotere editie via het lokale adres van de app (bijvoorbeeld `http://localhost:5231`). De app neemt uploads tot 20 GB aan.

De groepen staan sinds edities in de database. `Groups` in `appsettings.json` vult alleen een editie die nog geen groepen heeft (een nieuwe installatie); daarna past de app het niet meer toe.

Een database van voor edities wordt bij de eerste start één editie, genoemd naar het jaar van de eerste inzending. Elke groep wordt een team met hetzelfde id, zodat groepsaccounts blijven werken; inzendingen, punten, locaties en instellingen blijven staan. Bij SQLite staat vooraf een kopie van de database in `scouting_storage/_backup/pre-editions-{tijdstip}.db`; bij PostgreSQL maak je zelf eerst een back-up (`pg_dump`).

## Opslag

```text
scouting_storage/
  de-wolven/
    CDW 16 14-32-05.jpg   ← nog niet beoordeeld
    approved/
    awesome/
    rejected/
    avonturiers/          ← check-ins en thema's, alleen met de Avonturiers aan
      CDW L7 13-05-12.jpg
      approved/
      awesome/
      rejected/
```

Een bestandsnaam bestaat uit de afkorting van de groep, het opdrachtnummer en het tijdstip van het Slack-bericht (`CDW 16 14-32-05.jpg`). Zo staan meerdere pogingen voor dezelfde opdracht, bijvoorbeeld twee afgekeurde, naast elkaar in één map. Het tijdstip is in de tijdzone van `TimeZone` (standaard Nederlandse tijd), ook als de server in een andere tijdzone draait.

Staan er meerdere bestanden in één bericht, dan krijgen ze een volgnummer in de volgorde van Slack: `CDW 16 14-32-05 (1).jpg`, `CDW 16 14-32-05 (2).jpg`, `CDW 16 14-32-05 (3).jpg`. Een bericht met één bestand krijgt geen volgnummer. Heeft een map toch al een bestand met dezelfde naam, dan krijgt het nieuwe bestand er een extra volgnummer bij, bijvoorbeeld `CDW 16 14-32-05 (2).jpg` of `CDW 16 14-32-05 (1) (2).jpg`.

Een bestand wordt eerst onder een tijdelijke naam gedownload en pas na een geslaagde download hernoemd. Valt de verbinding halverwege weg, dan blijft er dus geen half bestand achter. Een download die een minuut lang niets binnenkrijgt, wordt afgebroken, zodat één vastgelopen video de groep niet ophoudt. Een tijdelijk bestand dat na een crash achterbleef, wordt bij de volgende start opgeruimd.

Lukt het opslaan in de database niet nadat een bestand is verplaatst, dan gaat het bestand terug naar waar het was. Ontbreekt een bestand toch, dan zoekt de app het in de andere statusmappen van de groep; hij maakt nooit een vervangend bestand. Bij de start meldt het logboek hoeveel inzendingen geen bestand meer op de schijf hebben.

De groepsmappen komen uit de groepsnamen. Geven twee groepen dezelfde map, dan start de app niet en zegt het logboek welke groepen het zijn. Is het puntenbestand kapot, dan start de app ook niet. Het puntenbestand mag komma's of puntkomma's gebruiken, zoals Nederlandse Excel opslaat.

Wat je in de app instelt (juryleden, regels, teksten, tijden, handmatige modus) staat in de database, in de tabel `RuntimeSettings`. Accounts staan in de tabellen `AspNet…`, de sleutels voor de inlogcookies en wachtwoordlinks in `DataProtectionKeys`. Is de opgeslagen instelling onleesbaar, dan start de app toch, met de waarden uit `appsettings.json`, en zegt het logboek wat er mis is.

Een oudere versie bewaarde de instellingen in `runtime-settings.json`. De eerste start met een lege tabel leest dat bestand één keer in en hernoemt het naar `runtime-settings.json.imported`. Daarna gebruikt de app het bestand niet meer. Is het bestand onleesbaar, dan blijft het staan en meldt het logboek dat.

Geaccepteerde bestanden: alle afbeeldingen, video's en geluidsopnames, plus PDF en Word (`.doc`, `.docx`). SVG-bestanden worden geweigerd, omdat die scripts kunnen bevatten.

## Lokaal draaien

Je hebt de [.NET 10 SDK](https://dotnet.microsoft.com/download) en [Node.js](https://nodejs.org/) (met npm) nodig. Bij de eerste build installeert npm Bootstrap en Bootstrap Icons; die worden naar `wwwroot/lib` gekopieerd.

**Snel, met SQLite:**

```bash
dotnet run --project src/ScoutingAutomation.Web --launch-profile http
```

De app draait dan op <http://localhost:5231>. De database `scouting_dev.db` en de map `scouting_storage/` worden automatisch aangemaakt, met voorbeelddata.

**Met PostgreSQL, via .NET Aspire** (Docker vereist):

```bash
dotnet run --project src/ScoutingAutomation.AppHost
```

**In productie, met Docker Compose** (PostgreSQL en de app in containers; alleen Docker vereist):

```bash
cp .env.example .env   # vul POSTGRES_PASSWORD, SLACK_BOT_TOKEN, SLACK_APP_TOKEN, BOOTSTRAP_ADMIN_EMAIL en eventueel SMTP_… in
docker login ghcr.io   # eenmalig, met een GitHub-token met read:packages
docker compose pull && docker compose up -d
```

Het image komt uit de GitHub Container Registry (`ghcr.io/ricardotill/du_review`, `amd64` en `arm64`); GitHub Actions test elke wijziging en publiceert bij elke release (`v*`-tag) een nieuwe `latest`. Zelf bouwen kan met `docker compose up -d --build`. De app draait dan op <http://localhost:5231>. Gegevens staan in Docker-volumes; zie de [handleiding](docs/handleiding-beheer.md#met-docker-compose-productie) voor de eerste beheerder, mail, back-ups en het overnemen van instellingen.

### Via internet: Cloudflare Tunnel

Om de app via internet op een HTTPS-adres te zetten (bijvoorbeeld `https://du.jouwdomein.nl`), zonder poorten open te zetten, gebruik je een [Cloudflare Tunnel](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/). Maak in het Cloudflare-dashboard een tunnel aan met een public hostname naar `http://localhost:5231` (`run-gameday.sh`) of `http://app:8080` (Docker Compose), en start dan:

```bash
cloudflared tunnel run --token <token>          # naast ./run-gameday.sh (brew install cloudflared)
docker compose --profile tunnel up -d --build   # met CLOUDFLARE_TUNNEL_TOKEN in .env
cloudflared tunnel --url http://localhost:5231  # snel testen: tijdelijk trycloudflare.com-adres, zonder account
```

Voor zes groepen en de jury is een gratis tunnel ruim genoeg; WebSockets (voor het live dashboard) werken, en bestanden komen via Slack binnen, dus de uploadgrens van Cloudflare speelt alleen een rol bij [Editie importeren](#editie-exporteren-en-importeren). Gebruik op de speeldag een vaste tunnel met eigen domein: een snelle tunnel krijgt bij elke start een nieuw adres. Stap voor stap staat in de [handleiding](docs/handleiding-beheer.md#via-internet-cloudflare-tunnel).

Om zonder Slack te testen, zet je **simulatiemodus** aan met `Scouting:Slack:SimulationMode` = `true`, bijvoorbeeld in `appsettings.Development.json`. Er gaat dan niets naar Slack, en je kunt alles testen via de pagina **Slack-simulator**. Zonder die instelling staat simulatiemodus uit.

Wil je opnieuw beginnen? Stop de app en verwijder `scouting_dev.db*` en de groepsmappen in `scouting_storage/`.

## Meerdere instanties

Met PostgreSQL kun je de app op meer dan één server of container tegelijk laten draaien, op dezelfde database. Dat werkt zo:

- **Eén instantie praat met Slack.** De instanties kiezen via de database een leider. Alleen die houdt de Socket Mode-verbinding open, haalt berichten in en stuurt de geplande berichten (startbericht, eindbericht, herinneringen, rondes). Valt de leider weg, dan neemt binnen een paar seconden een andere het over en haalt die de gemiste berichten in. Bovenaan staat op de andere instanties **Slack via andere instantie**.
- **Wijzigingen zie je overal direct.** Een beoordeling, een instelling, een account of een nieuwe editie op de ene instantie verschijnt meteen op de pagina's van de andere.
- **Niets loopt door elkaar.** Wat per kanaal na elkaar moet (Slack-berichten, statuswijzigingen op het dashboard), herbouwen uit Slack, edities en accounts wachten op elkaar, ook tussen instanties.
- **Inloggen werkt op elke instantie.** De sleutels voor de inlogcookies staan in de database.
- Bij de start richt één instantie tegelijk de database in; de andere wacht daarop.

Wat je zelf moet regelen:

- **Sticky sessions.** Een pagina houdt een vaste verbinding open met één instantie. Laat de load balancer een bezoeker daarom steeds naar dezelfde instantie sturen (sticky sessions of session affinity, bijvoorbeeld op een cookie). Zonder dat vallen pagina's steeds weg.
- **Gedeelde opslag.** Alle instanties moeten dezelfde map voor `StorageRoot` zien, bijvoorbeeld een netwerkschijf. Anders staat een bestand alleen op de instantie die het downloadde.
- **Webhook.** Gebruik je de Events API in plaats van Socket Mode, dan verwerkt elke instantie wat bij haar binnenkomt. Dat is veilig, maar alleen de leider haalt in en stuurt geplande berichten.

Met SQLite draait de app altijd als één instantie. Per instantie zijn alleen het logboek op **Gebeurtenissen** en de rem op te veel mislukte inlogpogingen per adres; de blokkade van een account na te veel pogingen geldt overal.

## Configuratie

Alles staat onder `Scouting` in `src/ScoutingAutomation.Web/appsettings.json`. Wijzigingen gelden na een herstart. Juryleden, handmatige modus en meer kun je daarnaast ook in de app aanpassen, zonder herstart. Dat bewaart de app in de database.

| Instelling | Betekenis |
|---|---|
| `Groups` | De groepen voor een nieuwe installatie: per groep naam, afkorting (voor bestandsnamen), Slack-kanaal-ID, optioneel het kanaal van de Avonturiers (`AdventurersSlackChannelId`) en omschrijving. Elk kanaal mag maar bij één groep horen; anders start de app niet. Alleen gebruikt zolang de actieve editie geen groepen heeft; daarna beheer je groepen op **Beheer** > **Edities**. |
| `Reviewers` | Slack member-ID's van de jury. Alleen hun emoji's tellen. Op de pagina **Configuratie** kun je er meer toevoegen. |
| `ReviewerNames` | Optioneel: een naam per member-ID, bijvoorbeeld `{ "U04ABC123": "Sanne" }`. Namen die in de app zijn ingevuld of uit Slack zijn opgehaald, gaan voor. |
| `RuntimeSettingsFile` | Alleen voor overstappen van een oudere versie: het `runtime-settings.json` dat de eerste start één keer in de database inleest (standaard `runtime-settings.json`). |
| `Accounts` | `BootstrapAdminEmail`: het adres van de eerste beheerder, gemaakt bij de eerste start als er nog geen beheerder is. `BootstrapAdminPassword`: optioneel het wachtwoord daarvan, alleen voor lokaal testen (via user secrets, nooit in een bestand in Git). Zie **Toegang**. |
| `Email` | De mailserver voor uitnodigingen en **Wachtwoord vergeten?**: `Host`, `Port` (standaard 587), `Security` (`StartTls`, `SslOnConnect` of `None`), `Username`, `Password`, `FromAddress` en `FromName` (standaard `DU Review`). Zonder `Host` mailt de app niets en kopieert een beheerder de links zelf. |
| `ManualMode` | Of handmatige modus aan staat zolang niemand de schakelaar in de app heeft gebruikt (standaard `false`). |
| `ReviewStatusReactions` | Welke emoji Goedgekeurd (`Approved`), Awesome (`Awesome`) of Afgekeurd (`Rejected`) betekent. |
| `SlackFeedback` | Emoji en tekst waarmee de bot reageert op een probleem. `AlreadyCompleted` is de reactie op een nieuwe poging na goedkeuring (`{number}` = opdrachtnummer). `TooLate` is de reactie op een inzending na de eindtijd (`{end}` = eindtijd, bijvoorbeeld `17:00`). |
| `BlockAttemptsAfterApproval` | Of een groep een opdracht die al goedgekeurd of awesome is niet opnieuw mag insturen. Ook in de app aan te passen. Zie **Nieuwe poging na goedkeuring**. |
| `BonusCategories` | De categorieën voor extra punten, met `Key`, `Name` en optioneel `ShortName`. Ontbreekt de lijst, dan gelden Avonturiers (`avonturiers`) en Reisbureau (`reisbureau`). Ook in de app aan te passen. Zie **Extra punten en puntencategorieën**. |
| `ReviewReminder` | `ChannelId`: het jurykanaal. `AfterMinutes`: na hoeveel minuten wachten de jury een herinnering krijgt (`null` = geen). De tijd is ook in de app aan te passen. Zie **Herinnering voor de jury**. |
| `GameStartAnnouncement` | Het startbericht dat de bot bij de start van het spel in elk groepskanaal plaatst. `Message` is de tekst (Slack-opmaak; leeg = geen bericht). Plaatshouders: `{group}` (naam van de groep), `{retryRule}` (of opnieuw insturen na een goedkeuring mag, volgens `BlockAttemptsAfterApproval`), `{approved}`, `{awesome}`, `{rejected}` (de emoji uit `ReviewStatusReactions`) en `{missingNumber}`, `{unknownNumber}`, `{unsupportedFile}`, `{downloadFailed}`, `{alreadyCompleted}` (de emoji uit `SlackFeedback`). Met `AutoPost` (standaard `true`) plaatst de app het bericht zelf op `EventStart`. Zie **Startbericht**. |
| `GameEndAnnouncement` | Het eindbericht dat de bot bij het einde van het spel in elk groepskanaal plaatst. `Message` is de tekst (leeg = geen bericht). Plaatshouders: `{group}` (naam van de groep) en `{end}` (de eindtijd). Met `AutoPost` (standaard `true`) plaatst de app het bericht zelf op `EventEnd`. Zie **Eindtijd en eindbericht**. |
| `Adventurers` | Het Avonturiers-deel. `Enabled` zet het aan. `RequireUnlockedLocation`, `UnlockPool`, `Rounds`, de teksten en de tijden van `StartAnnouncement` en `FinalAnnouncement` (`PostAt`, `AutoPost`) zijn ook in de app aan te passen. Verder: `ThemeMaxPoints`, `FinalMaxPoints`, `FinalGameCategoryKey`, `RoundMinutes` (lengte van een gegenereerde ronde), `Feedback` (emoji en tekst van de bot), `MapTileUrl` en `MapAttribution`. Zie **Avonturiers**. |
| `PublicUrl` | Het adres waarop de app van buiten bereikbaar is, bijvoorbeeld `https://du.example.nl`. Nodig voor de kaartlinks in de berichten aan de Avonturiers en de links in mails over accounts. |
| `SupportedExtensions`, `SupportedMimeTypePrefixes` | Welke bestanden worden geaccepteerd. |
| `AssignmentPointsFile` | Het CSV-bestand met de opdrachten, hun punten en optioneel titel en beschrijving. Zie **Punten**. |
| `StorageRoot` | De map waarin de bestanden worden opgeslagen. |
| `TimeZone` | De tijdzone van het spel voor bestandsnamen en tijden op de pagina's (standaard `Europe/Amsterdam`). |
| `EventStart` | De starttijd van het spel in de tijdzone van `TimeZone`, bijvoorbeeld `"2026-10-03T09:00"`. Slack-berichten van daarvoor worden stil genegeerd: geen inzending en geen feedback, ook niet bij inhalen of opnieuw opbouwen. Leeg (`null`) telt elk bericht mee. Ook in de app aan te passen. |
| `EventEnd` | De eindtijd van het spel in de tijdzone van `TimeZone`, bijvoorbeeld `"2026-10-03T17:00"`. Inzendingen vanaf dat moment tellen niet. Leeg (`null`) betekent geen eindtijd. Ook in de app aan te passen. Zie **Eindtijd en eindbericht**. |
| `ForwardedHeaders:KnownNetworks` | Buiten `Scouting`, optioneel: netwerken (CIDR) van een proxy op een andere machine of container, waarvan de app het doorgegeven bezoekersadres en HTTPS vertrouwt. Een proxy op dezelfde computer is altijd vertrouwd. Zie [Via internet](#via-internet-cloudflare-tunnel). |
| `Slack` | Simulatiemodus, verbindingsmethode (`SocketMode`) en de Slack-geheimen: bot-token, App-Level Token of Signing Secret. `SimulationMode` staat standaard uit; in simulatiemodus controleert de app de handtekening van Slack niet, dus zet hem nooit aan op een server die van buiten bereikbaar is. |

Lijsten in `appsettings.json` hebben geen standaardwaarden in de code, behalve `BonusCategories` als die helemaal ontbreekt. Wat in het bestand staat, is dus precies wat de app gebruikt.

## Startbericht

Bij de start van het spel plaatst de bot in elk groepskanaal één bericht: inzenden kan nu, zo stuur je een inzending in, en wat de emoji betekenen. De tekst staat in `Scouting:GameStartAnnouncement:Message`, en is in de app aan te passen (zie **Teksten van de bot aanpassen**).

- Met een starttijd (`EventStart`) en `AutoPost` aan plaatst de app het bericht zelf op de starttijd.
- Start de app later dan een uur na de starttijd, dan plaatst hij het bericht niet meer zelf. Een laat "het spel is begonnen" zou alleen verwarren.
- Op de pagina **Configuratie** staat onder **Startbericht in Slack** een voorbeeld, per groep of het bericht al is geplaatst, en een knop om het met de hand te sturen. Dat werkt alleen als beheerder en als de app met Slack verbonden is.
- Elk kanaal krijgt het bericht één keer, ook na een herstart. De app onthoudt de geplaatste kanalen in de database. Opnieuw sturen naar alle kanalen kan met de hand, na bevestiging.
- In simulatiemodus komt het bericht alleen in het logboek.

## Eindtijd en eindbericht

Met een eindtijd (`EventEnd`) stopt het spel op een vast moment.

- Een inzending die vanaf de eindtijd in Slack wordt geplaatst, telt niet en wordt niet opgeslagen. De bot reageert met `:alarm_clock:` en een uitleg in de thread (`SlackFeedback:TooLate`).
- De tijd van het Slack-bericht beslist, niet het moment van verwerken. Een bericht dat op tijd was, telt dus ook als de app het later inhaalt, of als de groep het opdrachtnummer na de eindtijd toevoegt.
- De jury kan na de eindtijd gewoon doorgaan met beoordelen.
- Bij de eindtijd plaatst de bot in elk groepskanaal één eindbericht (`GameEndAnnouncement`). Dat werkt net als het startbericht: niet zelf als de app pas meer dan een uur na de eindtijd start, met de hand te sturen onder **Eindbericht in Slack**, en elk kanaal krijgt het één keer.
- Bovenaan elke pagina staat de stand van het spel: **Wacht op start** voor de starttijd, **Spel bezig** tijdens het spel en **Spel afgelopen** na de eindtijd. Zonder start- en eindtijd staat er niets. De diavoorstelling toont rechtsboven hoe lang het spel nog duurt.

### Start- en eindtijd in de app

Als beheerder pas je op de pagina **Configuratie** onder **Speeltijd** de start- en eindtijd aan terwijl de app draait. De tijden gaan in de tijdzone van `TimeZone`.

- Een nieuwe tijd geldt direct. Het start- of eindbericht volgt de nieuwe tijd.
- Een leeg veld opslaan betekent: geen start- of eindtijd.
- De eindtijd moet na de starttijd liggen.
- De tijden blijven bewaard na een herstart, in de database. **Terug naar appsettings** zet een tijd weer op die uit `appsettings.json`.

## Teksten van de bot aanpassen

Als beheerder pas je op de pagina **Configuratie** de teksten van de bot aan terwijl de app draait: het startbericht (onder **Startbericht in Slack**), het eindbericht (onder **Eindbericht in Slack**) en de zes antwoorden in de thread (onder **Automatische feedback**). Klik op het potlood, wijzig de tekst en sla op.

- De tekst bewerk je zoals hij in Slack komt, met dezelfde editor als **Bericht sturen**: knoppen voor vet, schuin, doorgehaald, code, links en opsommingen, en 🙂 voor emoji. Opgeslagen wordt Slack-opmaak (`*vet*`, `_schuin_`). Plaatshouders zoals `{group}` en emoji-codes zoals `:thumbsup:` typ je gewoon als tekst. Sla je op zonder iets te wijzigen, dan blijft de tekst precies zoals hij was.
- Een nieuwe tekst geldt direct voor het volgende bericht van de bot. Wat al in Slack staat, verandert niet.
- De teksten blijven bewaard na een herstart, in de database. Ze gaan voor de teksten in `appsettings.json`.
- **Terug naar appsettings** zet een tekst weer op die uit `appsettings.json`.
- Een lege tekst betekent: geen bericht. De emoji van de bot staan in `appsettings.json` en zijn niet in de app aan te passen.
- Heb je het startbericht eerder in de app aangepast, dan staat de plaatshouder `{retryRule}` er niet vanzelf in. Klik op **Terug naar appsettings**, of voeg hem zelf toe.

## Inhalen na een onderbreking

Slack stuurt een bericht of emoji maar één keer naar de app. Draaide de app even niet, of was de verbinding weg, dan haalt de app zelf in wat hij heeft gemist. Dat gebeurt bij het opstarten, en met Socket Mode ook na elke nieuwe verbinding met Slack. De app:

1. leest de berichten van vandaag uit alle groepskanalen, ook antwoorden in threads. Met de Avonturiers aan ook hun kanalen, die eerst, omdat hun check-ins bepalen welke opdrachten zijn vrijgespeeld;
2. registreert inzendingen die nog ontbreken, en geeft de groep de gewone feedback als er iets niet klopt. Een bericht dat al feedback kreeg, krijgt die niet nog een keer;
3. zet de beoordelingen gelijk aan de emoji's die nu in Slack staan. Is een emoji weggehaald, dan wordt de inzending weer **Open**. Een **Status handmatig aanpassen** blijft staan zolang er in Slack geen beoordeling op het bericht staat.

Er wordt niets weggegooid. **Gebeurtenissen** en het logboek melden wat er is ingehaald. Kan een kanaal of bericht niet worden gelezen, dan gaat de rest gewoon door. Voordat de app een beoordeling verandert, leest hij de emoji's op dat bericht opnieuw uit Slack. Zo draait het inhalen nooit een beoordeling terug die een jurylid net heeft geplaatst. Inhalen en live berichten van één kanaal wachten op elkaar, net als **Status handmatig aanpassen**. In handmatige modus wordt niet ingehaald, omdat de app dan alle bestanden opnieuw zou opslaan. Herbouw dan vanuit Slack.

## Herbouwen vanuit Slack

Is er iets misgegaan, bijvoorbeeld omdat de app een tijd niet draaide, dan kan de app alle inzendingen opnieuw opbouwen uit de Slack-berichten. Dit staat onderaan de pagina **Configuratie**, onder **Opnieuw opbouwen uit Slack**. Het werkt alleen als beheerder en als de app met Slack verbonden is.

Kies het tijdstip vanaf wanneer berichten meetellen (standaard vandaag 00:00) en typ `OPBOUWEN` om te bevestigen. De app:

1. leest alle berichten van alle groepskanalen sinds dat tijdstip, ook antwoorden in threads, en met de Avonturiers aan ook hun kanalen. Lukt dat voor een of meer kanalen niet, dan stopt de app en blijft alles zoals het was. Per kanaal zie je dan de groep, het kanaal, de foutcode van Slack en wat je eraan kunt doen, bijvoorbeeld de bot uitnodigen bij `not_in_channel`;
2. verplaatst alle groepsmappen naar `scouting_storage/_backup/{tijdstip}/`, samen met een `submissions.json` met alle huidige inzendingen en beoordelingen;
3. verwijdert alle inzendingen en beoordelingen, en verwerkt alle berichten opnieuw, in de volgorde waarin ze geplaatst zijn. Er wordt niets in Slack geplaatst, ook geen bericht over vrijgespeelde opdrachten;
4. past de beoordelingen toe zoals ze nu in Slack staan. Slack zegt niet in welke volgorde emoji's zijn geplaatst: zijn juryleden het oneens, dan telt de beste beoordeling (Awesome, dan Goedgekeurd, dan Afgekeurd);
5. toont een verslag: hoeveel inzendingen en beoordelingen er zijn, welke berichten niet zijn ingelezen en waarom, en waar juryleden het oneens waren.

Nieuwe berichten en reacties die tijdens het herbouwen binnenkomen, wachten tot het klaar is en worden daarna gewoon verwerkt.

Wat verloren gaat: handmatige aanpassingen via **Status handmatig aanpassen** (ze staan nog wel in de backup), en berichten die in Slack zijn verwijderd. Emoji's die later zijn weggehaald tellen niet meer mee, en bewerkte berichten worden gelezen zoals ze nu zijn.

## Koppelen aan Slack

Zie **[docs/slack-koppelen.md](docs/slack-koppelen.md)** voor het aanmaken van de Slack-app, de benodigde rechten, en het instellen van kanalen en jury.

De app kan op twee manieren verbinden:

- **Socket Mode** (aanbevolen): de app maakt zelf een verbinding met Slack. Er is geen publiek adres of HTTPS-certificaat nodig, dus dit werkt ook met alleen een IP-adres. Valt de verbinding weg, dan maakt de app zelf opnieuw verbinding. Elke 15 seconden controleert de app de verbinding, zodat ook een verbinding die stil wegvalt (wifi weg, laptop in slaap) wordt opgemerkt. Met Socket Mode is het webhookadres `/api/slack/events` uitgeschakeld.
- **Events API**: Slack stuurt berichten naar `https://<adres>/api/slack/events`. De app controleert dan bij elk verzoek de handtekening van Slack met het Signing Secret. Verzoeken zonder geldige handtekening, of ouder dan 5 minuten, worden geweigerd. Is er geen Signing Secret ingesteld, dan wordt alles geweigerd.

De verbindingsstatus staat bovenaan elke pagina en op de pagina **Configuratie**.

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

De tests zijn per onderwerp in mappen verdeeld:

- **Unittests** (`tests/ScoutingAutomation.UnitTests`): `Workflow` (verwerken van berichten en reacties), `Slack` (Slack-client, Socket Mode, inhalen, herbouwen), `Storage` (bestanden en bestandsnamen), `Data` (database en repositories), `Model` (entiteiten en punten), `Settings` (instellingen, sleutels, juryleden, tijdzone), `Web` (inloggen en favorieten) en `Components` (pagina's).
- **Integratietests** (`tests/ScoutingAutomation.IntegrationTests`): `Slack`, `Workflow`, `Web` en `Hosting` (opstarten).

Het oorspronkelijke ontwerp staat in `ScoutingAutomation.Complete.SDD.md`.

## Beperkingen

- Een groep heeft één account; binnen een groep delen de leden dat wachtwoord.
- Zonder HTTPS gaan het wachtwoord en het login-cookie onversleuteld over het netwerk.
- Live bijwerken werkt binnen één server. Draai je meerdere exemplaren van de app, dan is extra werk nodig.
- Inhalen kijkt alleen naar berichten van vandaag. Een bericht dat is verwijderd of waarvan het nummer is aangepast terwijl de app weg was, wordt niet bijgewerkt. Herbouw in dat geval vanuit Slack.
