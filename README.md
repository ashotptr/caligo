# **Caligo**
This project is a web platform that brings together a **blog**, a **media catalog**, a **live radio**, **chat rooms** and a **music AI**.

Everything runs in Docker containers and starts with one command.

### What you can do
* **Posts:** Read, write and schedule posts, clap for them, and get new posts by email.
* **Thesaurarium:** One catalog for films, series, episodes, actors, artists, albums and songs, with album covers, search, ratings, reviews, comments and personal collections.
* **Radio:** One live stream for every listener, with several ways to listen. Songs from the Thesaurarium can be queued to the live radio.
* **Chat Rooms:** Public chat rooms, called frequencies.
* **Omniscient AI:** Ask about music, artists or lyrics.
* **Ingest:** Signed-in users add albums from a Spotify link or a search.
* **Video:** Films and episodes are packaged for HLS and DASH and played in the browser.

### `src/caligo-client`
The website, a React app. It is the only front end.

### `src/posts`
The main API in ASP.NET Core. It handles accounts, posts and the Thesaurarium.

### `src/catalog` and `src/streamingservice`
The Thesaurarium and its database schema. `src/streamingservice` keeps the schema in MySQL and PostgreSQL versions, and the site runs on PostgreSQL.

### `src/caligo`
CaligoEngine: the C servers for HTTP, the radio and XMPP chat, the CGI apps, and the Python downloader and AI. It has its own README.

### `src/caligo-streaming`
Streaming servers in C: the radio relay (MP3, Opus and low latency) and the video delivery and packaging.

### `src/analytics` and `src/notifications`
Background services. Analytics counts post views, claps, video playback and radio listeners (Kafka, ClickHouse and MongoDB). Notifications sends emails about new posts (RabbitMQ).

### `deploy`
Docker Compose files for development, production and the lab, and the images for Caligo and Liquidsoap.

### Data
Everything the project stores lives in folders, not in Docker volumes:

* `data/dev` and `data/prod`: The databases and service state, one set per environment.
* `src/caligo`: Caligo's music (`public_html/music` for the radio, `public_html/mixtape` for albums) and its own state files.
* `media`: Videos. New files go in `media/incoming` and packaged ones are kept in `media/library`.

---

## Usage
### Dependencies
**Docker** with Compose (Docker Desktop on Windows and macOS).

The **.NET 8 SDK** and **Node.js 22** are only needed for running the tests or working on the code outside Docker.

Caligo's downloader and AI read their keys from `src/caligo/.env`: `SPOTIFY_CLIENT_ID`, `SPOTIFY_CLIENT_SECRET`, `OPENROUTER_API_KEY`, `GENIUS_ACCESS_TOKEN`, `LASTFM_API_KEY` and `LASTFM_API_SECRET`. The rest of the site works without them.

### Run

```
docker compose -f deploy/compose/docker-compose.yml up -d --build

```

The first build takes a while. Then open `http://localhost:8080`.

* The access code on the first screen is `caligo`.
* An admin account is created on the first start: `admin@localhost.dev` with the password `AdminDev04!`.
* Albums put in `src/caligo/public_html/mixtape` or added with Ingest show up in the Thesaurarium within a minute.
* To add a video, put the file in `media/incoming`, open the title as admin and press **Package**.

Other local addresses:

* API: `http://localhost:5028`
* Emails sent by the site (MailHog): `http://localhost:8025`
* Kafka UI: `http://localhost:8081`
* Traces and logs (Aspire dashboard): `http://localhost:18888`

### Stop

```
docker compose -f deploy/compose/docker-compose.yml down

```

### Work on the website
With the stack running, the website can run on its own with hot reload:

```
cd src/caligo-client
npm install
VITE_API_URL=http://localhost:5028 npm run dev

```

Then open `http://localhost:5173`.

### Tests

```
dotnet test Caligo.sln

```

The integration tests start their own databases with Testcontainers, so Docker must be running.

### Lab
* `bash deploy/lab/listen.sh up` starts the extra ways to listen to the radio on this machine (WebRTC, RTSP, RTMP, SRT, LL-HLS, QUIC, Icecast, DASH and HLS). `bash deploy/lab/listen.sh down` stops them.
* `bash deploy/lab/run-lab.sh` measures how fast each way delivers the radio. The results are on the Lab page for admins.

### Production
Production uses `docker-compose.prod.yml`, with its secrets in `deploy/compose/.env` (database passwords, the JWT key, the admin account, email settings and the Cloudflare Tunnel token). The site is published through the Cloudflare Tunnel.

```
cd deploy/compose
docker compose -f docker-compose.prod.yml --profile liquidsoap up -d --build

```
