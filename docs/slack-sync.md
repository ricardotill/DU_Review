# Slack-sync: hoe het werkt

Bron: [Claude Doc](https://claude.ai/code/artifact/5e7f69bb-3c35-4041-8553-e73751f0bd18); bijgewerkt 2026-10-02.

## Overzicht

Slack is leidend: wat in het groepskanaal staat, is de waarheid. De app leest berichten met bestanden als inzendingen en emoji van juryleden als beoordelingen. Gaat er iets mis, dan herstelt de app dat vanuit Slack, niet andersom.

- Elke groep heeft één Slack-kanaal voor de Toeristen. Met de Avonturiers aan heeft elke groep een tweede kanaal voor de Avonturiers; dat kanaal loopt op precies dezelfde manier (zelfde slot, catch-up, rebuild en mislukte events).
- Een bericht met een of meer bestanden wordt een inzending per bestand.
- Een emoji van een jurylid (ingesteld onder Reviewers) zet de status van die inzendingen.
- Wijzigingen op het dashboard (overrides) zijn tijdelijk: een volledige rebuild gooit ze weg.

## Live events

Slack stuurt elk event direct naar de app, via de webhook of via Socket Mode. Welke van de twee draait, staat in de configuratie.

```mermaid
flowchart LR
    slack["Slack-kanaal<br/>bericht, edit, delete, emoji"]
    webhook["Webhook<br/>fout = 500, Slack probeert opnieuw"]
    socket["Socket Mode<br/>direct bevestigd, wachtrij per kanaal"]
    dispatcher["Dispatcher<br/>slot per kanaal, wacht op rebuild"]
    out["/events + mislukte events"]
    slack --> webhook --> dispatcher
    slack --> socket --> dispatcher
    dispatcher --> out
```

- **Webhook**: de app verwerkt het event binnen het verzoek. Bij een fout antwoordt de app met 500 en probeert Slack het nog een paar keer.
- **Socket Mode**: de app bevestigt het event meteen en zet het in een wachtrij per kanaal. Bij een fout probeert Slack het niet opnieuw.
- **Volgorde**: events van één kanaal worden één voor één verwerkt. Catch-up en overrides wachten op hun beurt.
- **Dubbel**: een bestand wordt nooit twee keer opgeslagen. De app herkent het aan kanaal, bericht en bestand.

## Beoordelingen en overrides

Een emoji van een jurylid op het bericht bepaalt de status van alle inzendingen in dat bericht. Haalt het jurylid de emoji weg, dan gaat de status terug naar open.

- Alleen Slack-gebruikers in de lijst Reviewers tellen mee.
- Een **override** op het dashboard zet een status zonder emoji. De app noteert die als "Dashboard override". Net als een emoji geldt een override voor alle inzendingen van het bericht.
- Een emoji op een bericht zonder inzending (geen foto, verwijderd, of zonder geldig opdrachtnummer) wordt genegeerd, niet als fout bewaard. Krijgt het bericht later toch een inzending, dan neemt de catch-up de emoji mee.
- Catch-up en het opnieuw draaien van een event laten overrides staan.
- Een volledige rebuild gooit overrides weg; ze staan dan alleen nog in de back-up-JSON.
- Tijdens een rebuild kun je geen override zetten.

## Catch-up

De catch-up leest de kanalen van vandaag (vanaf middernacht) opnieuw en vult aan wat de app gemist heeft. Hij verwijdert niets en laat overrides staan.

- **Wanneer**: bij het starten van de app, en in Socket Mode bij elke herverbinding. Een admin kan hem ook zelf starten met **Catch-up nu draaien** op `/events`.
- **Herstelt**: gemiste inzendingen en emoji die niet bij de status passen.
- **Herstelt niet**: verwijderde berichten. Daarvoor zet je het mislukte event opnieuw in gang, of herbouw je de groep.
- **Al goedgekeurd** (🔒): de catch-up weigert alleen een bericht dat nog niet in de app staat én na de eerste goedgekeurde inzending van die opdracht is geplaatst. Berichten die al als inzending in de app staan, krijgen geen 🔒 en geen reactie van de bot.
- **Avonturiers**: de catch-up leest het Avonturiers-kanaal van een groep vóór het Toeristen-kanaal, zodat de vrijgespeelde opdrachten kloppen voordat de Toeristen-inzendingen worden gecontroleerd. In de melding staat zo'n kanaal als "{groep} (Avonturiers)".
- Het resultaat verschijnt als melding in de eventlog op `/events`.
- In handmatige modus draait de catch-up niet.

## Mislukte events

Elk event dat mislukt, wordt bewaard en staat bovenaan `/events` onder **Mislukte events**. Het blijft daar staan, ook na een herstart, tot het lukt of een admin het negeert.

| Knop | Wat het doet |
| --- | --- |
| Opnieuw | Draait dit ene event nog een keer. |
| Alles opnieuw | Draait alle open fouten, oudste eerst: eerst het bericht, dan de beoordeling ervan. |
| Negeren | Haalt de fout uit de lijst. |
| Catch-up nu draaien | Leest de kanalen van vandaag opnieuw; zie Catch-up. |

- **Bericht, bewerking, verwijdering**: de app draait het event precies zoals Slack het stuurde. Bestanden die al binnen zijn, worden niet dubbel opgeslagen. Lukt een bericht, dan leest de app ook de emoji die nu op dat bericht staan: een beoordeling die binnenkwam toen het bestand nog niet binnen was, telt zo alsnog.
- **Emoji**: de app draait het oude event niet na. Hij leest de emoji die nu in Slack op het bericht staan. Zo telt wat het jurylid inmiddels besloten heeft.
- Lukt het nog steeds niet, dan telt de kolom **Pogingen** op en staat de nieuwe fout erbij.
- Stuurt Slack hetzelfde event zelf nog eens en lukt het dan, dan verdwijnt de fout vanzelf.
- Tijdens een rebuild kun je geen event opnieuw draaien.

## Herstelladder

Begin bij de lichtste stap en ga pas verder als die niet helpt. Alleen de laatste twee stappen gooien gegevens weg.

1. **Event opnieuw** op `/events`. Voor één gemist bericht of één beoordeling. Overrides blijven staan.
2. **Catch-up nu draaien** op `/events`. Voor alles wat vandaag gemist is, ook berichten die niet als fout in de lijst staan (bijvoorbeeld tijdens een herstart). Overrides blijven staan; verwijderde berichten herstelt hij niet.
3. **Groep herbouwen** op de pagina van de groep. Leest de kanalen van één groep (Toeristen en Avonturiers) helemaal opnieuw in. Overrides van die groep gaan verloren.
4. **Volledige rebuild** onder Configuratie. Alleen als laatste redmiddel: alle overrides en handmatige beoordelingen gaan verloren. Ze staan nog in `submissions.json` in de back-upmap.

## Berichten van de bot bij herstel

Bij catch-up en rebuild plaatst de bot geen berichten opnieuw. Dat geldt ook voor de Avonturiers:

- **Vrijgespeelde opdrachten**: het aantal wordt uit de beoordelingen uitgerekend. Na een rebuild klopt het vanzelf, en de bot meldt het niet opnieuw in het Toeristen-kanaal.
- **Rondeberichten, startinformatie en eindlocatie**: welke al geplaatst zijn, staat in `runtime-settings.json`. Een rebuild verandert dat niet, dus elk kanaal krijgt elk bericht maar één keer. Opnieuw sturen kan met de hand op **Avonturiers → Rondes**.
- **Rondepunten**: worden uitgerekend uit de tijd van het Slack-bericht, dus een rebuild geeft dezelfde uitslag.

## Wie mag wat

Juryleden zien alles; alleen admins kunnen iets herstellen. Admin ben je met een beheerdersaccount (zie **Beheer** > **Accounts**).

| Actie | Jury | Admin |
| --- | --- | --- |
| `/events` en mislukte events bekijken | ja | ja |
| Event opnieuw, alles opnieuw, negeren | nee | ja |
| Catch-up nu draaien | nee | ja |
| Override op een inzending | nee | ja |
| Groep herbouwen, volledige rebuild | nee | ja |

Wordt een account geblokkeerd, verwijderd of geen beheerder meer, dan zijn open tabbladen van dat account binnen een minuut geen admin meer. De knoppen weigeren dan op de server.

## Checklist voor zaterdag

- [ ] Verbindingsstatus bovenin de app is groen.
- [ ] Jury-Slack-ID's staan allemaal onder Reviewers.
- [ ] Elke groep staat in de configuratie met het juiste kanaal, en met de Avonturiers aan ook het Avonturiers-kanaal.
- [ ] De bot is lid van alle kanalen, ook die van de Avonturiers.
- [ ] Minstens één coordinator per dienst heeft een beheerdersaccount.
- [ ] Tijdens het spel: `/events` open houden en de lijst Mislukte events in de gaten houden.
- [ ] Na een storing of herstart: eerst Catch-up nu draaien, dan Alles opnieuw.
- [ ] Geen volledige rebuild zonder overleg: overrides gaan verloren.
