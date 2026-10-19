settings.log.stdout.set(true)
settings.log.file.set(false)

relay_host = environment.get(default="relay", "RELAY_HOST")
relay_port = int_of_string(environment.get(default="8000", "RELAY_PORT"))
password = environment.get("RELAY_SOURCE_PASSWORD")
radio_url = environment.get(default="http://caligo:9001/radio/", "RADIO_URL")

if password == "" then
  log.critical(label="caligo", "RELAY_SOURCE_PASSWORD is empty, the relay would refuse the stream")
  exit(1)
end

radio = source.drop.metadata(input.http(max_buffer=10., radio_url))

output.icecast(
  %ogg(%opus(bitrate=96, samplerate=48000, channels=2)),
  host=relay_host,
  port=relay_port,
  user="source",
  password=password,
  mount="/live/opus",
  method="put",
  name="Caligo Radio",
  description="Caligo radio as Ogg Opus",
  mksafe(radio)
)
