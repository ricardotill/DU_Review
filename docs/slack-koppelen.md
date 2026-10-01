# De app koppelen aan de Slack-workspace

Deze handleiding beschrijft hoe je DU Review koppelt aan de Slack-workspace van het Destination Unknown regiospel. Na het koppelen:

- ontvangt de app elk bericht met een bestand in de groepskanalen en registreert het als inzending;
- verwerkt de app de emoji-reacties van de jury als beoordeling;
- reageert de bot zelf met een emoji en een antwoord in de thread als een inzending niet klopt.

## Kies een verbindingsmethode

De app kan Slack-berichten op twee manieren ontvangen:

| | **Socket Mode** (aanbevolen) | **Events API** |
|---|---|---|
| Hoe | De app maakt zelf een verbinding met Slack en houdt die open. | Slack stuurt elk bericht naar een adres van de app. |
| Publiek adres nodig | Nee. Werkt ook met alleen een IP-adres, achter een router of firewall. | Ja, een **HTTPS**-adres met een geldig certificaat. Een los IP-adres werkt niet. |
| Wat de app nodig heeft | Uitgaand internet naar `slack.com` (poort 443). | Een inkomende verbinding van Slack, via een domein of een tunnel. |
| Beveiliging | De verbinding wordt geopend met een geheim App-Level Token. | Elk verzoek wordt gecontroleerd met het Signing Secret. |

Draait de app op een machine met alleen een IP-adres? Kies dan **Socket Mode**.

Beide methoden verwerken berichten op precies dezelfde manier. Alleen de verbinding verschilt.

## Wat je nodig hebt

- Beheerrechten in de Slack-workspace (of iemand die apps mag goedkeuren).
- De Slack-kanalen van alle groepen, en de namen van alle juryleden.
- **Alleen voor de Events API:** een publiek bereikbaar HTTPS-adres voor de app, zodat Slack `https://<jouw-adres>/api/slack/events` kan bereiken. Lokaal testen kan met een tunnel, bijvoorbeeld `ngrok http 5231` of `cloudflared tunnel --url http://localhost:5231`.

## Stap 1: Slack-app aanmaken

1. Ga naar <https://api.slack.com/apps> en kies **Create New App** → **From a manifest**.
2. Kies de workspace van het regiospel.
3. Plak het manifest van de gekozen methode, en klik op **Create**.

### Manifest voor Socket Mode

```yaml
display_information:
  name: DU Review
  description: Verwerkt inzendingen voor het Destination Unknown regiospel
features:
  bot_user:
    display_name: DU Review
    always_online: true
oauth_config:
  scopes:
    bot:
      - channels:history   # berichten in openbare kanalen ontvangen
      - groups:history     # berichten in privékanalen ontvangen
      - files:read         # geüploade bestanden downloaden
      - reactions:read     # emoji-reacties van de jury ontvangen
      - users:read         # namen van juryleden ophalen
      - reactions:write    # zelf een emoji plaatsen bij een probleem
      - chat:write         # een antwoord in de thread plaatsen
settings:
  event_subscriptions:
    bot_events:
      - message.channels
      - message.groups
      - reaction_added
      - reaction_removed
  org_deploy_enabled: false
  socket_mode_enabled: true
  token_rotation_enabled: false
```

### Manifest voor de Events API

Gelijk aan het manifest hierboven, maar met een Request URL en zonder Socket Mode. Vervang het `settings`-deel door het deel hieronder, en vervang `JOUW-ADRES` door het HTTPS-adres van de app.

```yaml
settings:
  event_subscriptions:
    request_url: https://JOUW-ADRES/api/slack/events
    bot_events:
      - message.channels
      - message.groups
      - reaction_added
      - reaction_removed
  org_deploy_enabled: false
  socket_mode_enabled: false
  token_rotation_enabled: false
```

Slack controleert de Request URL meteen met een ondertekend `url_verification`-verzoek. De app moet dan al draaien **met** het Signing Secret. Lukt de controle nog niet? Doe dan eerst stap 2 tot en met 4, en vul daarna onder **Event Subscriptions** de Request URL opnieuw in.

## Stap 2: App installeren en geheimen kopiëren

1. Ga in de Slack-app naar **OAuth & Permissions** en klik op **Install to Workspace**. Keur de rechten goed.
2. Kopieer het **Bot User OAuth Token**. Het begint met `xoxb-`. Dit heb je bij beide methoden nodig: de app gebruikt het om bestanden te downloaden en om te reageren.
3. Daarnaast, afhankelijk van de methode:
   - **Socket Mode:** ga naar **Basic Information** → **App-Level Tokens** → **Generate Token and Scopes**. Geef het token een naam, voeg de scope `connections:write` toe en klik op **Generate**. Kopieer het token. Het begint met `xapp-`.
   - **Events API:** ga naar **Basic Information** → **App Credentials** en kopieer het **Signing Secret** (klik op **Show**).

Behandel tokens en het Signing Secret als wachtwoorden. Zet ze nooit in `appsettings.json` en nooit in Git.

## Stap 3: De app configureren

Alle instellingen staan onder `Scouting` in `src/ScoutingAutomation.Web/appsettings.json`. Na een wijziging moet je de app herstarten.

### Verbinding met Slack

Zet de geheimen als omgevingsvariabelen.

**Socket Mode:**

```bash
export Scouting__Slack__SimulationMode="false"
export Scouting__Slack__SocketMode="true"
export Scouting__Slack__BotToken="xoxb-..."
export Scouting__Slack__AppToken="xapp-..."
```

**Events API:**

```bash
export Scouting__Slack__SimulationMode="false"
export Scouting__Slack__SocketMode="false"
export Scouting__Slack__BotToken="xoxb-..."
export Scouting__Slack__SigningSecret="..."
```

Lokaal kun je ook *user secrets* gebruiken (eenmalig `init`), bijvoorbeeld voor Socket Mode:

```bash
dotnet user-secrets init --project src/ScoutingAutomation.Web
dotnet user-secrets set "Scouting:Slack:SimulationMode" "false" --project src/ScoutingAutomation.Web
dotnet user-secrets set "Scouting:Slack:SocketMode" "true" --project src/ScoutingAutomation.Web
dotnet user-secrets set "Scouting:Slack:BotToken" "xoxb-..." --project src/ScoutingAutomation.Web
dotnet user-secrets set "Scouting:Slack:AppToken" "xapp-..." --project src/ScoutingAutomation.Web
```

Met `SimulationMode` op `true` maakt de app geen verbinding met Slack, stuurt hij niets naar Slack en maakt hij nepbestanden aan. Voor een echte koppeling moet deze op `false`. De Slack-simulator in de app is dan uitgeschakeld.

#### Hoe de Events API wordt beveiligd

Bij de Events API controleert de app de handtekening van elk verzoek op `/api/slack/events`:

- verzoeken zonder geldige handtekening worden geweigerd (`401 Unauthorized`);
- verzoeken die ouder zijn dan 5 minuten worden ook geweigerd, zodat niemand een oud verzoek opnieuw kan afspelen;
- is er geen Signing Secret ingesteld, dan wordt **elk** verzoek geweigerd, en staat er bij het starten een foutmelding in het logboek.

Zo kan niemand nep-inzendingen of nep-beoordelingen sturen, ook niet als hij de URL kent.

Bij Socket Mode is deze controle niet nodig: de verbinding zelf is beveiligd met het App-Level Token. Het adres `/api/slack/events` blijft wel bestaan, maar weigert alles zolang er geen Signing Secret is.

### Groepen en kanalen

Elke groep hoort bij één Slack-kanaal. Het kanaal-ID vind je in Slack: open het kanaal, klik op de kanaalnaam bovenaan, en scroll in het venster naar beneden. Het ID begint met `C`.

```json
"Groups": [
  {
    "Name": "De Wolven",
    "Abbreviation": "CDW",
    "SlackChannelId": "C0123ABCDEF",
    "Description": "Welpen horde"
  }
]
```

`Abbreviation` wordt gebruikt in de bestandsnamen, bijvoorbeeld `CDW 16 14-32-05.jpg`. Geef elke groep een eigen afkorting.

### Jury

Alleen reacties van de mensen in `Reviewers` tellen mee als beoordeling. Berichten van deze mensen worden nooit als inzending gezien.

Slack stuurt geen namen maar **member-ID's**. Die vind je zo: open iemands profiel, klik op **⋮** en kies **Copy member ID**. Een ID begint met `U`.

```json
"Reviewers": [ "U04ABC123", "U05DEF456" ]
```

Juryleden kun je ook tijdens het spel toevoegen, zonder herstart: op de pagina **Configuration**, onder **Reviewers** (alleen in admin-modus). Die tellen meteen mee en blijven bewaard na een herstart (in `runtime-settings.json`, zie `Scouting:RuntimeSettingsFile`). Juryleden uit `appsettings.json` hebben daar een slotje en kun je alleen in dat bestand aanpassen.

#### Namen van juryleden

Een member-ID zegt weinig. Geef juryleden daarom een naam; die zie je dan overal in de app in plaats van het ID. Er zijn drie manieren, in deze volgorde van voorrang:

1. **Zelf invullen** op de pagina **Configuration**, bij het jurylid (potloodje). Een lege naam haalt hem weer weg.
2. **Ophalen uit Slack** met **Fetch names from Slack**. De app gebruikt de weergavenaam uit het Slack-profiel. Een naam die je zelf hebt ingevuld, wordt nooit overschreven. Hiervoor is het recht `users:read` nodig.
3. In `appsettings.json` onder `ReviewerNames`:

   ```json
   "ReviewerNames": { "U04ABC123": "Sanne", "U05DEF456": "Joris" }
   ```

Namen uit de app worden bewaard in `runtime-settings.json`.

Heb je de Slack-app al eerder aangemaakt? Voeg dan `users:read` toe onder **OAuth & Permissions** → **Bot Token Scopes**, en installeer de app opnieuw.

Haalt een jurylid een emoji weg, dan kijkt de app welke beoordelingen er nog op het bericht staan. De beste daarvan telt; is er geen meer, dan wordt de inzending weer **Pending**. Daarvoor is het event `reaction_removed` nodig (staat in de manifesten hierboven).

Heb je de Slack-app al eerder aangemaakt? Voeg dan `reaction_removed` toe onder **Event Subscriptions** → **Subscribe to bot events**, en sla op. Er zijn geen extra rechten nodig.

Bewerkte en verwijderde berichten komen binnen via dezelfde events `message.channels` en `message.groups`. Je hoeft daarvoor niets extra in te stellen. Voegt een groep het opdrachtnummer toe door het bericht te bewerken, dan haalt de bot zijn eigen ❓ weg. Daarvoor gebruikt hij het recht `reactions:write`, dat hij al heeft.

### Emoji's voor beoordelingen

Welke emoji welke beoordeling geeft, stel je in onder `ReviewStatusReactions`. Gebruik de Slack-naam van de emoji, zonder dubbele punten:

```json
"ReviewStatusReactions": {
  "Approved": [ "thumbsup", "+1", "white_check_mark", "heavy_check_mark" ],
  "Rejected": [ "x", "-1", "no_entry_sign" ],
  "Awesome": [ "heart", "star", "fire", "trophy" ]
}
```

De emoji's die de bot zelf plaatst (onder `SlackFeedback`) mogen niet in deze lijsten staan.

## Stap 4: App starten en bot uitnodigen

1. Start de app:

   ```bash
   dotnet run --project src/ScoutingAutomation.Web --launch-profile http
   ```

2. Controleer de verbinding:
   - **Socket Mode:** binnen een paar seconden staat bovenaan de app **Connected to Slack**. Staat er **Slack: error** of **Slack: connecting**, klik er dan op voor de reden. De app probeert het zelf opnieuw, met steeds langere pauzes (tot 30 seconden). Valt de internetverbinding even weg, dan maakt de app daarna zelf weer verbinding.
   - **Events API:** controleer in de Slack-app onder **Event Subscriptions** dat de Request URL **Verified** is.
     - Gebruik je een tunnel? Laat die naar `http://localhost:5231` wijzen, niet naar de https-poort. Anders stuurt de app de verzoeken door naar een adres dat Slack niet kan bereiken.
     - Draait de app achter een proxy? Die mag de inhoud van het verzoek niet aanpassen (bijvoorbeeld opnieuw formatteren of comprimeren). De handtekening klopt dan niet meer.
3. Nodig de bot uit in **elk groepskanaal**. Typ in het kanaal:

   ```text
   /invite @DU Review
   ```

   Zonder uitnodiging ontvangt de app geen berichten uit dat kanaal.

## Stap 5: Testen

1. Controleer op de pagina **Configuration** in de app:
   - de Slack-modus staat op **Live**;
   - bij **Connection** staat **Socket Mode** met **Connected**, of **Events API**;
   - bij Socket Mode staat het app-token op **set**; bij de Events API staat het Signing Secret op **set, requests are verified**;
   - het bot-token staat op **set**;
   - alle groepen hebben het juiste kanaal en een afkorting;
   - de jury staat onder **Reviewers**.
2. Plaats in een groepskanaal een foto met de tekst `Opdracht 1`. Binnen een paar seconden staat de inzending op het dashboard.
3. Reageer als jurylid met 👍 op het bericht. De status wordt **Approved** en het bestand verhuist naar de map `approved/`.
4. Plaats een foto met de tekst `Hallo`. De bot reageert met ❓ en vraagt in de thread om een opdrachtnummer. Er wordt niets opgeslagen.

## Herbouwen vanuit Slack

Draaide de app een tijd niet, of klopt er iets niet meer, dan kun je alle inzendingen opnieuw opbouwen uit de berichten in Slack. Ga naar **Configuration** → **Rebuild from Slack**, kies het tijdstip vanaf wanneer berichten meetellen, en typ `REBUILD`.

- Hiervoor zijn geen extra rechten nodig: `channels:history`, `groups:history` en `files:read` staan al in het manifest.
- De bot moet lid zijn van elk groepskanaal (zie stap 4). Anders stopt het herbouwen voordat er iets is veranderd. De app toont dan per kanaal dat niet gelezen kon worden: de groep, het kanaal-ID, de foutcode van Slack en wat je eraan kunt doen.
- Alleen in admin-modus.
- Het werkt alleen met `SimulationMode` op `false` en een bot-token.
- Alle huidige bestanden en een `submissions.json` gaan eerst naar `scouting_storage/_backup/{tijdstip}/`.
- Er wordt niets in Slack geplaatst. Berichten en reacties die tijdens het herbouwen binnenkomen, wachten tot het klaar is.
- Slack beperkt hoe vaak berichten opgevraagd mogen worden. Wordt die grens bereikt, dan wacht de app zo lang als Slack aangeeft en gaat daarna verder. Voor een regiospel van één dag duurt herbouwen vooral zo lang als het opnieuw downloaden van alle bestanden.

Zie de [README](../README.md#herbouwen-vanuit-slack) voor wat er precies gebeurt en wat verloren gaat.

## Problemen oplossen

### Beide methoden

| Probleem | Oplossing |
|---|---|
| Berichten komen niet binnen | Is de bot uitgenodigd in het kanaal? Klopt het kanaal-ID in `Groups`? |
| Reacties van de jury doen niets | Staat het member-ID (`U...`) van het jurylid onder **Reviewers** (in `appsettings.json` of toegevoegd in de app)? Staat de emoji in `ReviewStatusReactions`? Staat **Manual mode** uit? |
| Een weggehaalde emoji verandert niets | Staat `reaction_removed` bij de bot events van de Slack-app? Kan de app de reacties lezen (recht `reactions:read`)? Zie het logboek: kan de app ze niet lezen, dan blijft de status zoals hij was. |
| De bot reageert niet zelf | Staat `SimulationMode` op `false` en is het bot-token ingesteld? Heeft de app de rechten `reactions:write` en `chat:write`? Zie het logboek van de app. |
| De bot reageert met ⚠️ dat een bestand niet kon worden opgehaald | De download uit Slack mislukte. Heeft de app het recht `files:read`? Installeer de app opnieuw na het toevoegen van rechten. |
| Herbouwen stopt met `not_in_channel` bij een kanaal | De bot is niet uitgenodigd in dat groepskanaal. Nodig hem uit (stap 4) en start opnieuw. |
| Herbouwen stopt met `channel_not_found` bij een kanaal | Het kanaal-ID in `Groups` klopt niet, of het kanaal is privé en de bot is er geen lid van. |
| Herbouwen stopt met `missing_scope` | De app mist `channels:history` of `groups:history`. Voeg de rechten toe en installeer de app opnieuw. |
| Herbouwen stopt met `invalid_auth` of `token_revoked` | Het bot-token klopt niet (meer). Kopieer het opnieuw uit **OAuth & Permissions**. |
| Namen ophalen geeft `missing_scope` | De app mist `users:read`. Voeg het recht toe en installeer de app opnieuw. |
| Namen ophalen: "Slack does not know …" | Het member-ID klopt niet. Kopieer het opnieuw via **Copy member ID**. |
| **Fetch names from Slack** ontbreekt | Namen ophalen werkt alleen met `SimulationMode` op `false` en een bot-token. |
| Een bericht zonder bestand wordt genegeerd | Dat klopt: alleen berichten met een bestand zijn inzendingen. |

Na het toevoegen van rechten (scopes) moet je de Slack-app altijd opnieuw installeren in de workspace.

### Socket Mode

| Probleem | Oplossing |
|---|---|
| **Slack: error** met `App token not set` | Stel `Scouting:Slack:AppToken` in. Zie stap 3. |
| **Slack: error** met `invalid_auth` | Het App-Level Token klopt niet. Kopieer het opnieuw; het moet met `xapp-` beginnen. |
| **Slack: error** met `not_allowed_token_type` | Je hebt het bot-token (`xoxb-`) als App-Level Token ingevuld. Gebruik het `xapp-`-token. |
| **Slack: error** met een andere Slack-foutcode | Staat Socket Mode aan in de Slack-app (**Settings** → **Socket Mode**)? Heeft het App-Level Token de scope `connections:write`? |
| Blijft op **connecting** hangen | De machine kan `slack.com` niet bereiken. Controleer of uitgaand verkeer over poort 443 is toegestaan. |

### Events API

| Probleem | Oplossing |
|---|---|
| Request URL wordt niet geverifieerd | De app draait niet op dat adres, of is niet via HTTPS bereikbaar. Controleer de tunnel of het domein. Draait de app wel, kijk dan in het logboek naar `Rejected Slack request` (zie hieronder). |
| Logboek: `Rejected Slack request … MissingSigningSecret` | Er is geen Signing Secret ingesteld. Zie stap 3. |
| Logboek: `Rejected Slack request … InvalidSignature` | Het Signing Secret klopt niet (let op spaties bij het kopiëren), of een proxy past de inhoud van het verzoek aan. |
| Logboek: `Rejected Slack request … StaleTimestamp` | De klok van de server loopt meer dan 5 minuten voor of achter. Zet tijdsynchronisatie (NTP) aan. |
| Logboek: `Rejected Slack request … MissingHeaders` | Het verzoek kwam niet van Slack, of een proxy haalt de headers `X-Slack-Signature` en `X-Slack-Request-Timestamp` weg. |
| Een inzending ontbreekt, zonder bot-reactie | Kijk in het logboek. Slack verwacht binnen 3 seconden antwoord en stuurt het bericht anders opnieuw. Dubbele inzendingen worden wel herkend en overgeslagen. |
