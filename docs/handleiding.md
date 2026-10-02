# Handleiding DU Review

Laatst bijgewerkt: 2 oktober 2026

## Zo werkt het

Groepen plaatsen hun opdrachten in Slack, de jury beoordeelt ze daar met een emoji, en DU Review doet de rest: bestanden opslaan, punten tellen en een live scorebord tonen. Slack is altijd leidend: wat in Slack staat, telt.

1. Een scout plaatst een foto, video, geluidsopname of document in het Slack-kanaal van de eigen groep, met het opdrachtnummer in de tekst.
2. De app downloadt het bestand en slaat het op in de map van de groep, met een vaste naam zoals `CDW 12 14-32-05.jpg`.
3. Een jurylid reageert in Slack met een emoji: goedgekeurd, awesome of afgekeurd.
4. De app verplaatst het bestand naar de juiste map en werkt het scorebord direct bij.

Klopt er iets niet, bijvoorbeeld een ontbrekend opdrachtnummer, dan reageert de bot zelf in Slack met een emoji en een uitleg in de thread.

Deze handleiding heeft een deel per rol: groepen, jury, iedereen die het dashboard bekijkt, en de organisatie (beheer).

## Voor de groepen: een opdracht insturen

Zet het opdrachtnummer in je bericht en voeg het bestand toe, in het kanaal van je eigen groep. Meer is niet nodig.

1. Open het Slack-kanaal van je groep.
2. Voeg een of meer bestanden toe: foto, video, geluidsopname, PDF of Word-document.
3. Typ het opdrachtnummer in de tekst, bijvoorbeeld `Opdracht 12`.
4. Verstuur het bericht.

### Zo schrijf je het opdrachtnummer

| Voorbeeld | Werkt |
| --- | --- |
| `Opdracht 12`, `taak 12`, `assignment 12` | ja |
| `#12` | ja |
| `A12` | ja |
| `12 - kampvuur`, `12: kampvuur` | ja, als het nummer vooraan staat |
| `we deden het met 12 man` | nee: een los getal in de tekst telt niet |

### Goed om te weten

- **Meerdere bestanden in één bericht** mag. Ze tellen allemaal voor dezelfde opdracht en krijgen een volgnummer: `(1)`, `(2)`, `(3)`.
- **Een opdracht opnieuw insturen** mag, tot de jury hem heeft goedgekeurd of awesome gegeven. Daarna is de opdracht klaar: een nieuwe poging wordt niet opgeslagen en de bot zet er een 🔒 op. Per opdracht telt de beste beoordeling van je groep. (De organisatie kan de regel uitzetten; dan mag opnieuw insturen altijd. Het startbericht in Slack zegt wat er geldt.)
- **Nummer vergeten of verkeerd getypt?** Bewerk je bericht in Slack en zet het juiste nummer erin. De app pakt de wijziging op. Opnieuw plaatsen kan ook.
- **Bericht verwijderd** in Slack? Dan telt die inzending niet meer mee.
- **Niet geaccepteerd**: zip-bestanden, SVG-afbeeldingen en andere bestandstypen dan hierboven.

### Als de bot reageert

De bot zet een emoji op je bericht en legt in de thread uit wat er mis is.

| De bot zet | Betekenis | Wat jij doet |
| --- | --- | --- |
| ❓ | Geen opdrachtnummer gevonden, of die opdracht bestaat niet | Bewerk je bericht en zet het juiste nummer erin |
| ⚠️ bij "niet ondersteund" | Een bestand heeft een type dat niet telt | Stuur het als foto, video, geluid, PDF of Word |
| ⚠️ bij "kon niet worden opgehaald" | Het bestand kwam niet goed binnen | Plaats het bestand opnieuw |
| 🔒 | Deze opdracht is al goedgekeurd; de nieuwe poging is niet opgeslagen | Niets, de opdracht is klaar. Ga door met een andere opdracht |

Geen reactie van de bot betekent dat alles goed is ontvangen. De beoordeling van de jury zie je als emoji van een jurylid op je bericht.

## Voor de jury: beoordelen in Slack

Je beoordeelt een inzending door een emoji op het bericht van de groep te zetten. Alleen emoji's van juryleden tellen; de organisatie zet je Slack-account op de jurylijst.

| Beoordeling | Emoji (Slack-naam) | Punten |
| --- | --- | --- |
| Goedgekeurd | 👍 `:thumbsup:` · ✅ `:white_check_mark:` · ✔️ `:heavy_check_mark:` | standaardpunten van de opdracht |
| Awesome | ❤️ `:heart:` · ⭐ `:star:` · 🔥 `:fire:` · 🏆 `:trophy:` | awesome-punten van de opdracht |
| Afgekeurd | ❌ `:x:` · 👎 `:-1:` · 🚫 `:no_entry_sign:` | 0 |

Nog niet beoordeeld heet **Open** en geeft 0 punten.

### Zo beoordeel je

1. Open **Te beoordelen** in het dashboard. Daar staan alle open inzendingen, de langst wachtende eerst. Klik op een inzending: het bericht opent in de Slack-app. Springt de app niet naar het bericht, gebruik dan het pijltje ernaast; dat opent het bericht in de browser.
2. Bekijk het bestand in Slack.
3. Zet één beoordelings-emoji op het bericht.
4. Klaar. Het scorebord werkt binnen een paar seconden bij.

### Regels die je moet kennen

- **Eén emoji geldt voor het hele bericht.** Staan er drie foto's in, dan krijgen ze alle drie dezelfde beoordeling. Wil je één bestand anders beoordelen, vraag dan de organisatie om een handmatige aanpassing.
- **Oneens met een ander jurylid?** Dan telt de beste beoordeling op het bericht, in welke volgorde de emoji's ook zijn gezet. Awesome gaat voor Goedgekeurd, Goedgekeurd gaat voor Afgekeurd. Overleg dus eerst als je een beoordeling naar beneden wilt halen, en vraag het andere jurylid de emoji weg te halen.
- **Vergist?** Haal je emoji weg en zet de goede. Haal je de enige beoordeling weg, dan is de inzending weer Open.
- **Andere emoji's tellen niet.** Een 👀 of 😂 mag gewoon; de app negeert ze. Ook emoji's van scouts of andere leiding tellen niet.
- **Berichten van juryleden zijn nooit een inzending.** Je kunt dus gewoon in de kanalen reageren, ook met een bestand.
- **Per opdracht telt de beste inzending van een groep.** Keur je een eerdere poging af en een latere goed, dan telt alleen de goedkeuring.
- **Na een goedkeuring is de opdracht klaar.** Een nieuwe poging van die groep wordt geweigerd (🔒) en komt niet in de lijst. Stonden er twee pogingen tegelijk open en keur je er één goed, dan staat de andere op **Te beoordelen** onder **telt niet meer**. Die hoef je niet te beoordelen.
- **Wacht een inzending te lang?** Dan zet de bot een herinnering in het jurykanaal, met een link naar het bericht.

### Tips

- Reageer in de thread van het bericht als je de groep iets wilt laten weten, bijvoorbeeld waarom iets is afgekeurd.
- Wil je één opdracht voor alle groepen in één keer beoordelen, voor eerlijke Awesomes? Open de opdracht en zet **Alleen open inzendingen** aan. Met ← en → ga je naar de vorige en volgende opdracht, met **N** naar de volgende opdracht met open inzendingen.
- Twijfel je over een Awesome? Gebruik de ster ☆ op het dashboard om favorieten te markeren; die zijn alleen voor jou zichtbaar.

## Het dashboard

Het dashboard toont live de stand, alle inzendingen en de bestanden zelf. Je hoeft de pagina nooit te verversen: nieuwe inzendingen en beoordelingen verschijnen vanzelf.

### Inloggen

1. Open het adres van de app dat je van de organisatie kreeg.
2. Vul de toegangssleutel in. Hoofdletters en spaties maken niet uit.
3. Je browser blijft daarna 30 dagen ingelogd.

Na 5 verkeerde sleutels binnen een minuut moet je even wachten voordat je het opnieuw kunt proberen.

### De pagina's

| Pagina | Wat je er ziet en doet |
| --- | --- |
| **Dashboard** | Totalen, het scorebord met per groep wanneer ze voor het laatst iets instuurden, de punten in de loop van het spel en de voortgang per opdracht. In beheermodus ook **Exporteren**. |
| **Te beoordelen** | De open inzendingen, de langst wachtende eerst, met hoe lang ze al wachten. Klik om het bericht in Slack te openen. Inzendingen die geen punten meer kunnen opleveren staan apart onder **telt niet meer**. |
| **Inzendingen** | Alle inzendingen. Zoek en filter op groep, opdracht, status en jurylid, of toon alleen je favorieten. |
| **Opdrachten** | Alle opdrachten. Per opdracht zie je de inzendingen van alle groepen naast elkaar, desgewenst alleen de open inzendingen. Daar start je ook een diavoorstelling van die opdracht. |
| **Groepen** | Alle groepen. Per groep zie je score, plaats, de punten in de loop van het spel naast die van de andere groepen, en alle opdrachten. |
| **Bericht sturen** | Een bericht van de bot in de Slack-kanalen van de groepen. Zie [Bericht sturen](#bericht-sturen). |
| **Scorebord** | De totalen van de inzendingen en het scorebord in grote letters, met de punten in de loop van het spel, voor een beamer. Werkt zichzelf bij. |
| **Diavoorstelling** | Alle foto's, video's en geluidsopnames achter elkaar, bijvoorbeeld op een beamer. Afgekeurde inzendingen worden overgeslagen. Kies een groep, een opdracht, alleen awesome of alleen favorieten, herhalen, door elkaar, het aantal seconden per foto, en of het scorebord tussendoor komt. |
| **Gebeurtenissen** | Alles wat er live uit Slack binnenkomt, en wat de app ermee deed. Zie [Gebeurtenissen](#gebeurtenissen). |
| **Configuratie** | De instellingen en de juryleden. Voor de organisatie. |
| **Beheer** | Naar beheermodus gaan. Voor de organisatie. |

Lange lijsten op **Te beoordelen**, **Inzendingen** en **Gebeurtenissen** staan op pagina's van 50. Kies een ander filter, dan begin je weer op de eerste pagina.

Klik op een inzending voor de detailpagina: het bestand, wie het plaatste, het bericht, en de hele beoordelingsgeschiedenis met namen van juryleden.

### Zo wordt de score berekend

- Goedgekeurd geeft de standaardpunten van de opdracht, Awesome de awesome-punten. Open en Afgekeurd geven 0.
- Elke opdracht telt één keer per groep: de beste beoordeling van al haar inzendingen voor die opdracht.
- Groepen met evenveel punten delen een plaats.

### Gebeurtenissen

Op **Gebeurtenissen** zie je live alles wat Slack naar de app stuurt: nieuwe berichten, bewerkte en verwijderde berichten, en emoji's die worden geplaatst of weggehaald. Per gebeurtenis zie je de tijd, de groep, wie het deed, de tekst of emoji, en het resultaat:

- **Verwerkt**: de app heeft er iets mee gedaan, bijvoorbeeld een inzending geregistreerd of een beoordeling toegepast. Met de pijl open je de inzending.
- **Genegeerd**: er hoefde niets te gebeuren, bijvoorbeeld een bericht zonder bestand of een emoji van iemand die geen jurylid is. De toelichting zegt waarom.
- **Mislukt**: de app kon de gebeurtenis niet verwerken. Ook een bericht waarvan een bestand niet kon worden gedownload staat op **Mislukt**; de toelichting noemt het bestand.

Daarnaast staan hier, schuin gedrukt, meldingen van de app zelf:

- **Verbinding**: de verbinding met Slack is er (**Verwerkt**), is weggevallen of mislukt (**Mislukt**, met de reden), of wordt door Slack vernieuwd (**Info**). Is de laatste verbindingsmelding een fout, dan staat bovenaan de pagina een rode balk. Die verdwijnt zodra de app weer verbonden is.
- **Bijwerken**: na elke nieuwe verbinding leest de app terug wat hij miste. De toelichting zegt wat er is bijgewerkt, of dat er niets werd gemist. Kon een kanaal of bericht niet worden gelezen, dan staat de melding op **Mislukt**.
- **Fout in de app**: elke fout die de app tegenkomt, bijvoorbeeld een startbericht dat niet kon worden geplaatst, een mislukte herbouw, een volle schijf of een bestand dat ontbreekt.

Filter op soort, resultaat en groep. Met **Pauzeren** zet je de lijst stil om rustig te lezen; nieuwe gebeurtenissen worden geteld en verschijnen als je hervat.

De lijst toont 50 gebeurtenissen per pagina, de nieuwste eerst. Pauzeer als je door oudere pagina's bladert; anders schuiven nieuwe gebeurtenissen de lijst steeds een stukje op.

De pagina toont de laatste 500 gebeurtenissen sinds de app is gestart. Na een herstart begint de lijst leeg.

### Bericht sturen

Op **Bericht sturen** plaats je een bericht in de Slack-kanalen van de groepen, bijvoorbeeld "Lunch om 12:00 bij de kampvuurplaats". Elk jurylid kan dit. Het bericht verschijnt als de bot, niet onder je eigen naam.

1. Typ je bericht. Met de knoppen erboven maak je tekst **vet**, *schuin*, ~~doorgehaald~~ of `code`, voeg je een link toe, of maak je een opsomming. Ctrl+B en Ctrl+I werken ook. Wat je ziet is hoe het in Slack komt.
2. Met 🙂 kies je een emoji; zoeken kan in het Nederlands.
3. Met **Foto toevoegen** stuur je één foto mee (maximaal 20 MB). De tekst komt dan boven de foto; alleen een foto zonder tekst kan ook. Met het kruisje haal je de foto weer weg.
4. Rechts kies je de groepen. Standaard staan alle groepen aan; met **Niets** zet je ze allemaal uit, met **Alles** weer aan. Op de pagina van een groep brengt **Bericht sturen** je hier met alleen die groep aangevinkt.
5. Klik op **Sturen naar … kanalen** en daarna op **Zeker?** om te bevestigen. Per groep zie je of het bericht geplaatst is.

De bot moet lid zijn van het kanaal. Is hij dat niet, dan zie je bij die groep **Mislukt: `not_in_channel`**. Een bericht of foto van de bot telt nooit als inzending. Voor foto's heeft de bot het recht `files:write` nodig (zie [Slack koppelen](slack-koppelen.md)).

### Favorieten

Klik op de ster ☆ bij een inzending om hem als favoriet te markeren, bijvoorbeeld voor de prijsuitreiking. Favorieten zijn per browser en alleen voor jou zichtbaar. Er passen er maximaal 300 in. Een andere browser of een gewiste browser heeft dus geen favorieten.

### Meldingen

Komt er een nieuwe inzending binnen, dan verschijnt rechtsonder een melding. Je ziet welke groep hem instuurde, voor welke opdracht, hoeveel bestanden en door wie. Klik op de melding om de inzending te openen. Een melding verdwijnt na 8 seconden, of later als je de muis erop houdt of het tabblad niet open hebt.

Met de bel 🔔 rechtsboven zet je de meldingen uit of aan. Daar zet je ook een zacht geluidje aan; dat staat standaard uit. Deze keuze geldt alleen voor jouw browser. Na het herladen van de pagina speelt het geluid pas als je één keer ergens op de pagina hebt geklikt; zo werken browsers.

Er komen geen meldingen bij opnieuw opbouwen uit Slack, en niet in de diavoorstelling.

## Voor de organisatie: beheer

Beheertaken doe je in beheermodus. Daarvoor heb je de aparte beheerderssleutel nodig; de gewone toegangssleutel is genoeg om te kijken.

### Beheermodus

- **Aan:** ga naar **Beheer** en vul de beheerderssleutel in. Inloggen met de beheerderssleutel op het loginscherm kan ook.
- **Uit:** klik op **Beheermodus verlaten**.
- Zolang beheermodus aan staat, staat **Beheer** bovenaan de pagina.

### Sleutels

- Bij de eerste start maakt de app een toegangssleutel en een beheerderssleutel aan. Ze staan eenmalig in het logboek en worden bewaard in `access-keys.json`.
- Op de pagina **Beheer** maak je een nieuwe sleutel aan. Iedereen die met de oude sleutel is ingelogd, wordt direct uitgelogd. Jij blijft ingelogd.
- Een nieuwe beheerderssleutel wordt maar één keer getoond. Schrijf hem meteen op.

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

De aanpassing komt in de beoordelingsgeschiedenis. Slack blijft leidend: zet een jurylid later een emoji op het bericht, dan telt die emoji. Na **Opnieuw opbouwen uit Slack** verdwijnen handmatige aanpassingen. Bij het automatisch inhalen blijft een aanpassing staan zolang er in Slack geen beoordeling op het bericht staat.

### Handmatige modus

Zet **Handmatige modus** aan met de schakelaar op het **Dashboard** als de organisatie alles zelf wil beoordelen. De app slaat dan alleen nog bestanden op met de juiste naam in de map van de groep. Er komen geen inzendingen op het dashboard, emoji's worden genegeerd en de bot zegt niets in Slack. Bovenaan elke pagina staat dan **Handmatige modus**. Na een herstart geldt weer de instelling `Scouting:ManualMode`.

### Automatisch inhalen

Slack stuurt elk bericht en elke emoji maar één keer. Draaide de app even niet, of was de verbinding weg, dan haalt hij zelf in wat hij heeft gemist: bij het opstarten en bij elke nieuwe verbinding met Slack.

- De app leest de berichten van vandaag uit alle groepskanalen en registreert wat ontbreekt.
- Beoordelingen worden gelijkgezet met de emoji's die nu in Slack staan.
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
  _deleted/christiaan-de-wet/       ← bestanden van verwijderde berichten
  _backup/20261001-143000/          ← back-up van opnieuw opbouwen
```

## Draaiboek voor de speeldag

Doe de generale repetitie een paar dagen vóór het spel, op dezelfde server en in dezelfde Slack-workspace. Dan is de speeldag zelf vooral kijken.

### Een paar dagen vooraf

- [ ] Elk groepskanaal bestaat en de bot is lid van elk kanaal (`/invite @DU Review`).
- [ ] Alle juryleden staan op **Configuratie**, met hun naam.
- [ ] `opdrachtenpunten.csv` bevat alle opdrachten met de juiste punten.
- [ ] De tijdzone (`Scouting:TimeZone`) staat op `Europe/Amsterdam`.
- [ ] De starttijd (`Scouting:EventStart`) staat op het begin van het spel, bijvoorbeeld `"2026-10-03T09:00"`. Berichten van daarvoor tellen niet mee. Een testbericht van vóór de starttijd verschijnt dus niet op het dashboard. Op de starttijd plaatst de bot het startbericht in elk groepskanaal.
- [ ] Controleer de tekst van het startbericht: **Configuratie** > **Startbericht in Slack** > **Voorbeeld**. Pas hem zo nodig aan met het potlood (in beheermodus).
- [ ] De eindtijd (`Scouting:EventEnd`, of **Configuratie** > **Speeltijd** in beheermodus) staat op het einde van het spel. Inzendingen vanaf dat moment tellen niet; de bot reageert met :alarm_clock:. Op de eindtijd plaatst de bot het eindbericht in elk groepskanaal.
- [ ] Controleer de tekst van het eindbericht: **Configuratie** > **Eindbericht in Slack** > **Voorbeeld**.
- [ ] Er is genoeg schijfruimte: reken op enkele GB voor video's.
- [ ] Generale repetitie: vier mensen plaatsen tegelijk, één met een grote video (200 MB of meer). Twee juryleden zetten verschillende emoji's op één bericht. Iemand bewerkt een bericht en iemand verwijdert er een. Herstart de app halverwege en controleer dat niets ontbreekt.
- [ ] Toegangssleutel en beheerderssleutel staan veilig genoteerd.

### Op de ochtend van het spel

- [ ] Start de app en controleer bovenaan de pagina de Slack-status: **Verbonden met Slack**.
- [ ] Kijk op **Gebeurtenissen**: er staat **Verbonden met Slack** en een **Bijwerken**-melding, en geen **Fout in de app**. Houd deze pagina tijdens het spel ergens open.
- [ ] **Handmatige modus** staat uit (geen melding bovenaan).
- [ ] Plaats in elk kanaal een testbericht met een foto en `Opdracht 1`. Controleer dat het op het dashboard verschijnt, en verwijder het daarna in Slack.
- [ ] Deel de toegangssleutel met de jury en de mensen bij het scorebord.
- [ ] Zet de diavoorstelling of het dashboard klaar op de beamer.
- [ ] Na de starttijd: elk groepskanaal heeft het startbericht. Op **Configuratie** > **Startbericht in Slack** staat per groep **Geplaatst om …**. Mist er een, of is er geen starttijd ingesteld, klik dan in beheermodus op **Startbericht nu sturen**.
- [ ] Na de eindtijd: elk groepskanaal heeft het eindbericht (**Configuratie** > **Eindbericht in Slack**), en bovenaan de app staat **Spel afgelopen**.

### Uitleg aan de groepen (twee minuten)

- Plaats je opdracht in je eigen kanaal met het nummer erbij: `Opdracht 12`.
- Meerdere foto's in één bericht mag.
- Zet de bot een ❓, bewerk dan je bericht en zet het juiste nummer erin.
- Zet de bot een ⚠️, lees de uitleg in de thread.

### Uitleg aan de jury (twee minuten)

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
| De emoji van een jurylid doet niets | Staat het jurylid op **Configuratie**? Is het een beoordelings-emoji (zie de tabel bij de jury)? Staat **Handmatige modus** uit? |
| De status is anders dan het laatste emoji | Dat klopt als juryleden het oneens zijn: de beste beoordeling telt. Haal de emoji weg die niet meer moet tellen. |
| Bovenaan staat een rode melding **Slack: fout** of **Slack: verbinden** | De app heeft geen verbinding met Slack. Controleer het internet van de server en het logboek. Na herstel haalt de app vanzelf in wat hij miste. |
| De app is herstart of was een tijd weg | Wacht op **Verbonden met Slack**. De app haalt de berichten van vandaag vanzelf in. Ontbreekt er daarna nog iets, gebruik dan **Opnieuw opbouwen uit Slack**. |
| Iemand kan niet meer inloggen | Is de toegangssleutel net vernieuwd? Geef de nieuwe sleutel. Na 5 foute pogingen binnen een minuut even wachten. |
| Een handmatige aanpassing is verdwenen | Er is opnieuw opgebouwd uit Slack, of een jurylid heeft daarna een emoji gezet. Slack is leidend; zet de emoji in Slack. |
| Een video speelt niet af in het dashboard | Download het bestand via de detailpagina; niet elke browser speelt elk videoformaat af. |
| Een bestand staat in `_deleted` | Het bericht of bestand is in Slack verwijderd. Zet het terug door het opnieuw in Slack te plaatsen. |

Voor problemen met het koppelen van Slack zelf (tokens, rechten, Socket Mode) staat een uitgebreide tabel in [Slack koppelen](slack-koppelen.md#problemen-oplossen).
